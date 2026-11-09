import { DatabaseSync } from 'node:sqlite';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const mocks = vi.hoisted(() => ({
    database: null,
    execute: vi.fn(),
    executeTransaction: vi.fn(),
    snapshotQueue: [],
    intervalCallback: null,
    failNotificationTransaction: false,
    projectionTransactionsCompleted: 0,
    dbVars: { userPrefix: 'usrnotifications' },
    watchState: {
        isLoggedIn: true,
        isFriendsLoaded: true,
        isFavoritesLoaded: true
    }
}));

vi.mock('../sqlite', () => ({
    default: {
        execute: mocks.execute,
        executeTransaction: mocks.executeTransaction
    }
}));

vi.mock('../runtimeCapabilities', () => ({
    runtimeCapabilities: { serverMode: true }
}));

vi.mock('../watchState', () => ({
    watchState: mocks.watchState
}));

vi.mock('../syncTelemetry', () => ({
    syncTelemetry: {
        state: 'healthy',
        lastSuccessAt: '2026-01-01T00:00:00.000Z',
        lastAttemptAt: '2026-01-01T00:00:00.000Z',
        nextRetryAt: null,
        errorCode: null
    }
}));

vi.mock('../websocket', () => ({
    wsState: {
        connected: true,
        connecting: false,
        connectedAt: '2026-01-01T00:00:00.000Z',
        lastEventAt: null,
        lastAttemptAt: '2026-01-01T00:00:00.000Z',
        nextRetryAt: null,
        errorCode: null
    }
}));

vi.mock('../serverProjection', () => ({
    buildServerProjectionSnapshot: vi.fn(() => mocks.snapshotQueue.shift())
}));

vi.mock('../database', () => ({
    dbVars: mocks.dbVars
}));

vi.mock('worker-timers', () => ({
    setInterval: vi.fn((callback) => {
        mocks.intervalCallback = callback;
        return 1;
    }),
    clearInterval: vi.fn()
}));

let startServerProjectionPublisher;
let stopPublisher;

function snapshot() {
    return {
        currentUserId: 'usr_notifications',
        users: [],
        entities: [],
        favorites: [],
        favoriteGroups: [],
        favoriteFingerprint: 'usr_notifications:empty-favorites',
        fingerprint: 'notification-regression-domain'
    };
}

function stores() {
    return {
        auth: {
            loginForm: { loading: false },
            attemptingAutoLogin: false
        },
        friend: {
            isRefreshFriendsLoading: false
        }
    };
}

function notificationEvents() {
    return mocks.database
        .prepare(`SELECT type, resource, payload_json
            FROM vrcx_web_event_outbox
            WHERE resource = 'notifications'
            ORDER BY sequence`)
        .all();
}

function notificationRevision() {
    return mocks.database
        .prepare(`SELECT revision FROM vrcx_web_notification_revision WHERE user_prefix = 'usrnotifications'`)
        .get()?.revision;
}

function observedRevision() {
    return mocks.database
        .prepare(`SELECT observed_revision FROM vrcx_web_notification_projection_state
            WHERE user_prefix = 'usrnotifications'`)
        .get()?.observed_revision;
}

async function publishAgain() {
    const completed = mocks.projectionTransactionsCompleted;
    mocks.snapshotQueue.push(snapshot());
    mocks.intervalCallback();
    await vi.waitFor(() => expect(mocks.projectionTransactionsCompleted).toBe(completed + 1));
}

async function resetHarness({ includeV2 = true } = {}) {
    stopPublisher?.();
    stopPublisher = undefined;
    mocks.database?.close();
    vi.resetModules();
    mocks.database = new DatabaseSync(':memory:');
    mocks.database.exec(`
        CREATE TABLE usrnotifications_notifications (id TEXT PRIMARY KEY, expired INTEGER NOT NULL DEFAULT 0);
        ${
            includeV2
                ? `CREATE TABLE usrnotifications_notifications_v2 (id TEXT PRIMARY KEY, seen INTEGER NOT NULL DEFAULT 0);
                   INSERT INTO usrnotifications_notifications_v2 (id, seen) VALUES ('not_v2', 0);`
                : ''
        }
    `);
    mocks.execute.mockReset();
    mocks.execute.mockImplementation(async (callback, sql, args) => {
        const statement = mocks.database.prepare(sql);
        const rows = args ? statement.all(args) : statement.all();
        for (const row of rows) callback(Object.values(row));
    });
    mocks.executeTransaction.mockReset();
    mocks.executeTransaction.mockImplementation(async (operations) => {
        let affectedRows = 0;
        const isProjectionTransaction = operations.some((operation) =>
            operation.sql.includes('INSERT INTO vrcx_web_projection_state (')
        );
        mocks.database.exec('BEGIN');
        try {
            for (const operation of operations) {
                const statement = mocks.database.prepare(operation.sql);
                const result = operation.args ? statement.run(operation.args) : statement.run();
                affectedRows += Number(result.changes || 0);
            }
            if (
                mocks.failNotificationTransaction &&
                operations.some((operation) => operation.args?.['@resource'] === 'notifications')
            ) {
                throw new Error('intentional notification publication rollback');
            }
            mocks.database.exec('COMMIT');
            return affectedRows;
        } catch (error) {
            mocks.database.exec('ROLLBACK');
            throw error;
        } finally {
            if (isProjectionTransaction) mocks.projectionTransactionsCompleted++;
        }
    });
    mocks.snapshotQueue.length = 0;
    mocks.snapshotQueue.push(snapshot());
    mocks.intervalCallback = null;
    mocks.failNotificationTransaction = false;
    mocks.projectionTransactionsCompleted = 0;
    mocks.dbVars.userPrefix = 'usrnotifications';
    mocks.watchState.isLoggedIn = true;
    mocks.watchState.isFriendsLoaded = true;
    mocks.watchState.isFavoritesLoaded = true;
    ({ startServerProjectionPublisher } = await import('../serverProjectionPublisher'));
    stopPublisher = await startServerProjectionPublisher(stores());
    mocks.database.exec('DELETE FROM vrcx_web_event_outbox');
}

beforeEach(async () => {
    await resetHarness();
});

afterEach(() => {
    stopPublisher?.();
    stopPublisher = undefined;
    mocks.database?.close();
    mocks.database = null;
    vi.restoreAllMocks();
});

describe('serverProjectionPublisher notification revisions', () => {
    it('emits once for committed v1 and v2 notification mutations, but not duplicate polls', async () => {
        expect(notificationRevision()).toBe(0);
        expect(observedRevision()).toBe(0);

        mocks.database.prepare(`INSERT INTO usrnotifications_notifications (id) VALUES ('not_v1')`).run();
        await publishAgain();
        expect(notificationRevision()).toBe(1);
        expect(notificationEvents()).toEqual([{ type: 'data', resource: 'notifications', payload_json: '{}' }]);

        await publishAgain();
        expect(notificationEvents()).toHaveLength(1);

        mocks.database.prepare(`UPDATE usrnotifications_notifications_v2 SET seen = 1 WHERE id = 'not_v2'`).run();
        await publishAgain();
        expect(notificationRevision()).toBe(2);
        expect(notificationEvents()).toHaveLength(2);

        mocks.database.prepare(`DELETE FROM usrnotifications_notifications_v2 WHERE id = 'not_v2'`).run();
        await publishAgain();
        expect(notificationRevision()).toBe(3);
        expect(notificationEvents()).toHaveLength(3);

        await publishAgain();
        expect(notificationEvents()).toHaveLength(3);
        expect(observedRevision()).toBe(3);
    });

    it('does not observe rolled-back source writes and republishes after a failed outbox transaction', async () => {
        mocks.database.exec('BEGIN');
        mocks.database.prepare(`INSERT INTO usrnotifications_notifications (id) VALUES ('not_rolled_back')`).run();
        mocks.database.exec('ROLLBACK');
        expect(notificationRevision()).toBe(0);

        await publishAgain();
        expect(notificationEvents()).toEqual([]);

        mocks.database.prepare(`INSERT INTO usrnotifications_notifications (id) VALUES ('not_committed')`).run();
        expect(notificationRevision()).toBe(1);
        const error = vi.spyOn(console, 'error').mockImplementation(() => {});
        mocks.failNotificationTransaction = true;
        await publishAgain();
        expect(error).toHaveBeenCalledOnce();
        expect(notificationEvents()).toEqual([]);
        expect(observedRevision()).toBe(0);

        mocks.failNotificationTransaction = false;
        await publishAgain();
        expect(notificationEvents()).toEqual([{ type: 'data', resource: 'notifications', payload_json: '{}' }]);
        expect(observedRevision()).toBe(1);
    });

    it('starts with legacy-only data, then tracks v2 after that table is created', async () => {
        await resetHarness({ includeV2: false });
        expect(notificationRevision()).toBeUndefined();
        expect(observedRevision()).toBeUndefined();

        mocks.database.exec(`
            CREATE TABLE usrnotifications_notifications_v2 (id TEXT PRIMARY KEY, seen INTEGER NOT NULL DEFAULT 0);
            INSERT INTO usrnotifications_notifications_v2 (id, seen) VALUES ('not_added_later', 0);
        `);
        await publishAgain();
        expect(notificationRevision()).toBe(0);
        expect(notificationEvents()).toEqual([{ type: 'data', resource: 'notifications', payload_json: '{}' }]);

        mocks.database
            .prepare(`UPDATE usrnotifications_notifications_v2 SET seen = 1 WHERE id = 'not_added_later'`)
            .run();
        await publishAgain();
        expect(notificationRevision()).toBe(1);
        expect(notificationEvents()).toHaveLength(2);
        expect(observedRevision()).toBe(1);
    });
});
