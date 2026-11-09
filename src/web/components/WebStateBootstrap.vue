<template><slot /></template>

<script setup>
    import { onBeforeUnmount, onMounted, watch } from 'vue';
    import { reactive } from 'vue';

    import { computeTrustLevel, createDefaultUserRef, parseLocation } from '../../shared/utils';
    import { useFeedStore, useFriendStore, useGameLogStore, useNotificationStore, useUserStore } from '../../stores';
    import { watchState } from '../../services/watchState';
    import { webDatabase } from '../webDatabase';
    import { hydrateWebFavorites } from '../favorites';
    import { getWebEntity } from '../entities';
    import { webSession } from '../session';

    let active = true;
    let mounted = false;
    let refreshTimer = null;
    let refreshInFlight = false;
    const pendingResources = new Set();
    const handledRevisions = new Map();

    function createWebUser(user, isFriend = true) {
        const ref = reactive(
            createDefaultUserRef({
                ...user,
                displayName: user.displayName || user.id,
                location: user.location || 'offline',
                status: user.status || 'offline',
                state: user.state || 'offline',
                isFriend
            })
        );
        const trust = computeTrustLevel(ref.tags, ref.developerType);
        ref.$isModerator = trust.isModerator;
        ref.$isTroll = trust.isTroll;
        ref.$isProbableTroll = trust.isProbableTroll;
        ref.$trustLevel = trust.trustLevel;
        ref.$trustClass = trust.trustClass;
        ref.$trustSortNum = trust.trustSortNum;
        ref.$userColour = '';
        ref.$friendNumber = isFriend && Number.isInteger(user.friendNumber) ? user.friendNumber : 0;
        ref.$location = parseLocation(ref.location);
        return ref;
    }

    async function hydrateCurrentUser() {
        const currentUserId = webSession.state.status?.currentUserId;
        if (!currentUserId) return;

        const userStore = useUserStore();
        let user;
        try {
            user = await getWebEntity('user', currentUserId);
        } catch (error) {
            if (error?.status !== 404 && active) {
                webSession.state.error =
                    error instanceof Error ? error.message : '現在のユーザーを読み込めませんでした。';
            }
            return;
        }
        if (!active || webSession.state.status?.currentUserId !== currentUserId) return;

        const ref = createWebUser(user, false);
        userStore.applyUserLanguage(ref);
        userStore.setCurrentUser(ref);
        userStore.setCachedUser(ref);
    }

    async function hydrate() {
        const friendStore = useFriendStore();
        const userStore = useUserStore();
        watchState.isFriendsLoaded = false;

        try {
            const users = await webDatabase.getWebFriends();
            if (!active) return;

            // Use VRCX's batch removal path so its sorted sidebar index and
            // presence-derived lists are cleared together with the friend Map.
            friendStore.refreshFriendsStatus({
                friends: [],
                offlineFriends: [],
                activeFriends: [],
                onlineFriends: []
            });
            friendStore.friendLog.clear();
            userStore.clearCachedUsers();

            let highestFriendNumber = 0;
            users.forEach((user) => {
                const ref = createWebUser(user);
                highestFriendNumber = Math.max(highestFriendNumber, ref.$friendNumber);
                userStore.setCachedUser(ref);
                friendStore.friendLog.set(ref.id, {
                    userId: ref.id,
                    displayName: ref.displayName,
                    trustLevel: ref.$trustLevel,
                    friendNumber: ref.$friendNumber
                });
                friendStore.addFriend(ref.id, ref.state);
            });
            friendStore.state.friendNumber = highestFriendNumber;
            friendStore.updateOnlineFriendCounter();
            await hydrateCurrentUser();
            watchState.isFriendsLoaded = true;
        } catch (error) {
            if (!active) return;
            webSession.state.error = error instanceof Error ? error.message : 'フレンド情報を読み込めませんでした。';
        }
    }

    function queueResourceRefresh() {
        if (!mounted || refreshInFlight || refreshTimer !== null || pendingResources.size === 0) {
            return;
        }
        refreshTimer = window.setTimeout(() => {
            refreshTimer = null;
            void refreshResources();
        }, 80);
    }

    async function refreshResources() {
        if (!active || refreshInFlight) {
            return;
        }
        const resources = Array.from(pendingResources);
        pendingResources.clear();
        if (resources.length === 0) {
            return;
        }

        refreshInFlight = true;
        try {
            const work = [];
            if (resources.includes('friends')) work.push(hydrate());
            if (resources.includes('friendHistory')) work.push(useFriendStore().initFriendLogHistoryTable());
            if (resources.includes('feed')) work.push(useFeedStore().feedTableLookup());
            if (resources.includes('notifications')) work.push(useNotificationStore().initNotifications());
            if (resources.includes('gameLog')) work.push(useGameLogStore().gameLogTableLookup());
            if (resources.includes('favorites')) work.push(hydrateWebFavorites());
            await Promise.allSettled(work);
        } finally {
            refreshInFlight = false;
            queueResourceRefresh();
        }
    }

    watch(
        () => webSession.state.resourceRevisions,
        (revisions) => {
            for (const [resource, revision] of Object.entries(revisions)) {
                const current = Number(revision) || 0;
                if (current > (handledRevisions.get(resource) || 0)) {
                    handledRevisions.set(resource, current);
                    pendingResources.add(resource);
                }
            }
            queueResourceRefresh();
        },
        { deep: true }
    );

    watch(
        () => webSession.state.status?.currentUserId,
        () => {
            if (mounted) void hydrateCurrentUser();
        }
    );

    onMounted(async () => {
        for (const [resource, revision] of Object.entries(webSession.state.resourceRevisions)) {
            handledRevisions.set(resource, Number(revision) || 0);
        }
        mounted = true;
        await hydrate();
        if (active) {
            // Browser authorization does not set the desktop login flag, so
            // seed each original read store explicitly on the first mount.
            // Subsequent reads use the same resource-revision/SSE path.
            for (const resource of ['friendHistory', 'feed', 'notifications', 'gameLog', 'favorites']) {
                pendingResources.add(resource);
            }
            await refreshResources();
        }
    });

    onBeforeUnmount(() => {
        active = false;
        mounted = false;
        if (refreshTimer !== null) {
            window.clearTimeout(refreshTimer);
            refreshTimer = null;
        }
        watchState.isFriendsLoaded = false;
        watchState.isFavoritesLoaded = false;
    });
</script>
