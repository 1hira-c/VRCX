const FEED_FILTERS = new Set(['GPS', 'Status', 'Bio', 'Avatar', 'Online', 'Offline']);
const GAME_LOG_FILTERS = new Set([
    'Location',
    'OnPlayerJoined',
    'OnPlayerLeft',
    'PortalSpawn',
    'Event',
    'External',
    'VideoPlay',
    'StringLoad',
    'ImageLoad'
]);

export const historyDatabaseReadNames = new Set([
    'lookupFeedDatabase',
    'searchFeedDatabase',
    'lookupGameLogDatabase',
    'searchGameLogDatabase',
    'getSessionsLocationSegments',
    'getSessionsLocationSegmentsByAnchor',
    'getSessionsEventsForSegments',
    'getUserMemo',
    'getAllUserMemos',
    'getWorldMemo',
    'getAvatarMemoDB',
    'getUserStats',
    'getAllUserStats',
    'getFriendLogHistory',
    'getFriendLogHistoryForUserId'
]);

function asText(value) {
    return typeof value === 'string' ? value : '';
}

function nullableText(value) {
    return typeof value === 'string' ? value : null;
}

function nullableNumber(value) {
    return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

function serializeFeedRow(value) {
    return {
        rowId: Number(value?.rowId),
        created_at: asText(value?.created_at),
        userId: nullableText(value?.userId),
        displayName: nullableText(value?.displayName),
        type: asText(value?.type),
        location: nullableText(value?.location),
        worldName: nullableText(value?.worldName),
        previousLocation: nullableText(value?.previousLocation),
        time: nullableNumber(value?.time),
        groupName: nullableText(value?.groupName),
        status: nullableText(value?.status),
        statusDescription: nullableText(value?.statusDescription),
        previousStatus: nullableText(value?.previousStatus),
        previousStatusDescription: nullableText(value?.previousStatusDescription),
        bio: nullableText(value?.bio),
        previousBio: nullableText(value?.previousBio),
        bioLinks: Array.isArray(value?.bioLinks) ? value.bioLinks : [],
        previousBioLinks: Array.isArray(value?.previousBioLinks) ? value.previousBioLinks : [],
        ownerId: nullableText(value?.ownerId),
        avatarName: nullableText(value?.avatarName),
        currentAvatarImageUrl: nullableText(value?.currentAvatarImageUrl),
        currentAvatarThumbnailImageUrl: nullableText(value?.currentAvatarThumbnailImageUrl),
        previousCurrentAvatarImageUrl: nullableText(value?.previousCurrentAvatarImageUrl),
        previousCurrentAvatarThumbnailImageUrl: nullableText(value?.previousCurrentAvatarThumbnailImageUrl)
    };
}

function serializeGameLogRow(value) {
    return {
        rowId: Number(value?.rowId),
        created_at: asText(value?.created_at),
        type: asText(value?.type),
        displayName: nullableText(value?.displayName),
        location: nullableText(value?.location),
        userId: nullableText(value?.userId),
        time: nullableNumber(value?.time),
        worldId: nullableText(value?.worldId),
        worldName: nullableText(value?.worldName),
        groupName: nullableText(value?.groupName),
        instanceId: nullableText(value?.instanceId),
        videoUrl: nullableText(value?.videoUrl),
        videoName: nullableText(value?.videoName),
        videoId: nullableText(value?.videoId),
        resourceUrl: nullableText(value?.resourceUrl),
        resourceType: nullableText(value?.resourceType),
        data: nullableText(value?.data),
        message: nullableText(value?.message)
    };
}

function serializeSessionLocation(value) {
    return {
        id: Number(value?.id),
        created_at: asText(value?.created_at),
        location: asText(value?.location),
        worldId: nullableText(value?.worldId),
        worldName: nullableText(value?.worldName),
        time: nullableNumber(value?.time),
        groupName: nullableText(value?.groupName)
    };
}

function serializeSessionEvent(value) {
    return {
        type: asText(value?.type),
        created_at: asText(value?.created_at),
        displayName: nullableText(value?.displayName),
        userId: nullableText(value?.userId),
        location: asText(value?.location),
        videoUrl: nullableText(value?.videoUrl),
        videoName: nullableText(value?.videoName),
        videoId: nullableText(value?.videoId)
    };
}

function serializeMemo(value, idField) {
    return {
        [idField]: asText(value?.[idField]),
        editedAt: nullableText(value?.editedAt),
        memo: nullableText(value?.memo)
    };
}

function serializeFriendHistory(value) {
    return {
        rowId: Number(value?.rowId),
        created_at: asText(value?.created_at),
        type: asText(value?.type),
        userId: asText(value?.userId),
        displayName: nullableText(value?.displayName),
        previousDisplayName: nullableText(value?.previousDisplayName),
        trustLevel: nullableText(value?.trustLevel),
        previousTrustLevel: nullableText(value?.previousTrustLevel),
        friendNumber: Number.isInteger(value?.friendNumber) ? value.friendNumber : null
    };
}

function favorites(stores, enabled) {
    return enabled ? Array.from(stores.friend.localFavoriteFriends.values()) : [];
}

async function currentFriendStatsInput(stores, database) {
    const userIds = new Set();
    const displayNames = new Set();
    for (const friend of stores.friend.friends.values()) {
        if (/^usr_[A-Za-z0-9_-]+$/.test(friend?.id)) userIds.add(friend.id);
        if (typeof friend?.ref?.displayName === 'string' && friend.ref.displayName.length <= 512) {
            displayNames.add(friend.ref.displayName);
        }
    }

    // The hidden renderer can be logged out while the imported snapshot is
    // still browsable. In that state, derive the same friend set from VRCX's
    // own current-friend table instead of trusting browser-provided IDs.
    if (userIds.size === 0) {
        for (const friend of await database.getFriendLogCurrent()) {
            if (/^usr_[A-Za-z0-9_-]+$/.test(friend?.userId)) userIds.add(friend.userId);
            if (typeof friend?.displayName === 'string' && friend.displayName.length <= 512) {
                displayNames.add(friend.displayName);
            }
        }
    }
    return { userIds: [...userIds], displayNames: [...displayNames] };
}

/**
 * Execute a closed set of history reads through the existing VRCX database
 * service. Browser arguments never select a method, table, SQL string, URL, or
 * header outside this switch; favorites and the bulk friend set are resolved
 * from the original hidden renderer stores.
 */
export async function executeHistoryDatabaseRead(stores, database, payload) {
    const args = payload.arguments;
    switch (payload.name) {
        case 'lookupFeedDatabase':
            return (
                await database.lookupFeedDatabase(args.filters, favorites(stores, args.favoritesOnly), args.maxEntries)
            ).map(serializeFeedRow);
        case 'searchFeedDatabase':
            return (
                await database.searchFeedDatabase(
                    args.search,
                    args.filters,
                    favorites(stores, args.favoritesOnly),
                    args.maxEntries,
                    args.dateFrom,
                    args.dateTo
                )
            ).map(serializeFeedRow);
        case 'lookupGameLogDatabase':
            return (
                await database.lookupGameLogDatabase(
                    args.filters,
                    favorites(stores, args.favoritesOnly),
                    args.maxEntries
                )
            ).map(serializeGameLogRow);
        case 'searchGameLogDatabase':
            return (
                await database.searchGameLogDatabase(
                    args.search,
                    args.filters,
                    favorites(stores, args.favoritesOnly),
                    args.maxEntries
                )
            ).map(serializeGameLogRow);
        case 'getSessionsLocationSegments':
            return (await database.getSessionsLocationSegments(args.beforeId, args.limit)).map(
                serializeSessionLocation
            );
        case 'getSessionsLocationSegmentsByAnchor':
            return (await database.getSessionsLocationSegmentsByAnchor(args.sinceDate, args.limit)).map(
                serializeSessionLocation
            );
        case 'getSessionsEventsForSegments':
            return (
                await database.getSessionsEventsForSegments(args.locationTags, args.afterDate, args.beforeDate)
            ).map(serializeSessionEvent);
        case 'getUserMemo':
            return serializeMemo(await database.getUserMemo(args.userId), 'userId');
        case 'getAllUserMemos':
            return (await database.getAllUserMemos()).map((value) => ({
                userId: asText(value?.userId),
                memo: nullableText(value?.memo)
            }));
        case 'getWorldMemo':
            return serializeMemo(await database.getWorldMemo(args.worldId), 'worldId');
        case 'getAvatarMemoDB':
            return serializeMemo(await database.getAvatarMemoDB(args.avatarId), 'avatarId');
        case 'getUserStats': {
            const value = await database.getUserStats(
                { id: args.userId, displayName: args.displayName },
                args.inCurrentWorld
            );
            return {
                timeSpent: nullableNumber(value?.timeSpent) ?? 0,
                lastSeen: nullableText(value?.lastSeen),
                joinCount: Number.isInteger(value?.joinCount) ? value.joinCount : 0,
                userId: asText(value?.userId),
                previousDisplayNames:
                    value?.previousDisplayNames instanceof Map
                        ? Array.from(value.previousDisplayNames, ([displayName, updatedAt]) => ({
                              displayName: asText(displayName),
                              updatedAt: asText(updatedAt)
                          }))
                        : []
            };
        }
        case 'getAllUserStats': {
            const { userIds, displayNames } = await currentFriendStatsInput(stores, database);
            return (await database.getAllUserStats(userIds, displayNames)).map((value) => ({
                lastSeen: nullableText(value?.lastSeen),
                userId: nullableText(value?.userId),
                timeSpent: nullableNumber(value?.timeSpent) ?? 0,
                joinCount: Number.isInteger(value?.joinCount) ? value.joinCount : 0,
                displayName: nullableText(value?.displayName)
            }));
        }
        case 'getFriendLogHistory':
            return (await database.getFriendLogHistory()).map(serializeFriendHistory);
        case 'getFriendLogHistoryForUserId':
            return (await database.getFriendLogHistoryForUserId(args.userId, args.types)).map(serializeFriendHistory);
        default:
            throw new Error('Unsupported history database read');
    }
}

export const historyReadFilters = Object.freeze({
    feed: FEED_FILTERS,
    gameLog: GAME_LOG_FILTERS
});
