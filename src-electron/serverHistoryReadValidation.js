const HISTORY_DATABASE_READ_NAMES = new Set([
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
const FRIEND_HISTORY_TYPES = new Set(['Friend', 'Unfriend', 'DisplayName', 'TrustLevel']);
const USER_ID = /^usr_[A-Za-z0-9_-]+$/;
const WORLD_ID = /^wrld_[A-Za-z0-9_-]+$/;
const AVATAR_ID = /^avtr_[A-Za-z0-9_-]+$/;

function isPlainObject(value) {
    return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function hasExactFields(value, fields) {
    if (!isPlainObject(value)) return false;
    const keys = Object.keys(value).sort();
    const expected = [...fields].sort();
    return keys.length === expected.length && keys.every((key, index) => key === expected[index]);
}

function isText(value, maximumLength, allowEmpty = false) {
    return (
        typeof value === 'string' &&
        (allowEmpty || value.length > 0) &&
        value.length <= maximumLength &&
        !value.includes('\0')
    );
}

function isNullableText(value, maximumLength) {
    return value === null || isText(value, maximumLength, true);
}

function isDate(value, allowEmpty = false) {
    return (allowEmpty && value === '') || (isText(value, 128) && Number.isFinite(Date.parse(value)));
}

function isInteger(value, minimum, maximum) {
    return Number.isSafeInteger(value) && value >= minimum && value <= maximum;
}

function isEntityId(value, pattern) {
    return isText(value, 256) && pattern.test(value);
}

function validateFilterArray(value, allowed) {
    if (!Array.isArray(value) || value.length > allowed.size || value.some((item) => !allowed.has(item))) {
        return null;
    }
    return [...new Set(value)];
}

function validateLookupArguments(value, filters) {
    if (!hasExactFields(value, ['filters', 'favoritesOnly', 'maxEntries'])) return null;
    const normalizedFilters = validateFilterArray(value.filters, filters);
    if (
        normalizedFilters === null ||
        typeof value.favoritesOnly !== 'boolean' ||
        !isInteger(value.maxEntries, 1, 100_000)
    ) {
        return null;
    }
    return {
        filters: normalizedFilters,
        favoritesOnly: value.favoritesOnly,
        maxEntries: value.maxEntries
    };
}

function validateSearchArguments(value, filters, { feed = false } = {}) {
    const fields = feed
        ? ['search', 'filters', 'favoritesOnly', 'maxEntries', 'dateFrom', 'dateTo']
        : ['search', 'filters', 'favoritesOnly', 'maxEntries'];
    if (!hasExactFields(value, fields)) return null;
    const normalized = validateLookupArguments(
        {
            filters: value.filters,
            favoritesOnly: value.favoritesOnly,
            maxEntries: value.maxEntries
        },
        filters
    );
    if (!normalized || !isText(value.search, 512, feed)) return null;
    if (feed) {
        if (!isDate(value.dateFrom, true) || !isDate(value.dateTo, true)) return null;
        if (value.dateFrom && value.dateTo && Date.parse(value.dateFrom) > Date.parse(value.dateTo)) return null;
        return { search: value.search, ...normalized, dateFrom: value.dateFrom, dateTo: value.dateTo };
    }
    return { search: value.search, ...normalized };
}

function validateHistoryDatabaseReadArguments(name, value) {
    if (!HISTORY_DATABASE_READ_NAMES.has(name) || !isPlainObject(value)) return null;
    switch (name) {
        case 'lookupFeedDatabase':
            return validateLookupArguments(value, FEED_FILTERS);
        case 'searchFeedDatabase':
            return validateSearchArguments(value, FEED_FILTERS, { feed: true });
        case 'lookupGameLogDatabase':
            return validateLookupArguments(value, GAME_LOG_FILTERS);
        case 'searchGameLogDatabase':
            return validateSearchArguments(value, GAME_LOG_FILTERS);
        case 'getSessionsLocationSegments':
            if (
                !hasExactFields(value, ['beforeId', 'limit']) ||
                (value.beforeId !== null && !isInteger(value.beforeId, 1, Number.MAX_SAFE_INTEGER)) ||
                !isInteger(value.limit, 1, 10_000)
            ) {
                return null;
            }
            return { beforeId: value.beforeId, limit: value.limit };
        case 'getSessionsLocationSegmentsByAnchor':
            if (
                !hasExactFields(value, ['sinceDate', 'limit']) ||
                !isDate(value.sinceDate) ||
                !isInteger(value.limit, 1, 10_000)
            ) {
                return null;
            }
            return { sinceDate: value.sinceDate, limit: value.limit };
        case 'getSessionsEventsForSegments': {
            if (
                !hasExactFields(value, ['locationTags', 'afterDate', 'beforeDate']) ||
                !Array.isArray(value.locationTags) ||
                value.locationTags.length < 1 ||
                value.locationTags.length > 1_000 ||
                value.locationTags.some((location) => !isText(location, 2_048)) ||
                !isDate(value.afterDate) ||
                !isDate(value.beforeDate) ||
                Date.parse(value.afterDate) > Date.parse(value.beforeDate)
            ) {
                return null;
            }
            return {
                locationTags: [...new Set(value.locationTags)],
                afterDate: value.afterDate,
                beforeDate: value.beforeDate
            };
        }
        case 'getUserMemo':
            return hasExactFields(value, ['userId']) && isEntityId(value.userId, USER_ID)
                ? { userId: value.userId }
                : null;
        case 'getWorldMemo':
            return hasExactFields(value, ['worldId']) && isEntityId(value.worldId, WORLD_ID)
                ? { worldId: value.worldId }
                : null;
        case 'getAvatarMemoDB':
            return hasExactFields(value, ['avatarId']) && isEntityId(value.avatarId, AVATAR_ID)
                ? { avatarId: value.avatarId }
                : null;
        case 'getAllUserMemos':
        case 'getAllUserStats':
        case 'getFriendLogHistory':
            return hasExactFields(value, []) ? {} : null;
        case 'getUserStats':
            if (
                !hasExactFields(value, ['userId', 'displayName', 'inCurrentWorld']) ||
                !isEntityId(value.userId, USER_ID) ||
                !isText(value.displayName, 512, true) ||
                typeof value.inCurrentWorld !== 'boolean'
            ) {
                return null;
            }
            return {
                userId: value.userId,
                displayName: value.displayName,
                inCurrentWorld: value.inCurrentWorld
            };
        case 'getFriendLogHistoryForUserId': {
            if (
                !hasExactFields(value, ['userId', 'types']) ||
                !isEntityId(value.userId, USER_ID) ||
                !Array.isArray(value.types) ||
                value.types.length > FRIEND_HISTORY_TYPES.size ||
                value.types.some((type) => !FRIEND_HISTORY_TYPES.has(type))
            ) {
                return null;
            }
            return { userId: value.userId, types: [...new Set(value.types)] };
        }
        default:
            return null;
    }
}

function normalizeTextArray(value, maxItems, maxLength) {
    if (!Array.isArray(value) || value.length > maxItems || value.some((item) => !isText(item, maxLength, true))) {
        throw new Error('Invalid renderer history database-read response');
    }
    return [...value];
}

function normalizeNullableNumber(value, maximum = Number.MAX_SAFE_INTEGER) {
    if (value === null) return null;
    if (!Number.isFinite(value) || Math.abs(value) > maximum) {
        throw new Error('Invalid renderer history database-read response');
    }
    return value;
}

const FEED_ROW_FIELDS = [
    'rowId',
    'created_at',
    'userId',
    'displayName',
    'type',
    'location',
    'worldName',
    'previousLocation',
    'time',
    'groupName',
    'status',
    'statusDescription',
    'previousStatus',
    'previousStatusDescription',
    'bio',
    'previousBio',
    'bioLinks',
    'previousBioLinks',
    'ownerId',
    'avatarName',
    'currentAvatarImageUrl',
    'currentAvatarThumbnailImageUrl',
    'previousCurrentAvatarImageUrl',
    'previousCurrentAvatarThumbnailImageUrl'
];

function normalizeFeedRow(value) {
    if (
        !hasExactFields(value, FEED_ROW_FIELDS) ||
        !isInteger(value.rowId, 0, Number.MAX_SAFE_INTEGER) ||
        !isText(value.created_at, 128) ||
        !FEED_FILTERS.has(value.type) ||
        !isNullableText(value.userId, 256) ||
        !isNullableText(value.displayName, 512)
    ) {
        throw new Error('Invalid renderer history database-read response');
    }
    const shortFields = [
        'location',
        'worldName',
        'previousLocation',
        'groupName',
        'status',
        'statusDescription',
        'previousStatus',
        'previousStatusDescription',
        'ownerId',
        'avatarName'
    ];
    const urlFields = [
        'currentAvatarImageUrl',
        'currentAvatarThumbnailImageUrl',
        'previousCurrentAvatarImageUrl',
        'previousCurrentAvatarThumbnailImageUrl'
    ];
    if (
        shortFields.some((field) => !isNullableText(value[field], 2_048)) ||
        urlFields.some((field) => !isNullableText(value[field], 16_384)) ||
        !isNullableText(value.bio, 65_536) ||
        !isNullableText(value.previousBio, 65_536)
    ) {
        throw new Error('Invalid renderer history database-read response');
    }
    return {
        ...value,
        time: normalizeNullableNumber(value.time, 366 * 24 * 60 * 60 * 1_000),
        bioLinks: normalizeTextArray(value.bioLinks, 64, 2_048),
        previousBioLinks: normalizeTextArray(value.previousBioLinks, 64, 2_048)
    };
}

const GAME_LOG_ROW_FIELDS = [
    'rowId',
    'created_at',
    'type',
    'displayName',
    'location',
    'userId',
    'time',
    'worldId',
    'worldName',
    'groupName',
    'instanceId',
    'videoUrl',
    'videoName',
    'videoId',
    'resourceUrl',
    'resourceType',
    'data',
    'message'
];

function normalizeGameLogRow(value) {
    if (
        !hasExactFields(value, GAME_LOG_ROW_FIELDS) ||
        !isInteger(value.rowId, 0, Number.MAX_SAFE_INTEGER) ||
        !isText(value.created_at, 128) ||
        !GAME_LOG_FILTERS.has(value.type)
    ) {
        throw new Error('Invalid renderer history database-read response');
    }
    const shortFields = [
        'displayName',
        'location',
        'userId',
        'worldId',
        'worldName',
        'groupName',
        'instanceId',
        'videoName',
        'videoId',
        'resourceType'
    ];
    if (
        shortFields.some((field) => !isNullableText(value[field], 2_048)) ||
        !isNullableText(value.videoUrl, 65_536) ||
        !isNullableText(value.resourceUrl, 65_536) ||
        !isNullableText(value.data, 65_536) ||
        !isNullableText(value.message, 65_536)
    ) {
        throw new Error('Invalid renderer history database-read response');
    }
    return {
        ...value,
        time: normalizeNullableNumber(value.time, 366 * 24 * 60 * 60 * 1_000)
    };
}

function normalizeSessionLocation(value) {
    const fields = ['id', 'created_at', 'location', 'worldId', 'worldName', 'time', 'groupName'];
    if (
        !hasExactFields(value, fields) ||
        !isInteger(value.id, 0, Number.MAX_SAFE_INTEGER) ||
        !isText(value.created_at, 128) ||
        !isText(value.location, 2_048) ||
        !isNullableText(value.worldId, 256) ||
        !isNullableText(value.worldName, 2_048) ||
        !isNullableText(value.groupName, 2_048)
    ) {
        throw new Error('Invalid renderer history database-read response');
    }
    return { ...value, time: normalizeNullableNumber(value.time, 366 * 24 * 60 * 60 * 1_000) };
}

function normalizeSessionEvent(value) {
    const fields = ['type', 'created_at', 'displayName', 'userId', 'location', 'videoUrl', 'videoName', 'videoId'];
    if (
        !hasExactFields(value, fields) ||
        !new Set(['OnPlayerJoined', 'OnPlayerLeft', 'VideoPlay']).has(value.type) ||
        !isText(value.created_at, 128) ||
        !isText(value.location, 2_048) ||
        !isNullableText(value.displayName, 512) ||
        !isNullableText(value.userId, 256) ||
        !isNullableText(value.videoUrl, 65_536) ||
        !isNullableText(value.videoName, 2_048) ||
        !isNullableText(value.videoId, 2_048)
    ) {
        throw new Error('Invalid renderer history database-read response');
    }
    return { ...value };
}

function normalizeMemo(value, idField, idPattern, expectedId) {
    if (
        !hasExactFields(value, [idField, 'editedAt', 'memo']) ||
        (value[idField] !== '' && !isEntityId(value[idField], idPattern)) ||
        (value[idField] !== '' && value[idField] !== expectedId) ||
        !isNullableText(value.editedAt, 128) ||
        !isNullableText(value.memo, 262_144)
    ) {
        throw new Error('Invalid renderer history database-read response');
    }
    return { ...value };
}

function normalizeFriendHistoryRow(value) {
    const fields = [
        'rowId',
        'created_at',
        'type',
        'userId',
        'displayName',
        'previousDisplayName',
        'trustLevel',
        'previousTrustLevel',
        'friendNumber'
    ];
    if (
        !hasExactFields(value, fields) ||
        !isInteger(value.rowId, 0, Number.MAX_SAFE_INTEGER) ||
        !isText(value.created_at, 128) ||
        !FRIEND_HISTORY_TYPES.has(value.type) ||
        !isEntityId(value.userId, USER_ID) ||
        !isNullableText(value.displayName, 512) ||
        !isNullableText(value.previousDisplayName, 512) ||
        !isNullableText(value.trustLevel, 128) ||
        !isNullableText(value.previousTrustLevel, 128) ||
        (value.friendNumber !== null && !isInteger(value.friendNumber, 0, Number.MAX_SAFE_INTEGER))
    ) {
        throw new Error('Invalid renderer history database-read response');
    }
    return { ...value };
}

function normalizeHistoryArray(value, maximum, normalizeItem) {
    if (!Array.isArray(value) || value.length > maximum) {
        throw new Error('Invalid renderer history database-read response');
    }
    return value.map(normalizeItem);
}

function normalizeHistoryDatabaseReadResult(name, value, requestArguments) {
    switch (name) {
        case 'lookupFeedDatabase':
        case 'searchFeedDatabase':
            return normalizeHistoryArray(value, requestArguments.maxEntries, normalizeFeedRow);
        case 'lookupGameLogDatabase':
        case 'searchGameLogDatabase':
            return normalizeHistoryArray(value, requestArguments.maxEntries, normalizeGameLogRow);
        case 'getSessionsLocationSegments':
        case 'getSessionsLocationSegmentsByAnchor':
            return normalizeHistoryArray(value, requestArguments.limit, normalizeSessionLocation);
        case 'getSessionsEventsForSegments':
            return normalizeHistoryArray(value, 100_000, normalizeSessionEvent);
        case 'getUserMemo':
            return normalizeMemo(value, 'userId', USER_ID, requestArguments.userId);
        case 'getWorldMemo':
            return normalizeMemo(value, 'worldId', WORLD_ID, requestArguments.worldId);
        case 'getAvatarMemoDB':
            return normalizeMemo(value, 'avatarId', AVATAR_ID, requestArguments.avatarId);
        case 'getAllUserMemos':
            return normalizeHistoryArray(value, 100_000, (entry) => {
                if (
                    !hasExactFields(entry, ['userId', 'memo']) ||
                    !isEntityId(entry.userId, USER_ID) ||
                    !isNullableText(entry.memo, 262_144)
                ) {
                    throw new Error('Invalid renderer history database-read response');
                }
                return { ...entry };
            });
        case 'getUserStats': {
            if (
                !hasExactFields(value, ['timeSpent', 'lastSeen', 'joinCount', 'userId', 'previousDisplayNames']) ||
                !Number.isFinite(value.timeSpent) ||
                value.timeSpent < 0 ||
                value.timeSpent > Number.MAX_SAFE_INTEGER ||
                !isNullableText(value.lastSeen, 128) ||
                !isInteger(value.joinCount, 0, Number.MAX_SAFE_INTEGER) ||
                value.userId !== requestArguments.userId ||
                !Array.isArray(value.previousDisplayNames) ||
                value.previousDisplayNames.length > 10_000
            ) {
                throw new Error('Invalid renderer history database-read response');
            }
            return {
                ...value,
                previousDisplayNames: value.previousDisplayNames.map((entry) => {
                    if (
                        !hasExactFields(entry, ['displayName', 'updatedAt']) ||
                        !isText(entry.displayName, 512, true) ||
                        !isText(entry.updatedAt, 128)
                    ) {
                        throw new Error('Invalid renderer history database-read response');
                    }
                    return { ...entry };
                })
            };
        }
        case 'getAllUserStats':
            return normalizeHistoryArray(value, 100_000, (entry) => {
                if (
                    !hasExactFields(entry, ['lastSeen', 'userId', 'timeSpent', 'joinCount', 'displayName']) ||
                    !isNullableText(entry.lastSeen, 128) ||
                    (entry.userId !== null && !isEntityId(entry.userId, USER_ID)) ||
                    !Number.isFinite(entry.timeSpent) ||
                    entry.timeSpent < 0 ||
                    entry.timeSpent > Number.MAX_SAFE_INTEGER ||
                    !isInteger(entry.joinCount, 0, Number.MAX_SAFE_INTEGER) ||
                    !isNullableText(entry.displayName, 512)
                ) {
                    throw new Error('Invalid renderer history database-read response');
                }
                return { ...entry };
            });
        case 'getFriendLogHistory':
            return normalizeHistoryArray(value, 100_000, normalizeFriendHistoryRow);
        case 'getFriendLogHistoryForUserId': {
            const rows = normalizeHistoryArray(value, 100_000, normalizeFriendHistoryRow);
            if (rows.some((row) => row.userId !== requestArguments.userId)) {
                throw new Error('Invalid renderer history database-read response');
            }
            return rows;
        }
        default:
            throw new Error('Invalid renderer history database-read response');
    }
}

module.exports = {
    HISTORY_DATABASE_READ_NAMES,
    normalizeHistoryDatabaseReadResult,
    validateHistoryDatabaseReadArguments
};
