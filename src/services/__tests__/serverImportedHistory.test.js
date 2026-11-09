import { DatabaseSync } from 'node:sqlite';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const transport = vi.hoisted(() => ({
    connection: null,
    statements: [],
    afterRead: null,
    watchState: { isLoggedIn: false }
}));

// Exercise the original repositories and their SQL against real SQLite. Only
// the host transport is replaced; no VRChat account or HTTP request is involved.
vi.mock('../sqlite', () => ({
    default: {
        async execute(callback, sql, args) {
            transport.statements.push(sql);
            const statement = transport.connection.prepare(sql);
            statement.setReturnArrays(true);
            const rows = args ? statement.all(args) : statement.all();
            if (transport.afterRead) await transport.afterRead(sql);
            for (const row of rows) callback(row);
        },
        async executeNonQuery(sql, args) {
            transport.statements.push(sql);
            const statement = transport.connection.prepare(sql);
            return (args ? statement.run(args) : statement.run()).changes;
        },
        async executeInsert(sql, args) {
            transport.statements.push(sql);
            return Number(transport.connection.prepare(sql).run(args).lastInsertRowid);
        }
    }
}));
vi.mock('../../web/platform.js', () => ({ isWebPlatform: false }));
vi.mock('../../web/webDatabase.js', () => ({ webDatabase: {} }));
vi.mock('../../web/webConfigRepository.js', () => ({ webConfigRepository: {} }));
vi.mock('../runtimeCapabilities', () => ({ runtimeCapabilities: { serverMode: true } }));
vi.mock('../watchState', () => ({ watchState: transport.watchState }));
vi.mock('../../api', () => ({ groupRequest: {}, queryRequest: {}, userRequest: {}, worldRequest: {} }));
vi.mock('../../coordinators/avatarCoordinator', () => ({ lookupAvatars: vi.fn() }));
vi.mock('../serverEntityResourceControl', () => ({ entityResource: vi.fn() }));

import { database, dbVars } from '../database';
import configRepository from '../config';
import { dispatchServerReadControlRequest } from '../serverReadControl';
import { recordAuthenticatedDataOwner } from '../serverDataOwner';

const owner = 'usr_11111111-1111-4111-8111-111111111111';
const otherOwner = 'usr_22222222-2222-4222-8222-222222222222';
const friend = 'usr_33333333-3333-4333-8333-333333333333';
const stores = {
    user: { currentUser: { id: '' } },
    friend: { friends: new Map(), localFavoriteFriends: new Map() }
};
const prefix = (id) => id.replaceAll('-', '').replaceAll('_', '');
const read = (name, args = {}) =>
    dispatchServerReadControlRequest(stores, { operation: 'database-read', payload: { name, arguments: args } });
const readFeed = () => read('lookupFeedDatabase', { filters: ['GPS'], favoritesOnly: false, maxEntries: 50 });

beforeEach(async () => {
    transport.connection = new DatabaseSync(':memory:');
    transport.statements = [];
    transport.afterRead = null;
    transport.watchState.isLoggedIn = false;
    stores.user.currentUser = { id: '' };
    await configRepository.init();
    await database.initTables();
    for (const [id, label] of [
        [owner, 'Imported'],
        [otherOwner, 'Other account']
    ]) {
        await database.initUserTables(id);
        transport.connection
            .prepare(`INSERT INTO ${prefix(id)}_feed_gps
            (created_at, user_id, display_name, location, world_name) VALUES (?, ?, ?, ?, ?)`)
            .run('2026-11-09T00:00:00Z', friend, `${label} friend`, 'wrld_fixture:123', `${label} world`);
        transport.connection
            .prepare(`INSERT INTO ${prefix(id)}_friend_log_history
            (created_at, type, user_id, display_name, previous_display_name, friend_number) VALUES (?, ?, ?, ?, ?, ?)`)
            .run('2026-11-09T00:00:00Z', 'DisplayName', friend, `${label} friend`, `${label} old name`, 7);
        transport.connection
            .prepare(`INSERT INTO gamelog_join_leave
            (created_at, type, display_name, location, user_id, time) VALUES (?, ?, ?, ?, ?, ?)`)
            .run('2026-11-09T00:00:00Z', 'OnPlayerLeft', `${label} owner`, `wrld_${prefix(id)}:123`, id, 60_000);
    }
    // This metadata is created by the trusted import transaction, not supplied
    // as an account selector in a browser request.
    transport.connection.exec(`CREATE TABLE vrcx_web_data_owner (
        id INTEGER PRIMARY KEY CHECK (id = 1), user_id TEXT NOT NULL, imported_at TEXT NOT NULL
    )`);
    transport.connection.prepare('INSERT INTO vrcx_web_data_owner VALUES (1, ?, ?)').run(owner, '2026-11-09T00:00:00Z');
    await configRepository.setString('lastUserLoggedIn', owner);
    dbVars.userId = '';
    dbVars.userPrefix = '';
});

afterEach(() => {
    dbVars.userId = '';
    dbVars.userPrefix = '';
    transport.connection.close();
});

describe('imported native history without a VRChat cookie', () => {
    it('restores the account for activity analysis when it is the first native history read', async () => {
        expect(await read('getDateOfInstanceActivity')).toMatchObject({ ok: true, result: ['2026-11-09T00:00:00Z'] });
        expect(
            await read('getInstanceActivity', { startDate: '2026-11-08T00:00:00Z', endDate: '2026-11-10T00:00:00Z' })
        ).toMatchObject({
            ok: true,
            result: { currentUserData: [{ user_id: owner, display_name: 'Imported owner' }] }
        });
        expect(transport.watchState.isLoggedIn).toBe(false);
    });

    it('reads the imported account through the original Feed and Friend Log SQL before login', async () => {
        const feed = await readFeed();
        const history = await read('getFriendLogHistory');
        expect(feed).toMatchObject({
            ok: true,
            result: [{ displayName: 'Imported friend', worldName: 'Imported world' }]
        });
        expect(history).toMatchObject({
            ok: true,
            result: [{ displayName: 'Imported friend', previousDisplayName: 'Imported old name', friendNumber: 7 }]
        });
        expect(transport.watchState.isLoggedIn).toBe(false);
        expect(stores.user.currentUser.id).toBe('');
        expect(transport.statements.some((sql) => /FROM _(?:feed|friend_log)/.test(sql))).toBe(false);
    });

    it('retains native history after session logout clears lastUserLoggedIn and the read context restarts', async () => {
        await readFeed();
        await configRepository.remove('lastUserLoggedIn');
        dbVars.userId = '';
        dbVars.userPrefix = '';
        expect(await readFeed()).toMatchObject({ ok: true, result: [{ displayName: 'Imported friend' }] });
        expect(await read('getFriendLogHistory')).toMatchObject({
            ok: true,
            result: [{ previousDisplayName: 'Imported old name' }]
        });
    });

    it('keeps the native context of a subsequently authenticated account when serving history', async () => {
        await readFeed();
        await database.initUserTables(otherOwner);
        stores.user.currentUser = { id: otherOwner };
        transport.watchState.isLoggedIn = true;
        expect(await readFeed()).toMatchObject({ ok: true, result: [{ displayName: 'Other account friend' }] });
        expect(dbVars.userId).toBe(otherOwner);
        await database.addGPSToDatabase({
            created_at: '2026-11-09T01:00:00Z',
            userId: friend,
            displayName: 'Live account friend',
            location: 'wrld_fixture:456',
            worldName: 'Live account world',
            previousLocation: '',
            time: 0,
            groupName: ''
        });
        expect(transport.connection.prepare(`SELECT COUNT(*) FROM ${prefix(owner)}_feed_gps`).get()['COUNT(*)']).toBe(
            1
        );
        expect(
            transport.connection.prepare(`SELECT COUNT(*) FROM ${prefix(otherOwner)}_feed_gps`).get()['COUNT(*)']
        ).toBe(2);
    });

    it('retains the new authenticated owner after logout and a worker read-context restart', async () => {
        await database.initUserTables(otherOwner);
        stores.user.currentUser = { id: otherOwner };
        transport.watchState.isLoggedIn = true;
        await recordAuthenticatedDataOwner(otherOwner);
        await configRepository.remove('lastUserLoggedIn');
        stores.user.currentUser = { id: '' };
        transport.watchState.isLoggedIn = false;
        dbVars.userId = '';
        dbVars.userPrefix = '';

        expect(await readFeed()).toMatchObject({ ok: true, result: [{ displayName: 'Other account friend' }] });
        expect(await read('getFriendLogHistory')).toMatchObject({
            ok: true,
            result: [{ previousDisplayName: 'Other account old name' }]
        });
    });

    it('does not overwrite a login context with an imported owner read that completed late', async () => {
        let releaseOwner;
        let ownerSelected;
        const selected = new Promise((resolve) => {
            ownerSelected = resolve;
        });
        const release = new Promise((resolve) => {
            releaseOwner = resolve;
        });
        transport.afterRead = async (sql) => {
            if (/SELECT user_id FROM vrcx_web_data_owner/.test(sql)) {
                ownerSelected();
                await release;
            }
        };
        const pending = readFeed();
        try {
            await selected;
            await database.initUserTables(otherOwner);
            stores.user.currentUser = { id: otherOwner };
            transport.watchState.isLoggedIn = true;
        } finally {
            transport.afterRead = null;
            releaseOwner();
        }

        expect(await pending).toMatchObject({ ok: true, result: [{ displayName: 'Other account friend' }] });
        expect(dbVars.userId).toBe(otherOwner);
    });

    it('keeps global game logs and memos readable even when no account owner exists', async () => {
        transport.connection.exec('DELETE FROM vrcx_web_data_owner');
        await configRepository.remove('lastUserLoggedIn');
        expect(
            await read('lookupGameLogDatabase', { filters: ['Location'], favoritesOnly: false, maxEntries: 50 })
        ).toMatchObject({ ok: true, result: [] });
        expect(await read('getWorldMemo', { worldId: 'wrld_fixture' })).toMatchObject({ ok: true });
    });

    it('rejects an invalid dedicated owner instead of using it in an SQL table name', async () => {
        transport.connection.prepare('UPDATE vrcx_web_data_owner SET user_id = ?').run("usr'; DROP TABLE configs; --");
        expect(await readFeed()).toMatchObject({ ok: false, errorCode: 'data_owner_unavailable' });
        expect(dbVars.userPrefix).toBe('');
        expect(transport.connection.prepare("SELECT name FROM sqlite_schema WHERE name = 'configs'").get().name).toBe(
            'configs'
        );
    });
});
