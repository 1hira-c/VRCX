const ENTITY_PREFIXES = Object.freeze({
    userId: 'usr_',
    worldId: 'wrld_',
    avatarId: 'avtr_',
    groupId: 'grp_',
    galleryId: 'ggal_',
    roleId: 'grol_'
});

const RESOURCE_FIELDS = new Map([
    ['userPublicProfile', ['userId']],
    ['userRepresentedGroup', ['userId']],
    ['userGroups', ['userId']],
    ['userWorlds', ['userId', 'n', 'offset', 'sort', 'order', 'releaseStatus', 'user']],
    ['userFavoriteGroups', ['userId', 'n', 'offset']],
    ['userFavoriteWorlds', ['userId', 'tag', 'n', 'offset']],
    ['userOwnedAvatars', ['userId', 'n', 'offset', 'sort', 'order', 'releaseStatus']],
    ['userLicensedAvatars', ['userId', 'n', 'offset']],
    ['userRemoteAvatars', ['userId', 'n', 'offset']],
    ['groupPosts', ['groupId', 'n', 'offset']],
    ['groupMember', ['groupId', 'userId']],
    ['groupMembers', ['groupId', 'n', 'offset', 'sort', 'roleId']],
    ['groupMembersSearch', ['groupId', 'query', 'n', 'offset']],
    ['groupGallery', ['groupId', 'galleryId', 'n', 'offset']],
    ['avatarGallery', ['avatarId']],
    ['worldInstance', ['worldId', 'instanceId']]
]);

function isPlainObject(value) {
    return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function hasExactFields(value, expected) {
    if (!isPlainObject(value)) return false;
    const keys = Object.keys(value).sort();
    const fields = [...expected].sort();
    return keys.length === fields.length && keys.every((key, index) => key === fields[index]);
}

function boundedText(value, maximum, { allowEmpty = false } = {}) {
    return (
        typeof value === 'string' &&
        value.length <= maximum &&
        (allowEmpty || value.length > 0) &&
        !value.includes('\0') &&
        !Array.from(value).some((character) => /[\u0001-\u001F\u007F]/.test(character))
    );
}

function entityId(field, value) {
    const prefix = ENTITY_PREFIXES[field];
    return prefix && boundedText(value, 256) && value.startsWith(prefix) && /^[A-Za-z0-9_-]+$/.test(value);
}

function page(argumentsValue, maximum = 100) {
    return (
        Number.isInteger(argumentsValue.n) &&
        argumentsValue.n >= 1 &&
        argumentsValue.n <= maximum &&
        Number.isInteger(argumentsValue.offset) &&
        argumentsValue.offset >= 0 &&
        argumentsValue.offset <= 10_000
    );
}

function oneOf(value, allowed) {
    return typeof value === 'string' && allowed.includes(value);
}

function validateUserWorlds(value) {
    return (
        entityId('userId', value.userId) &&
        page(value, 100) &&
        oneOf(value.sort, ['name', 'updated', 'created', 'favorites', 'popularity']) &&
        oneOf(value.order, ['ascending', 'descending']) &&
        oneOf(value.releaseStatus, ['public', 'all']) &&
        oneOf(value.user, ['', 'me'])
    );
}

function validateOwnedAvatars(value) {
    return (
        entityId('userId', value.userId) &&
        page(value, 100) &&
        oneOf(value.sort, ['updated']) &&
        oneOf(value.order, ['ascending', 'descending']) &&
        oneOf(value.releaseStatus, ['public', 'private', 'all'])
    );
}

function validateGroupMembers(value) {
    return (
        entityId('groupId', value.groupId) &&
        page(value, 100) &&
        oneOf(value.sort, ['joinedAt:asc', 'joinedAt:desc']) &&
        (value.roleId === '' || entityId('roleId', value.roleId))
    );
}

function validatePublicInstanceId(value) {
    return (
        boundedText(value, 2048) &&
        !/~(?:private|friends|hidden)\(/i.test(value) &&
        !/~nonce\(/i.test(value) &&
        !value.includes('://') &&
        /^[A-Za-z0-9_~().,:+-]+$/.test(value)
    );
}

function validArguments(name, value) {
    switch (name) {
        case 'userPublicProfile':
        case 'userRepresentedGroup':
        case 'userGroups':
            return entityId('userId', value.userId);
        case 'userWorlds':
            return validateUserWorlds(value);
        case 'userFavoriteGroups':
        case 'userLicensedAvatars':
        case 'userRemoteAvatars':
            return entityId('userId', value.userId) && page(value, 100);
        case 'userFavoriteWorlds':
            return entityId('userId', value.userId) && page(value, 100) && boundedText(value.tag, 256);
        case 'userOwnedAvatars':
            return validateOwnedAvatars(value);
        case 'groupPosts':
            return entityId('groupId', value.groupId) && page(value, 100);
        case 'groupMember':
            return entityId('groupId', value.groupId) && entityId('userId', value.userId);
        case 'groupMembers':
            return validateGroupMembers(value);
        case 'groupMembersSearch':
            return (
                entityId('groupId', value.groupId) &&
                page(value, 100) &&
                boundedText(value.query, 128) &&
                value.query.trim().length >= 3
            );
        case 'groupGallery':
            return entityId('groupId', value.groupId) && entityId('galleryId', value.galleryId) && page(value, 100);
        case 'avatarGallery':
            return entityId('avatarId', value.avatarId);
        case 'worldInstance':
            return entityId('worldId', value.worldId) && validatePublicInstanceId(value.instanceId);
        default:
            return false;
    }
}

function validateEntityResourcePayload(payload) {
    if (!hasExactFields(payload, ['name', 'arguments']) || !RESOURCE_FIELDS.has(payload.name)) {
        return null;
    }
    const fields = RESOURCE_FIELDS.get(payload.name);
    if (!hasExactFields(payload.arguments, fields) || !validArguments(payload.name, payload.arguments)) {
        return null;
    }
    return {
        name: payload.name,
        arguments: payload.arguments
    };
}

module.exports = {
    entityResourceNames: Object.freeze(Array.from(RESOURCE_FIELDS.keys())),
    validateEntityResourcePayload
};
