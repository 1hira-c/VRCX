import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import { nextTick } from 'vue';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const mocks = vi.hoisted(() => ({
    getWebFriends: vi.fn(),
    getFriendLogHistory: vi.fn().mockResolvedValue([]),
    hydrateWebFavorites: vi.fn().mockResolvedValue(undefined),
    feedTableLookup: vi.fn().mockResolvedValue(undefined),
    gameLogTableLookup: vi.fn().mockResolvedValue(undefined),
    initNotifications: vi.fn().mockResolvedValue(undefined),
    updateFriendFlow: vi.fn(),
    initFriendsFlow: vi.fn(),
    watchState: { isLoggedIn: false, isFriendsLoaded: false, isFavoritesLoaded: false },
    sessionState: null,
    userStore: {
        cachedUsers: new Map(),
        setCachedUser(ref) {
            this.cachedUsers.set(ref.id, ref);
        },
        clearCachedUsers() {
            this.cachedUsers.clear();
        }
    }
}));

vi.mock('../../../shared/utils', async () => ({
    ...(await import('../../../shared/utils/friend')),
    ...(await import('../../../shared/utils/compare')),
    computeTrustLevel: () => ({}),
    createDefaultUserRef: (user) => ({ ...user, name: user.displayName }),
    parseLocation: (tag) => ({ tag, isRealInstance: tag.startsWith('wrld_') }),
    isRealInstance: (tag) => tag.startsWith('wrld_'),
    createRateLimiter: vi.fn(),
    executeWithBackoff: vi.fn()
}));
vi.mock('../../../services/watchState', async () => {
    const { reactive } = await import('vue');
    mocks.watchState = reactive(mocks.watchState);
    return { watchState: mocks.watchState };
});
vi.mock('../../session', async () => {
    const { reactive } = await import('vue');
    mocks.sessionState = reactive({ status: { currentUserId: null }, resourceRevisions: { friends: 1 }, error: '' });
    return { webSession: { state: mocks.sessionState } };
});
vi.mock('../../webDatabase', () => ({ webDatabase: { getWebFriends: mocks.getWebFriends } }));
vi.mock('../../favorites', () => ({ hydrateWebFavorites: mocks.hydrateWebFavorites }));
vi.mock('../../entities', () => ({ getWebEntity: vi.fn() }));
vi.mock('../../platform', () => ({ isWebPlatform: true, platformCapabilities: {} }));
vi.mock('../../../services/config', () => ({
    default: { getString: vi.fn(async (_key, fallback) => fallback), setString: vi.fn() }
}));
vi.mock('../../../services/database', () => ({ database: { getFriendLogHistory: mocks.getFriendLogHistory } }));
vi.mock('../../../services/runtimeCapabilities', () => ({ runtimeCapabilities: { vrOverlay: false } }));
vi.mock('../../../api', () => ({ friendRequest: {}, userRequest: {} }));
vi.mock('vue-router', async () => {
    const { ref } = await import('vue');
    return { useRouter: () => ({ currentRoute: ref({ name: 'feed', params: {} }) }) };
});
vi.mock('../../../stores/settings/appearance', () => ({
    useAppearanceSettingsStore: () => ({ sidebarSortMethods: ['Sort Alphabetically'] })
}));
vi.mock('../../../stores/settings/general', () => ({ useGeneralSettingsStore: () => ({}) }));
vi.mock('../../../stores/user', () => ({ useUserStore: () => mocks.userStore }));
vi.mock('../../../stores/group', () => ({ useGroupStore: () => ({ clearGroupInstances: vi.fn() }) }));
vi.mock('../../../stores/location', () => ({
    useLocationStore: () => ({ lastLocation: { friendList: new Set() } })
}));
vi.mock('../../../stores/dashboard', () => ({ useDashboardStore: () => ({ dashboards: [] }) }));
vi.mock('../../../stores/favorite', () => ({
    useFavoriteStore: () => ({ cachedFavorites: new Map(), localFriendFavorites: {} })
}));
vi.mock('../../../coordinators/memoCoordinator', () => ({ getUserMemo: vi.fn() }));
vi.mock('../../../coordinators/friendSyncCoordinator', () => ({ runInitFriendsListFlow: mocks.initFriendsFlow }));
vi.mock('../../../coordinators/friendPresenceCoordinator', () => ({
    runPendingOfflineTickFlow: vi.fn(),
    runUpdateFriendFlow: mocks.updateFriendFlow
}));
vi.mock('../../../coordinators/friendRelationshipCoordinator', () => ({
    updateFriendship: vi.fn(),
    runUpdateFriendshipsFlow: vi.fn()
}));
vi.mock('../../../coordinators/userCoordinator', () => ({ applyUser: vi.fn() }));
vi.mock('../../../stores', async () => ({
    useFriendStore: (await import('../../../stores/friend')).useFriendStore,
    useUserStore: () => mocks.userStore,
    useFeedStore: () => ({ feedTableLookup: mocks.feedTableLookup }),
    useGameLogStore: () => ({ gameLogTableLookup: mocks.gameLogTableLookup }),
    useNotificationStore: () => ({ initNotifications: mocks.initNotifications })
}));

import { useFriendStore } from '../../../stores/friend';
import WebStateBootstrap from '../WebStateBootstrap.vue';

describe('Web friend snapshot replacement with the original FriendStore', () => {
    let wrapper;

    beforeEach(() => {
        vi.useFakeTimers();
        vi.clearAllMocks();
        setActivePinia(createPinia());
        mocks.userStore.cachedUsers.clear();
        mocks.watchState.isLoggedIn = false;
        mocks.watchState.isFriendsLoaded = false;
        mocks.sessionState.resourceRevisions = { friends: 1 };
        mocks.sessionState.error = '';
    });

    afterEach(() => {
        wrapper?.unmount();
        vi.useRealTimers();
    });

    const user = (id, state, friendNumber) => ({ id, displayName: id, state, friendNumber, location: 'offline' });
    const ids = (rows) => rows.map(({ id }) => id).sort();

    async function refresh(users) {
        mocks.getWebFriends.mockResolvedValueOnce(users);
        mocks.sessionState.resourceRevisions.friends++;
        await nextTick();
        await vi.advanceTimersByTimeAsync(80);
        await flushPromises();
    }

    it('replaces removed IDs and presence in both the Map and indexed sidebar lists, then clears an empty snapshot', async () => {
        mocks.getWebFriends.mockResolvedValueOnce([user('usr_A', 'online', 1), user('usr_B', 'active', 2)]);
        wrapper = mount(WebStateBootstrap);
        await flushPromises();
        const store = useFriendStore();

        expect(ids(store.onlineFriends)).toEqual(['usr_A']);
        expect(ids(store.activeFriends)).toEqual(['usr_B']);
        expect(store.onlineFriendCount).toBe(1);
        expect(mocks.watchState.isFriendsLoaded).toBe(true);

        await refresh([user('usr_B', 'offline', 2), user('usr_C', 'online', 3)]);

        expect([...store.friends.keys()]).toEqual(['usr_B', 'usr_C']);
        expect(ids(store.onlineFriends)).toEqual(['usr_C']);
        expect(ids(store.activeFriends)).toEqual([]);
        expect(ids(store.offlineFriends)).toEqual(['usr_B']);
        expect(store.onlineFriendCount).toBe(1);
        expect([...store.friendLog.keys()]).toEqual(['usr_B', 'usr_C']);
        expect([...mocks.userStore.cachedUsers.keys()]).toEqual(['usr_B', 'usr_C']);
        expect(store.state.friendNumber).toBe(3);

        await refresh([]);

        expect(store.friends.size).toBe(0);
        expect(store.onlineFriends).toEqual([]);
        expect(store.activeFriends).toEqual([]);
        expect(store.offlineFriends).toEqual([]);
        expect(store.onlineFriendCount).toBe(0);
        expect(store.state.friendNumber).toBe(0);
        expect(mocks.watchState.isLoggedIn).toBe(false);
        expect(mocks.initFriendsFlow).not.toHaveBeenCalled();
        expect(mocks.updateFriendFlow).not.toHaveBeenCalled();
    });
});
