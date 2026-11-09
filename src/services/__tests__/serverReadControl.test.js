import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const mocks = vi.hoisted(() => ({
    watchState: { isLoggedIn: true },
    getUsers: vi.fn(),
    getWorlds: vi.fn(),
    groupSearch: vi.fn(),
    getUser: vi.fn(),
    getWorld: vi.fn(),
    getAvatar: vi.fn(),
    getGroup: vi.fn(),
    lookupAvatars: vi.fn(),
    getDateOfInstanceActivity: vi.fn(),
    getInstanceActivity: vi.fn(),
    getHotWorlds: vi.fn(),
    getHotWorldFriendDetail: vi.fn(),
    getMutualGraphSnapshot: vi.fn(),
    getMutualGraphMeta: vi.fn()
}));

vi.mock('../../api', async () => ({
    queryRequest: (await import('../../api/queryRequest')).default,
    userRequest: {
        getUsers: (...args) => mocks.getUsers(...args),
        getUser: (...args) => mocks.getUser(...args)
    },
    worldRequest: {
        getWorlds: (...args) => mocks.getWorlds(...args),
        getWorld: (...args) => mocks.getWorld(...args)
    },
    avatarRequest: {
        getAvatar: (...args) => mocks.getAvatar(...args)
    },
    groupRequest: {
        groupSearch: (...args) => mocks.groupSearch(...args),
        getGroup: (...args) => mocks.getGroup(...args)
    }
}));

// Keep the original queryRequest, policies, keys and QueryClient in this test.
// Only the transport is replaced, so repeated reads exercise the real cache.
vi.mock('../../api/user', () => ({ default: { getUser: (...args) => mocks.getUser(...args) } }));
vi.mock('../../api/world', () => ({ default: { getWorld: (...args) => mocks.getWorld(...args) } }));
vi.mock('../../api/avatar', () => ({ default: { getAvatar: (...args) => mocks.getAvatar(...args) } }));
vi.mock('../../api/group', () => ({ default: { getGroup: (...args) => mocks.getGroup(...args) } }));
vi.mock('../../api/favorite', () => ({ default: {} }));
vi.mock('../../api/instance', () => ({ default: {} }));
vi.mock('../../api/inventory', () => ({ default: {} }));
vi.mock('../../api/marketplace', () => ({ default: {} }));
vi.mock('../../api/misc', () => ({ default: {} }));
vi.mock('../appConfig', () => ({ logWebRequest: vi.fn(), withQueryLog: (work) => work() }));

vi.mock('../../coordinators/avatarCoordinator', () => ({
    lookupAvatars: (...args) => mocks.lookupAvatars(...args)
}));
vi.mock('../serverDataOwner', () => ({ readServerDataOwner: vi.fn().mockResolvedValue(null) }));

vi.mock('../database', () => ({
    database: {
        getDateOfInstanceActivity: (...args) => mocks.getDateOfInstanceActivity(...args),
        getInstanceActivity: (...args) => mocks.getInstanceActivity(...args),
        getHotWorlds: (...args) => mocks.getHotWorlds(...args),
        getHotWorldFriendDetail: (...args) => mocks.getHotWorldFriendDetail(...args),
        getMutualGraphSnapshot: (...args) => mocks.getMutualGraphSnapshot(...args),
        getMutualGraphMeta: (...args) => mocks.getMutualGraphMeta(...args)
    }
}));

vi.mock('../watchState', () => ({ watchState: mocks.watchState }));

import { createServerEntityDocument, dispatchServerReadControlRequest } from '../serverReadControl';
import { queryClient } from '../../queries/client';
import { entityQueryPolicies } from '../../queries/policies';

const stores = {
    user: { currentUser: { id: 'usr_current' } }
};

describe('serverReadControl', () => {
    beforeEach(() => {
        queryClient.clear();
        mocks.watchState.isLoggedIn = true;
        for (const mock of [
            mocks.getUsers,
            mocks.getWorlds,
            mocks.groupSearch,
            mocks.getUser,
            mocks.getWorld,
            mocks.getAvatar,
            mocks.getGroup,
            mocks.lookupAvatars,
            mocks.getDateOfInstanceActivity,
            mocks.getInstanceActivity,
            mocks.getHotWorlds,
            mocks.getHotWorldFriendDetail,
            mocks.getMutualGraphSnapshot,
            mocks.getMutualGraphMeta
        ]) {
            mock.mockReset();
        }
    });

    afterEach(() => {
        queryClient.clear();
        vi.useRealTimers();
    });

    it.each([
        ['user', 'usr_cached', 'getUser', entityQueryPolicies.user.staleTime],
        ['world', 'wrld_cached', 'getWorld', entityQueryPolicies.world.staleTime],
        ['avatar', 'avtr_cached', 'getAvatar', 120_000],
        ['group', 'grp_cached', 'getGroup', entityQueryPolicies.group.staleTime]
    ])(
        'shares the original %s cache between parallel browser reads and refreshes stale data',
        async (type, id, method, staleTime) => {
            vi.useFakeTimers({ toFake: ['Date'] });
            mocks[method].mockResolvedValue({ json: { id, name: 'First', displayName: 'First' } });
            const request = { operation: 'entity', payload: { type, id } };
            const replies = await Promise.all([
                dispatchServerReadControlRequest(stores, request),
                dispatchServerReadControlRequest(stores, request)
            ]);
            expect(replies.every((reply) => reply.ok && reply.item.name === 'First')).toBe(true);
            expect(mocks[method]).toHaveBeenCalledOnce();
            await dispatchServerReadControlRequest(stores, request);
            expect(mocks[method]).toHaveBeenCalledOnce();

            vi.setSystemTime(Date.now() + staleTime + 1);
            mocks[method].mockResolvedValue({ json: { id, name: 'Updated', displayName: 'Updated' } });
            const refreshed = await dispatchServerReadControlRequest(stores, request);
            expect(mocks[method]).toHaveBeenCalledTimes(2);
            expect(refreshed.item.name).toBe('Updated');
        }
    );

    it('uses the original VRCX user request with bounded typed parameters', async () => {
        mocks.getUsers.mockResolvedValue({
            json: [
                {
                    id: 'usr_result',
                    displayName: 'Result User',
                    bio: 'Visible bio',
                    password: 'drop-password',
                    accessToken: 'drop-token',
                    unknownPrivateField: 'drop-unknown'
                }
            ]
        });

        const response = await dispatchServerReadControlRequest(stores, {
            operation: 'search',
            payload: {
                type: 'user',
                query: 'Result',
                offset: 20,
                limit: 10,
                options: { fuzzy: false, sort: 'last_login', order: 'descending', customFields: 'displayName' }
            }
        });

        expect(mocks.getUsers).toHaveBeenCalledWith({
            n: 10,
            offset: 20,
            search: 'Result',
            fuzzy: false,
            sort: 'last_login',
            order: 'descending',
            customFields: 'displayName'
        });
        // Query string order follows the original searchUserByDisplayName/useSearchUser params.
        expect(Object.keys(mocks.getUsers.mock.calls[0][0])).toEqual([
            'n',
            'offset',
            'fuzzy',
            'search',
            'customFields',
            'sort',
            'order'
        ]);
        expect(response).toMatchObject({ ok: true, offset: 20, limit: 10 });
        expect(response.items).toHaveLength(1);
        expect(response.items[0]).toMatchObject({
            type: 'user',
            id: 'usr_result',
            name: 'Result User',
            payload: { id: 'usr_result', displayName: 'Result User', bio: 'Visible bio' }
        });
        expect(JSON.stringify(response)).not.toContain('drop-password');
        expect(JSON.stringify(response)).not.toContain('drop-token');
        expect(JSON.stringify(response)).not.toContain('drop-unknown');
    });

    it('preserves original VRCX world category endpoint and filter semantics without a text query', async () => {
        mocks.getWorlds.mockResolvedValue({
            json: [{ id: 'wrld_favorite', name: 'Favorite World', tags: ['system_approved'] }]
        });

        const response = await dispatchServerReadControlRequest(stores, {
            operation: 'search',
            payload: {
                type: 'world',
                query: '',
                offset: 60,
                limit: 60,
                options: {
                    worldOption: 'favorites',
                    sort: 'popularity',
                    order: 'descending',
                    featured: false,
                    tag: 'author_tag_featured,system_approved',
                    user: 'me',
                    releaseStatus: 'all'
                }
            }
        });

        expect(mocks.getWorlds).toHaveBeenCalledWith(
            {
                n: 60,
                offset: 60,
                sort: 'popularity',
                order: 'descending',
                featured: false,
                tag: 'author_tag_featured,system_approved',
                user: 'me',
                releaseStatus: 'all'
            },
            'favorites'
        );
        // Query string order follows the original useSearchWorld params.
        expect(Object.keys(mocks.getWorlds.mock.calls[0][0])).toEqual([
            'n',
            'offset',
            'sort',
            'featured',
            'order',
            'user',
            'releaseStatus',
            'tag'
        ]);
        expect(response).toMatchObject({ ok: true, items: [{ id: 'wrld_favorite' }] });
    });

    it('uses original detailed group lookup and retains meaningful read fields only', async () => {
        mocks.getGroup.mockResolvedValue({
            json: {
                id: 'grp_example',
                name: 'Example Group',
                ownerId: 'usr_owner',
                description: 'Group description',
                memberCount: 42,
                roles: [
                    {
                        id: 'grol_reader',
                        name: 'Reader',
                        permissions: ['group-instance-public']
                    }
                ],
                cookie: 'drop-cookie'
            }
        });

        const response = await dispatchServerReadControlRequest(stores, {
            operation: 'entity',
            payload: { type: 'group', id: 'grp_example' }
        });

        expect(mocks.getGroup).toHaveBeenCalledWith({ groupId: 'grp_example', includeRoles: true });
        expect(response).toMatchObject({
            ok: true,
            item: {
                type: 'group',
                id: 'grp_example',
                name: 'Example Group',
                ownerId: 'usr_owner',
                payload: { memberCount: 42 }
            }
        });
        expect(JSON.stringify(response)).not.toContain('drop-cookie');
    });

    it('does not make upstream requests while VRChat authentication is unavailable', async () => {
        mocks.watchState.isLoggedIn = false;
        const response = await dispatchServerReadControlRequest(stores, {
            operation: 'search',
            payload: { type: 'world', query: 'Example', offset: 0, limit: 60 }
        });

        expect(response).toMatchObject({ ok: false, errorCode: 'requires_authentication' });
        expect(mocks.getWorlds).not.toHaveBeenCalled();
    });

    it('rejects wrong entity id prefixes before crossing the fixed response boundary', () => {
        expect(createServerEntityDocument('world', { id: 'usr_wrong', name: 'Wrong' })).toBeNull();
        expect(createServerEntityDocument('avatar', { id: 'avtr_ok', name: 'Avatar' })).toMatchObject({
            type: 'avatar',
            id: 'avtr_ok',
            name: 'Avatar'
        });
    });

    it.each(['avatar_remote_database_disabled', 'avatar_remote_database_limit_exceeded'])(
        'preserves the fixed avatar provider failure %s through the read boundary',
        async (code) => {
            mocks.lookupAvatars.mockRejectedValue(Object.assign(new Error(), { code }));
            const result = await dispatchServerReadControlRequest(stores, {
                operation: 'search',
                payload: { type: 'avatar', query: 'name', offset: 0, limit: 5_000 }
            });
            expect(result).toMatchObject({ ok: false, items: [], errorCode: code });
        }
    );

    it('reports provider network failure instead of a successful empty avatar search', async () => {
        mocks.lookupAvatars.mockRejectedValue(new TypeError('provider unavailable'));
        const result = await dispatchServerReadControlRequest(stores, {
            operation: 'search',
            payload: { type: 'avatar', query: 'name', offset: 0, limit: 5_000 }
        });
        expect(result).toMatchObject({ ok: false, items: [], errorCode: 'network_error' });
    });

    it('rejects an oversized provider search result instead of truncating silently', async () => {
        mocks.lookupAvatars.mockResolvedValue(
            new Map(Array.from({ length: 5_001 }, (_, i) => [`avtr_${i}`, { id: `avtr_${i}` }]))
        );
        const result = await dispatchServerReadControlRequest(stores, {
            operation: 'search',
            payload: { type: 'avatar', query: 'name', offset: 0, limit: 5_000 }
        });
        expect(result).toMatchObject({ ok: false, items: [], errorCode: 'avatar_remote_database_limit_exceeded' });
    });

    it('invokes the original instance activity methods and serializes the original location map', async () => {
        const rows = [
            {
                id: 1,
                created_at: '2026-11-06T12:00:00Z',
                type: 'OnPlayerLeft',
                display_name: 'Current User',
                location: 'wrld_example:123',
                user_id: 'usr_current',
                time: 60_000
            }
        ];
        mocks.getInstanceActivity.mockResolvedValue({
            currentUserData: rows,
            detailData: new Map([['wrld_example:123', rows]])
        });

        mocks.watchState.isLoggedIn = false;
        const response = await dispatchServerReadControlRequest(stores, {
            operation: 'database-read',
            payload: {
                name: 'getInstanceActivity',
                arguments: {
                    startDate: '2026-11-06T00:00:00Z',
                    endDate: '2026-11-06T23:59:59Z'
                }
            }
        });

        expect(mocks.getInstanceActivity).toHaveBeenCalledWith('2026-11-06T00:00:00Z', '2026-11-06T23:59:59Z');
        expect(response).toEqual({
            ok: true,
            result: {
                currentUserData: rows,
                detailData: [{ location: 'wrld_example:123', rows }]
            },
            errorCode: null
        });
    });

    it('uses only the fixed hot-world and cached mutual graph database methods', async () => {
        mocks.getHotWorlds.mockResolvedValue([{ worldId: 'wrld_example' }]);
        mocks.getHotWorldFriendDetail.mockResolvedValue([{ userId: 'usr_friend' }]);
        mocks.getMutualGraphSnapshot.mockResolvedValue(new Map([['usr_friend', ['usr_mutual']]]));
        mocks.getMutualGraphMeta.mockResolvedValue(
            new Map([['usr_friend', { lastFetchedAt: '2026-11-06T12:00:00Z', optedOut: false }]])
        );

        const hotWorlds = await dispatchServerReadControlRequest(stores, {
            operation: 'database-read',
            payload: { name: 'getHotWorlds', arguments: { days: 30, limit: 30 } }
        });
        const friends = await dispatchServerReadControlRequest(stores, {
            operation: 'database-read',
            payload: { name: 'getHotWorldFriendDetail', arguments: { worldId: 'wrld_example', days: 30 } }
        });
        const snapshot = await dispatchServerReadControlRequest(stores, {
            operation: 'database-read',
            payload: { name: 'getMutualGraphSnapshot', arguments: {} }
        });
        const meta = await dispatchServerReadControlRequest(stores, {
            operation: 'database-read',
            payload: { name: 'getMutualGraphMeta', arguments: {} }
        });

        expect(mocks.getHotWorlds).toHaveBeenCalledWith(30, 30);
        expect(mocks.getHotWorldFriendDetail).toHaveBeenCalledWith('wrld_example', 30);
        expect(hotWorlds.result).toEqual([{ worldId: 'wrld_example' }]);
        expect(friends.result).toEqual([{ userId: 'usr_friend' }]);
        expect(snapshot.result).toEqual([{ friendId: 'usr_friend', mutualIds: ['usr_mutual'] }]);
        expect(meta.result).toEqual([
            { friendId: 'usr_friend', lastFetchedAt: '2026-11-06T12:00:00Z', optedOut: false }
        ]);
    });
});
