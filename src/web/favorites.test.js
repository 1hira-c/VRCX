import { beforeEach, describe, expect, it, vi } from 'vitest';

const mocks = vi.hoisted(() => {
    const favoriteStore = {
        cachedFavorites: new Map(),
        cachedFavoritesByObjectId: new Map(),
        cachedFavoriteGroups: { legacy: { id: 'legacy' } },
        state: {
            favoriteObjects: new Map(),
            favoriteFriends_: [],
            favoriteWorlds_: [],
            favoriteAvatars_: []
        },
        favoriteFriendGroups: [],
        favoriteWorldGroups: [],
        favoriteAvatarGroups: [],
        setFavoritesSortOrder: vi.fn(),
        setIsFavoriteLoading: vi.fn()
    };
    const userStore = {
        cachedUsers: new Map(),
        setCachedUser: vi.fn((ref) => userStore.cachedUsers.set(ref.id, ref))
    };
    return {
        favoriteStore,
        userStore,
        worldStore: { cachedWorlds: new Map() },
        avatarStore: { cachedAvatars: new Map() },
        friendStore: { localFavoriteFriends: new Set(), updateLocalFavoriteFriends: vi.fn() },
        getWebFavorites: vi.fn(),
        watchState: { isFavoritesLoaded: false }
    };
});

vi.mock('../stores', () => ({
    useFavoriteStore: () => mocks.favoriteStore,
    useUserStore: () => mocks.userStore,
    useWorldStore: () => mocks.worldStore,
    useAvatarStore: () => mocks.avatarStore,
    useFriendStore: () => mocks.friendStore
}));

vi.mock('../services/watchState', () => ({ watchState: mocks.watchState }));

vi.mock('../shared/utils', () => ({
    createDefaultUserRef: (json) => ({ id: '', displayName: '', ...json }),
    createDefaultWorldRef: (json) => ({ id: '', name: '', ...json }),
    createDefaultAvatarRef: (json) => ({ id: '', name: '', ...json })
}));

vi.mock('./webDatabase', () => ({
    webDatabase: { getWebFavorites: mocks.getWebFavorites }
}));

import { hydrateWebFavorites } from './favorites';

describe('hydrateWebFavorites', () => {
    beforeEach(() => {
        mocks.favoriteStore.cachedFavorites.clear();
        mocks.favoriteStore.cachedFavoritesByObjectId.clear();
        mocks.favoriteStore.cachedFavoriteGroups.legacy = { id: 'legacy' };
        mocks.favoriteStore.state.favoriteObjects.clear();
        mocks.favoriteStore.state.favoriteFriends_ = [];
        mocks.favoriteStore.state.favoriteWorlds_ = [];
        mocks.favoriteStore.state.favoriteAvatars_ = [];
        mocks.favoriteStore.favoriteFriendGroups.splice(0);
        mocks.favoriteStore.favoriteWorldGroups.splice(0);
        mocks.favoriteStore.favoriteAvatarGroups.splice(0);
        mocks.userStore.cachedUsers.clear();
        mocks.worldStore.cachedWorlds.clear();
        mocks.avatarStore.cachedAvatars.clear();
        mocks.friendStore.localFavoriteFriends.clear();
        mocks.friendStore.updateLocalFavoriteFriends.mockClear();
        mocks.favoriteStore.setFavoritesSortOrder.mockClear();
        mocks.favoriteStore.setIsFavoriteLoading.mockClear();
        mocks.watchState.isFavoritesLoaded = false;
    });

    it('hydrates the exact remote model without synthetic IDs, groups, or type conversion', async () => {
        mocks.getWebFavorites.mockResolvedValue({
            ready: true,
            favorites: [
                {
                    id: 'fvrt_world',
                    type: 'vrcPlusWorld',
                    favoriteId: 'wrld_example',
                    tags: ['vrcPlusWorlds1'],
                    $groupKey: 'vrcPlusWorld:vrcPlusWorlds1',
                    sortOrder: 1,
                    targetName: 'Example World',
                    targetPayload: { id: 'wrld_example', name: 'Example World' }
                },
                {
                    id: 'fvrt_friend',
                    type: 'friend',
                    favoriteId: 'usr_friend',
                    tags: ['group_0'],
                    $groupKey: 'friend:group_0',
                    sortOrder: 0,
                    targetName: 'Example Friend',
                    targetPayload: { id: 'usr_friend', displayName: 'Example Friend' }
                }
            ],
            groups: [
                {
                    key: 'friend:group_0',
                    type: 'friend',
                    name: 'group_0',
                    displayName: 'Best friends',
                    capacity: 150,
                    count: 1,
                    visibility: 'private',
                    sortOrder: 0
                },
                {
                    key: 'vrcPlusWorld:vrcPlusWorlds1',
                    type: 'vrcPlusWorld',
                    name: 'vrcPlusWorlds1',
                    displayName: 'VRC+ Group 1',
                    capacity: 100,
                    count: 1,
                    visibility: 'private',
                    sortOrder: 1
                }
            ]
        });

        await hydrateWebFavorites();

        expect([...mocks.favoriteStore.cachedFavorites.keys()]).toEqual(['fvrt_friend', 'fvrt_world']);
        expect(mocks.favoriteStore.cachedFavorites.get('fvrt_world')).toEqual({
            id: 'fvrt_world',
            type: 'vrcPlusWorld',
            favoriteId: 'wrld_example',
            tags: ['vrcPlusWorlds1'],
            $groupKey: 'vrcPlusWorld:vrcPlusWorlds1'
        });
        expect(mocks.favoriteStore.state.favoriteWorlds_[0]).toMatchObject({
            id: 'wrld_example',
            type: 'vrcPlusWorld',
            groupKey: 'vrcPlusWorld:vrcPlusWorlds1',
            name: 'Example World'
        });
        expect(mocks.favoriteStore.favoriteFriendGroups[0].displayName).toBe('Best friends');
        expect(mocks.favoriteStore.favoriteWorldGroups[0].type).toBe('vrcPlusWorld');
        expect(mocks.favoriteStore.cachedFavoriteGroups).toEqual({});
        expect(mocks.favoriteStore.setFavoritesSortOrder).toHaveBeenCalledWith(['usr_friend', 'wrld_example']);
        expect(mocks.friendStore.updateLocalFavoriteFriends).toHaveBeenCalledOnce();
        expect(mocks.watchState.isFavoritesLoaded).toBe(true);
    });
});
