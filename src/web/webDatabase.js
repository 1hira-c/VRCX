import { webApi } from './api';
import { selectWebAvatarFields, selectWebUserFields, selectWebWorldFields } from './entities';

const PAGE_SIZE = 200;
const MAX_READ_ROWS = 5000;
let nativeMaxTableSize = 500;
let nativeSearchTableSize = 50_000;
let cachedWebFriends = [];

// Mirrors the scalar fields in the vrchat package's NotificationV2.data
// union. Unknown upstream fields never enter the existing VRCX stores.
const notificationDataFields = Object.freeze([
    'badgeDescription',
    'badgeId',
    'badgeName',
    'boopingUserDisplayName',
    'ownerId',
    'ownerName',
    'title',
    'announcementTitle',
    'groupId',
    'groupName',
    'transferTargetDisplayName',
    'ownerUserDisplayName'
]);

const notificationDetailFields = Object.freeze([
    'worldId',
    'worldName',
    'groupId',
    'groupName',
    'location',
    'instanceId',
    'instanceName',
    'inviteMessage',
    'requestMessage',
    'responseMessage',
    'senderUserId',
    'senderUsername',
    'senderDisplayName',
    'recipientUserId',
    'recipientUsername',
    'recipientDisplayName',
    'receiverUserId',
    'receiverUsername',
    'receiverDisplayName',
    'targetUserId',
    'targetUsername',
    'targetDisplayName',
    'imageUrl',
    'thumbnailImageUrl',
    'senderUserIcon',
    'emojiId',
    'emojiVersion',
    'inventoryItemId'
]);

function asObject(value) {
    return value && typeof value === 'object' && !Array.isArray(value) ? value : {};
}

function asText(value, maxLength = 2048) {
    if (typeof value !== 'string') return '';
    return value.slice(0, maxLength);
}

function firstText(...values) {
    for (const value of values) {
        const text = asText(value);
        if (text) return text;
    }
    return '';
}

function textArray(value, maxItems = 32, maxLength = 2048) {
    if (!Array.isArray(value)) return [];
    return value
        .filter((item) => typeof item === 'string')
        .slice(0, maxItems)
        .map((item) => item.slice(0, maxLength));
}

function field(value, camelCase, snakeCase = camelCase) {
    const object = asObject(value);
    return object[camelCase] ?? object[snakeCase];
}

function parseObject(value) {
    if (typeof value === 'string') {
        try {
            return asObject(JSON.parse(value));
        } catch {
            return {};
        }
    }
    return asObject(value);
}

function safeScalar(value) {
    if (typeof value === 'string') return asText(value);
    if (typeof value === 'number' && Number.isFinite(value)) return value;
    if (typeof value === 'boolean') return value;
    return undefined;
}

function selectNotificationScalars(sources, allowedFields) {
    const selected = {};
    for (const key of allowedFields) {
        const snakeCase = key.replace(/[A-Z]/g, (letter) => `_${letter.toLowerCase()}`);
        let value;
        for (const source of sources) {
            value = field(source, key, snakeCase);
            if (value !== undefined && value !== null) break;
        }
        const scalar = safeScalar(value);
        if (scalar !== undefined && scalar !== '') selected[key] = scalar;
    }
    return selected;
}

function safeNotificationData(payload) {
    return selectNotificationScalars([parseObject(field(payload, 'data'))], notificationDataFields);
}

function safeNotificationDetails(payload, data) {
    const source = asObject(payload);
    return selectNotificationScalars([parseObject(field(source, 'details')), source, data], notificationDetailFields);
}

function normalizeDate(value) {
    if (typeof value !== 'string' || !value) return '';
    const parsed = Date.parse(value);
    return Number.isFinite(parsed) ? new Date(parsed).toISOString() : value;
}

function deriveFriendState(record, raw) {
    const state = firstText(raw.state, record.state).toLowerCase();
    if (state === 'online' || state === 'active' || state === 'offline') return state;
    const location = firstText(record.location, raw.location).toLowerCase();
    return location && location !== 'offline' ? 'online' : 'offline';
}

/**
 * Adapt the fixed Web BFF friend record to the existing VRCX user model.
 * This deliberately reads only named fields; it does not expose a generic
 * upstream response channel to the browser.
 */
export function toWebUser(record) {
    const raw = asObject(record?.raw);
    const location = firstText(record?.location, raw.location);
    return {
        ...selectWebUserFields(raw),
        id: firstText(record?.id, raw.id),
        displayName: firstText(record?.displayName, raw.displayName, raw.name),
        username: firstText(record?.username, raw.username),
        status: firstText(record?.status, raw.status, 'offline'),
        state: deriveFriendState(record ?? {}, raw),
        location,
        worldName: firstText(record?.worldName, raw.worldName),
        ...(Number.isInteger(raw.friendNumber) && raw.friendNumber >= 0 ? { friendNumber: raw.friendNumber } : {}),
        isFriend: true
    };
}

export function toFeedRow(record) {
    const payload = asObject(record?.payload);
    const kind = firstText(record?.kind).toLowerCase();
    const type =
        kind === 'gps'
            ? 'GPS'
            : kind === 'online_offline'
              ? firstText(field(payload, 'type'), 'Online')
              : kind === 'avatar'
                ? 'Avatar'
                : kind === 'bio'
                  ? 'Bio'
                  : kind === 'status'
                    ? 'Status'
                    : firstText(field(payload, 'type'), kind);
    return {
        rowId: record?.id,
        created_at: normalizeDate(record?.createdAt || field(payload, 'createdAt', 'created_at')),
        type,
        userId: firstText(record?.userId, field(payload, 'userId', 'user_id')),
        displayName: firstText(record?.displayName, field(payload, 'displayName', 'display_name')),
        location: firstText(field(payload, 'location')),
        worldName: firstText(field(payload, 'worldName', 'world_name')),
        previousLocation: firstText(field(payload, 'previousLocation', 'previous_location')),
        status: firstText(field(payload, 'status')),
        statusDescription: firstText(field(payload, 'statusDescription', 'status_description')),
        previousStatus: firstText(field(payload, 'previousStatus', 'previous_status')),
        previousStatusDescription: firstText(
            field(payload, 'previousStatusDescription', 'previous_status_description')
        ),
        avatarName: firstText(field(payload, 'avatarName', 'avatar_name')),
        ownerId: firstText(field(payload, 'ownerId', 'owner_id')),
        currentAvatarImageUrl: firstText(field(payload, 'currentAvatarImageUrl', 'current_avatar_image_url')),
        currentAvatarThumbnailImageUrl: firstText(
            field(payload, 'currentAvatarThumbnailImageUrl', 'current_avatar_thumbnail_image_url')
        ),
        previousCurrentAvatarImageUrl: firstText(
            field(payload, 'previousCurrentAvatarImageUrl', 'previous_current_avatar_image_url')
        ),
        previousCurrentAvatarThumbnailImageUrl: firstText(
            field(payload, 'previousCurrentAvatarThumbnailImageUrl', 'previous_current_avatar_thumbnail_image_url')
        ),
        bio: firstText(field(payload, 'bio')),
        previousBio: firstText(field(payload, 'previousBio', 'previous_bio')),
        bioLinks: textArray(field(payload, 'bioLinks', 'bio_links')),
        previousBioLinks: textArray(field(payload, 'previousBioLinks', 'previous_bio_links')),
        groupName: firstText(field(payload, 'groupName', 'group_name')),
        time: field(payload, 'time') ?? ''
    };
}

export function toGameLogRow(record) {
    const payload = asObject(record?.payload);
    const kind = firstText(record?.kind).toLowerCase();
    const resourceType = firstText(field(payload, 'resourceType', 'resource_type'));
    const type =
        kind === 'location'
            ? 'Location'
            : kind === 'join_leave'
              ? firstText(field(payload, 'type'), 'OnPlayerJoined')
              : kind === 'portal_spawn'
                ? 'PortalSpawn'
                : kind === 'video_play'
                  ? 'VideoPlay'
                  : kind === 'resource_load'
                    ? resourceType || 'StringLoad'
                    : kind === 'event'
                      ? 'Event'
                      : kind === 'external'
                        ? 'External'
                        : firstText(field(payload, 'type'), kind);
    return {
        rowId: record?.id,
        created_at: normalizeDate(record?.createdAt || field(payload, 'createdAt', 'created_at')),
        type,
        displayName: firstText(field(payload, 'displayName', 'display_name')),
        userId: firstText(field(payload, 'userId', 'user_id')),
        location: firstText(field(payload, 'location')),
        worldId: firstText(field(payload, 'worldId', 'world_id')),
        worldName: firstText(field(payload, 'worldName', 'world_name')),
        groupName: firstText(field(payload, 'groupName', 'group_name')),
        instanceId: firstText(field(payload, 'instanceId', 'instance_id')),
        videoName: firstText(field(payload, 'videoName', 'video_name')),
        videoId: firstText(field(payload, 'videoId', 'video_id')),
        videoUrl: firstText(field(payload, 'videoUrl', 'video_url')),
        resourceUrl: firstText(field(payload, 'resourceUrl', 'resource_url')),
        resourceType,
        time: field(payload, 'time') ?? '',
        data: firstText(field(payload, 'data')),
        message: firstText(field(payload, 'message'))
    };
}

export function toWebFavorite(record) {
    const rawTargetPayload = asObject(record?.targetPayload);
    const type = firstText(record?.type);
    if (!['friend', 'world', 'vrcPlusWorld', 'avatar'].includes(type)) return null;

    const favoriteId = firstText(record?.favoriteId);
    const id = firstText(record?.id);
    if (!favoriteId || !id) return null;

    let targetPayload;
    let targetName;
    if (type === 'friend') {
        targetPayload = {
            ...selectWebUserFields(rawTargetPayload),
            id: firstText(field(rawTargetPayload, 'id'), favoriteId),
            displayName: firstText(
                field(rawTargetPayload, 'displayName', 'display_name'),
                record?.targetName,
                favoriteId
            )
        };
        targetName = targetPayload.displayName;
    } else {
        const selected =
            type === 'world' || type === 'vrcPlusWorld'
                ? selectWebWorldFields(rawTargetPayload)
                : selectWebAvatarFields(rawTargetPayload);
        targetPayload = {
            ...selected,
            id: firstText(field(rawTargetPayload, 'id'), favoriteId),
            name: firstText(field(rawTargetPayload, 'name'), record?.targetName, favoriteId)
        };
        targetName = targetPayload.name;
    }

    const tags = textArray(record?.tags).slice(0, 32);
    const derivedGroupKey = `${type}:${String(tags[0] ?? '')}`;
    const candidateGroupKey = firstText(record?.groupKey);
    return {
        id,
        type,
        favoriteId,
        tags,
        $groupKey: candidateGroupKey.startsWith(`${type}:`) ? candidateGroupKey : derivedGroupKey,
        sortOrder: Math.max(0, Math.floor(Number(record?.sortOrder) || 0)),
        createdAt: normalizeDate(record?.createdAt),
        targetName: firstText(record?.targetName, targetName),
        targetPayload
    };
}

export function toWebFavoriteGroup(record) {
    const type = firstText(record?.type);
    const name = firstText(record?.name);
    const candidateKey = firstText(record?.key);
    if (!name || !['friend', 'world', 'vrcPlusWorld', 'avatar'].includes(type)) return null;
    return {
        key: candidateKey.startsWith(`${type}:`) ? candidateKey : `${type}:${name}`,
        type,
        name,
        displayName: firstText(record?.displayName, name),
        capacity: Math.max(0, Math.floor(Number(record?.capacity) || 0)),
        count: Math.max(0, Math.floor(Number(record?.count) || 0)),
        visibility: firstText(record?.visibility),
        sortOrder: Math.max(0, Math.floor(Number(record?.sortOrder) || 0))
    };
}

export function toNotificationRow(record) {
    const payload = asObject(record?.payload);
    const data = safeNotificationData(payload);
    const details = safeNotificationDetails(payload, data);
    const senderUserId = firstText(field(payload, 'senderUserId', 'sender_user_id'), details.senderUserId);
    const senderUsername = firstText(
        field(payload, 'senderUsername', 'sender_username'),
        field(payload, 'senderDisplayName', 'sender_display_name'),
        details.senderUsername,
        details.senderDisplayName
    );
    const recipientUserId = firstText(
        field(payload, 'recipientUserId', 'recipient_user_id'),
        field(payload, 'receiverUserId', 'receiver_user_id'),
        details.recipientUserId,
        details.receiverUserId
    );
    const recipientUsername = firstText(
        field(payload, 'recipientUsername', 'recipient_username'),
        field(payload, 'receiverUsername', 'receiver_username'),
        field(payload, 'recipientDisplayName', 'recipient_display_name'),
        details.recipientUsername,
        details.receiverUsername,
        details.recipientDisplayName
    );
    const worldId = firstText(field(payload, 'worldId', 'world_id'), details.worldId);
    const location = firstText(field(payload, 'location'), details.location, worldId);
    const groupId = firstText(field(payload, 'groupId', 'group_id'), details.groupId, data.groupId, data.ownerId);
    const groupName = firstText(
        field(payload, 'groupName', 'group_name'),
        details.groupName,
        data.groupName,
        data.ownerName
    );
    const imageUrl = firstText(field(payload, 'imageUrl', 'image_url'), details.imageUrl);
    const createdAt = normalizeDate(record?.createdAt || field(payload, 'createdAt', 'created_at'));
    const row = {
        id: firstText(record?.id),
        type: firstText(record?.type, field(payload, 'type')),
        createdAt,
        created_at: createdAt,
        updatedAt: normalizeDate(field(payload, 'updatedAt', 'updated_at')),
        expiresAt: normalizeDate(field(payload, 'expiresAt', 'expires_at')),
        title: firstText(record?.title, field(payload, 'title')),
        message: firstText(record?.message, field(payload, 'message')),
        seen: record?.seen ?? field(payload, 'seen') ?? false,
        link: firstText(field(payload, 'link')),
        linkText: firstText(field(payload, 'linkText', 'link_text')),
        imageUrl,
        senderUserId,
        senderUsername,
        senderDisplayName: firstText(
            field(payload, 'senderDisplayName', 'sender_display_name'),
            details.senderDisplayName
        ),
        recipientUserId,
        recipientUsername,
        recipientDisplayName: firstText(
            field(payload, 'recipientDisplayName', 'recipient_display_name'),
            details.recipientDisplayName,
            details.receiverDisplayName
        ),
        receiverUserId: recipientUserId,
        receiverUsername: recipientUsername,
        receiverDisplayName: firstText(
            field(payload, 'receiverDisplayName', 'receiver_display_name'),
            details.receiverDisplayName,
            details.recipientDisplayName
        ),
        targetUserId: firstText(field(payload, 'targetUserId', 'target_user_id'), details.targetUserId),
        targetUsername: firstText(
            field(payload, 'targetUsername', 'target_username'),
            details.targetUsername,
            details.targetDisplayName
        ),
        targetDisplayName: firstText(
            field(payload, 'targetDisplayName', 'target_display_name'),
            details.targetDisplayName
        ),
        worldId,
        worldName: firstText(field(payload, 'worldName', 'world_name'), details.worldName),
        location,
        instanceId: firstText(field(payload, 'instanceId', 'instance_id'), details.instanceId),
        instanceName: firstText(field(payload, 'instanceName', 'instance_name'), details.instanceName),
        groupId,
        groupName,
        inviteMessage: firstText(field(payload, 'inviteMessage', 'invite_message'), details.inviteMessage),
        requestMessage: firstText(field(payload, 'requestMessage', 'request_message'), details.requestMessage),
        responseMessage: firstText(field(payload, 'responseMessage', 'response_message'), details.responseMessage),
        data,
        // Response descriptors are actions. The web deployment is read-only,
        // so even a malformed server response cannot re-enable them.
        responses: [],
        details
    };
    if (Number(field(payload, 'version')) === 2) row.version = 2;
    const expired = field(payload, 'expired');
    if (expired !== undefined && expired !== null) row.$isExpired = Boolean(expired);
    return row;
}

async function readAll(readPage) {
    const rows = [];
    for (let offset = 0; offset < MAX_READ_ROWS; offset += PAGE_SIZE) {
        const page = await readPage({ offset, limit: PAGE_SIZE });
        const items = Array.isArray(page?.items) ? page.items : [];
        rows.push(...items);
        if (items.length < PAGE_SIZE || rows.length >= Number(page?.total ?? 0)) break;
    }
    return rows.slice(0, MAX_READ_ROWS);
}

function readOnly() {
    return Promise.reject(new Error('This VRCX Web deployment is read-only.'));
}

/**
 * Named read-model operations used by existing VRCX stores in web mode.
 * It deliberately has no SQL execution method and no caller-defined URL path.
 */
export const webDatabase = {
    setMaxTableSize(limit) {
        if (Number.isSafeInteger(limit) && limit >= 1 && limit <= 10_000) nativeMaxTableSize = limit;
    },

    setSearchTableSize(limit) {
        if (Number.isSafeInteger(limit) && limit >= 1 && limit <= 100_000) nativeSearchTableSize = limit;
    },

    async getWebFriends() {
        const friends = (await readAll((page) => webApi.getFriends(page))).map(toWebUser);
        cachedWebFriends = friends;
        return friends;
    },

    async getAllUserProfiles() {
        // The friend projection already carries the original cached profile.
        // Reuse that fixed read for the existing friend-list bio/link search.
        return cachedWebFriends.map((user) => ({
            userId: user.id,
            bio: user.bio || '',
            bioLinks: Array.isArray(user.bioLinks) ? user.bioLinks : []
        }));
    },

    async lookupFeedDatabase(filters = [], vipList = [], maxEntries = nativeMaxTableSize) {
        return webApi.getNativeFeed({
            filters,
            favoritesOnly: Array.isArray(vipList) && vipList.length > 0,
            maxEntries
        });
    },

    async searchFeedDatabase(
        search,
        filters = [],
        vipList = [],
        limit = nativeSearchTableSize,
        dateFrom = '',
        dateTo = ''
    ) {
        return webApi.getNativeFeed({
            search,
            filters,
            favoritesOnly: Array.isArray(vipList) && vipList.length > 0,
            maxEntries: limit,
            dateFrom,
            dateTo
        });
    },

    async lookupGameLogDatabase(filters = [], vipList = [], maxEntries = nativeMaxTableSize) {
        return webApi.getNativeGameLog({
            filters,
            favoritesOnly: Array.isArray(vipList) && vipList.length > 0,
            maxEntries
        });
    },

    async searchGameLogDatabase(search, filters = [], vipList = [], limit = nativeSearchTableSize) {
        return webApi.getNativeGameLog({
            search,
            filters,
            favoritesOnly: Array.isArray(vipList) && vipList.length > 0,
            maxEntries: limit
        });
    },

    getSessionsLocationSegments(beforeId, limit) {
        return webApi.getNativeSessionLocations(beforeId, limit);
    },

    getSessionsLocationSegmentsByAnchor(sinceDate, limit) {
        return webApi.getNativeSessionLocationsByAnchor(sinceDate, limit);
    },

    getSessionsEventsForSegments(locationTags, afterDate, beforeDate) {
        return webApi.getNativeSessionEvents(locationTags, afterDate, beforeDate);
    },

    async getNotifications() {
        return (await readAll((page) => webApi.getNotifications(page))).map(toNotificationRow);
    },

    async getWebFavorites() {
        const [favoriteRows, groupPayload] = await Promise.all([
            readAll((page) => webApi.getFavorites(page)),
            webApi.getFavoriteGroups()
        ]);
        return {
            ready: Boolean(groupPayload?.ready),
            favorites: favoriteRows.map(toWebFavorite).filter(Boolean),
            groups: (Array.isArray(groupPayload?.items) ? groupPayload.items : [])
                .map(toWebFavoriteGroup)
                .filter(Boolean)
                .toSorted((a, b) => a.sortOrder - b.sortOrder || a.key.localeCompare(b.key))
        };
    },

    async getUserMemo(userId) {
        const memo = asObject(await webApi.getNativeUserMemo(userId));
        return {
            userId: firstText(memo.userId, userId),
            editedAt: normalizeDate(memo.editedAt),
            memo: asText(memo.memo, 262_144)
        };
    },

    async getAllUserMemos() {
        const payload = await webApi.getNativeUserMemos();
        return (Array.isArray(payload) ? payload : []).map((memo) => ({
            userId: firstText(memo?.userId),
            memo: asText(memo?.memo, 262_144)
        }));
    },

    async getWorldMemo(worldId) {
        const memo = asObject(await webApi.getNativeWorldMemo(worldId));
        return {
            worldId: firstText(memo.worldId, worldId),
            editedAt: normalizeDate(memo.editedAt),
            memo: asText(memo.memo, 262_144)
        };
    },

    async getAvatarMemoDB(avatarId) {
        const memo = asObject(await webApi.getNativeAvatarMemo(avatarId));
        return {
            avatarId: firstText(memo.avatarId, avatarId),
            editedAt: normalizeDate(memo.editedAt),
            memo: asText(memo.memo, 262_144)
        };
    },

    async getFriendLogHistory() {
        const payload = await webApi.getNativeFriendHistory();
        return Array.isArray(payload) ? payload : [];
    },

    async getFriendLogHistoryForUserId(userId, types = []) {
        const payload = await webApi.getNativeFriendHistoryForUser(userId, types);
        return Array.isArray(payload) ? payload : [];
    },

    async getUserStats(input, inCurrentWorld) {
        const value = asObject(await webApi.getNativeUserStats(input?.id, input?.displayName ?? '', inCurrentWorld));
        const previousDisplayNames = new Map();
        for (const entry of Array.isArray(value.previousDisplayNames) ? value.previousDisplayNames : []) {
            const displayName = asText(entry?.displayName, 512);
            const updatedAt = asText(entry?.updatedAt, 128);
            if (displayName) previousDisplayNames.set(displayName, updatedAt);
        }
        return {
            timeSpent: Number(value.timeSpent) || 0,
            lastSeen: asText(value.lastSeen, 128),
            joinCount: Number(value.joinCount) || 0,
            userId: firstText(value.userId, input?.id),
            previousDisplayNames
        };
    },

    async getAllUserStats() {
        const payload = await webApi.getNativeAllUserStats();
        return Array.isArray(payload) ? payload : [];
    },

    async getDateOfInstanceActivity() {
        const payload = await webApi.getInstanceActivityDates();
        return Array.isArray(payload)
            ? payload.filter((value) => typeof value === 'string').map((value) => value.slice(0, 128))
            : [];
    },

    async getInstanceActivity(startDate, endDate) {
        const payload = asObject(await webApi.getInstanceActivity(startDate, endDate));
        const currentUserData = Array.isArray(payload.currentUserData) ? payload.currentUserData : [];
        const detailEntries = Array.isArray(payload.detailData) ? payload.detailData : [];
        const detailData = new Map();
        for (const entry of detailEntries) {
            const value = asObject(entry);
            const location = firstText(value.location);
            if (!location || !Array.isArray(value.rows)) continue;
            detailData.set(location, value.rows);
        }
        return { currentUserData, detailData };
    },

    async getHotWorlds(days = 30, limit = 30) {
        const payload = await webApi.getHotWorlds(days, limit);
        return Array.isArray(payload) ? payload : [];
    },

    async getHotWorldFriendDetail(worldId, days = 30) {
        const payload = await webApi.getHotWorldFriendDetail(worldId, days);
        return Array.isArray(payload) ? payload : [];
    },

    async getMutualGraphSnapshot() {
        const payload = await webApi.getMutualGraphSnapshot();
        const snapshot = new Map();
        if (!Array.isArray(payload)) return snapshot;
        for (const entry of payload) {
            const value = asObject(entry);
            const friendId = firstText(value.friendId);
            if (!friendId || !Array.isArray(value.mutualIds)) continue;
            snapshot.set(
                friendId,
                value.mutualIds.filter((id) => typeof id === 'string')
            );
        }
        return snapshot;
    },

    async getMutualGraphMeta() {
        const payload = await webApi.getMutualGraphMeta();
        const meta = new Map();
        if (!Array.isArray(payload)) return meta;
        for (const entry of payload) {
            const value = asObject(entry);
            const friendId = firstText(value.friendId);
            if (!friendId) continue;
            meta.set(friendId, {
                lastFetchedAt: typeof value.lastFetchedAt === 'string' ? value.lastFetchedAt.slice(0, 128) : null,
                optedOut: value.optedOut === true
            });
        }
        return meta;
    },

    deleteFriendLogHistory: readOnly,
    deleteGameLogEntry: readOnly,
    deleteNotification: readOnly,
    deleteNotificationV2: readOnly,
    updateNotificationExpired: readOnly,
    seenNotificationV2: readOnly,
    addNotificationToDatabase: readOnly,
    addNotificationV2ToDatabase: readOnly,
    expireNotificationV2: readOnly,
    setFriendLogCurrent: readOnly,
    setFriendLogCurrentArray: readOnly,
    addFriendLogHistoryArray: readOnly,
    updateGamelogVideoPlayName: readOnly,
    addGamelogVideoPlayToDatabase: readOnly,
    addGamelogLocationToDatabase: readOnly,
    addOnlineOfflineToDatabase: readOnly,
    addBioToDatabase: readOnly,
    setUserProfile: readOnly,
    setAvatarMemo: readOnly,
    deleteAvatarMemo: readOnly,
    addUserNote: readOnly,
    deleteUserNote: readOnly
};
