import { reactive } from 'vue';

import { createDefaultAvatarRef, createDefaultUserRef, createDefaultWorldRef } from '../shared/utils';
import { useAvatarStore, useFavoriteStore, useFriendStore, useUserStore, useWorldStore } from '../stores';
import { watchState } from '../services/watchState';
import { webDatabase } from './webDatabase';

function ensureUserRef(userStore, record) {
    const existing = userStore.cachedUsers.get(record.favoriteId);
    if (existing) return existing;

    const ref = reactive(
        createDefaultUserRef({
            state: 'offline',
            status: 'offline',
            location: 'offline',
            isFriend: true,
            ...(record.targetPayload || {}),
            id: record.favoriteId,
            displayName: record.targetName || record.targetPayload?.displayName || record.favoriteId
        })
    );
    userStore.setCachedUser(ref);
    return ref;
}

function ensureWorldRef(worldStore, record) {
    const existing = worldStore.cachedWorlds.get(record.favoriteId);
    const data = {
        ...(record.targetPayload || {}),
        id: record.favoriteId,
        name: record.targetName || record.targetPayload?.name || record.favoriteId
    };
    if (existing) {
        Object.assign(existing, data);
        return existing;
    }
    const ref = createDefaultWorldRef(data);
    worldStore.cachedWorlds.set(ref.id, ref);
    return ref;
}

function ensureAvatarRef(avatarStore, record) {
    const existing = avatarStore.cachedAvatars.get(record.favoriteId);
    const data = {
        ...(record.targetPayload || {}),
        id: record.favoriteId,
        name: record.targetName || record.targetPayload?.name || record.favoriteId
    };
    if (existing) {
        Object.assign(existing, data);
        return existing;
    }
    const ref = createDefaultAvatarRef(data);
    avatarStore.cachedAvatars.set(ref.id, ref);
    return ref;
}

/**
 * Populate the existing VRCX favorite store from the fixed Web read model.
 * The browser never requests or mutates VRChat favorite groups here.
 */
export async function hydrateWebFavorites() {
    const projection = await webDatabase.getWebFavorites();
    const records = projection.favorites.toSorted((a, b) => a.sortOrder - b.sortOrder || a.id.localeCompare(b.id));
    const favoriteStore = useFavoriteStore();
    const userStore = useUserStore();
    const worldStore = useWorldStore();
    const avatarStore = useAvatarStore();
    const friendStore = useFriendStore();

    favoriteStore.cachedFavorites.clear();
    favoriteStore.cachedFavoritesByObjectId.clear();
    favoriteStore.state.favoriteObjects.clear();
    favoriteStore.state.favoriteFriends_ = [];
    favoriteStore.state.favoriteWorlds_ = [];
    favoriteStore.state.favoriteAvatars_ = [];
    for (const key of Object.keys(favoriteStore.cachedFavoriteGroups)) {
        delete favoriteStore.cachedFavoriteGroups[key];
    }

    for (const record of records) {
        const favorite = {
            id: record.id,
            type: record.type,
            favoriteId: record.favoriteId,
            tags: [...record.tags],
            $groupKey: record.$groupKey
        };
        favoriteStore.cachedFavorites.set(favorite.id, favorite);
        favoriteStore.cachedFavoritesByObjectId.set(record.favoriteId, favorite);

        let ref = null;
        if (record.type === 'friend') {
            ref = ensureUserRef(userStore, record);
        } else if (record.type === 'world' || record.type === 'vrcPlusWorld') {
            ref = ensureWorldRef(worldStore, record);
        } else if (record.type === 'avatar') {
            ref = ensureAvatarRef(avatarStore, record);
        }

        const entry = {
            id: record.favoriteId,
            type: record.type,
            groupKey: record.$groupKey,
            ref,
            name: ref?.name || ref?.displayName || record.targetName || record.favoriteId,
            deleted: !ref
        };
        favoriteStore.state.favoriteObjects.set(record.favoriteId, entry);
        if (record.type === 'friend') favoriteStore.state.favoriteFriends_.push(entry);
        if (record.type === 'world' || record.type === 'vrcPlusWorld') favoriteStore.state.favoriteWorlds_.push(entry);
        if (record.type === 'avatar') favoriteStore.state.favoriteAvatars_.push(entry);
    }

    favoriteStore.favoriteFriendGroups.splice(
        0,
        favoriteStore.favoriteFriendGroups.length,
        ...projection.groups.filter((group) => group.type === 'friend')
    );
    favoriteStore.favoriteWorldGroups.splice(
        0,
        favoriteStore.favoriteWorldGroups.length,
        ...projection.groups.filter((group) => group.type === 'world' || group.type === 'vrcPlusWorld')
    );
    favoriteStore.favoriteAvatarGroups.splice(
        0,
        favoriteStore.favoriteAvatarGroups.length,
        ...projection.groups.filter((group) => group.type === 'avatar')
    );
    favoriteStore.setFavoritesSortOrder(records.map((record) => record.favoriteId));
    friendStore.updateLocalFavoriteFriends();
    favoriteStore.setIsFavoriteLoading(false);
    watchState.isFavoritesLoaded = true;
    return projection;
}
