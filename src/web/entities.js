import { itemsFrom, webApi } from './api';

const entityTypes = new Set(['user', 'world', 'avatar', 'group']);

function asObject(value) {
    return value && typeof value === 'object' && !Array.isArray(value) ? value : {};
}

function snakeCase(name) {
    return name.replace(/[A-Z]/g, (letter) => `_${letter.toLowerCase()}`);
}

function field(source, name) {
    const object = asObject(source);
    return object[name] ?? object[snakeCase(name)];
}

function firstString(...values) {
    return values.find((value) => typeof value === 'string' && value.length > 0) || '';
}

function copyScalars(source, names) {
    const selected = {};
    for (const name of names) {
        const value = field(source, name);
        if (value === null || ['string', 'number', 'boolean'].includes(typeof value)) {
            selected[name] = value;
        }
    }
    return selected;
}

function textArray(value) {
    return Array.isArray(value) ? value.filter((item) => typeof item === 'string') : [];
}

function objectArray(value, names) {
    if (!Array.isArray(value)) return [];
    return value.map((item) => copyScalars(item, names)).filter((item) => Object.keys(item).length > 0);
}

function selectAvatarStyles(value) {
    const source = asObject(value);
    return {
        ...copyScalars(source, ['primary', 'secondary']),
        supplementary: textArray(field(source, 'supplementary'))
    };
}

function selectGroupGalleries(value) {
    if (!Array.isArray(value)) return [];
    return value.slice(0, 256).map((gallery) => ({
        ...copyScalars(gallery, ['id', 'name', 'description', 'membersOnly', 'createdAt', 'updatedAt']),
        roleIdsToAutoApprove: textArray(field(gallery, 'roleIdsToAutoApprove')),
        roleIdsToManage: textArray(field(gallery, 'roleIdsToManage')),
        roleIdsToSubmit: textArray(field(gallery, 'roleIdsToSubmit')),
        roleIdsToView: textArray(field(gallery, 'roleIdsToView'))
    }));
}

const userScalarFields = Object.freeze([
    'username',
    'bio',
    'status',
    'statusDescription',
    'state',
    'location',
    'travelingToLocation',
    'travelingToWorld',
    'travelingToInstance',
    'worldId',
    'worldName',
    'instanceId',
    'platform',
    'last_platform',
    'last_activity',
    'last_login',
    'date_joined',
    'developerType',
    'friendRequestStatus',
    'isFriend',
    'pronouns',
    'ageVerified',
    'ageVerificationStatus',
    'userIcon',
    'iconUrl',
    'iconFrame',
    'profilePicOverride',
    'profilePicOverrideThumbnail',
    'currentAvatarImageUrl',
    'currentAvatarThumbnailImageUrl',
    'bannerColor',
    'bannerType',
    'bannerUrl',
    'profileEffect',
    'nameplateEffect',
    'isEconomyCreator',
    'allowAvatarCopying',
    'hasVrcPlus',
    'hasSharedConnectionsOptOut',
    'homeLocation',
    'note',
    'friendNumber'
]);

/** Select only user fields rendered by the existing VRCX read-only views. */
export function selectWebUserFields(source) {
    const payload = asObject(source);
    const friendNumber = field(payload, 'friendNumber') ?? payload.$friendNumber;
    return {
        ...copyScalars(payload, userScalarFields),
        ...(Number.isInteger(friendNumber) && friendNumber >= 0 ? { friendNumber } : {}),
        tags: textArray(field(payload, 'tags')),
        bioLinks: textArray(field(payload, 'bioLinks')),
        currentAvatarTags: textArray(field(payload, 'currentAvatarTags')),
        badges: objectArray(field(payload, 'badges'), [
            'badgeId',
            'badgeName',
            'badgeDescription',
            'badgeImageUrl',
            'hidden',
            'showcased'
        ]),
        pastDisplayNames: textArray(field(payload, 'pastDisplayNames'))
    };
}

function selectUnityPackages(source) {
    return objectArray(field(source, 'unityPackages'), [
        'id',
        'platform',
        'variant',
        'unityVersion',
        'unitySortNumber',
        'assetUrl',
        'version',
        'impostorizerVersion',
        'performanceRating',
        'created_at',
        'updated_at'
    ]);
}

function selectPublishedListings(source) {
    return objectArray(field(source, 'publishedListings'), [
        'id',
        'displayName',
        'description',
        'imageId',
        'imageUrl',
        'priceTokens',
        'listingType',
        'productId'
    ]);
}

export function selectWebWorldFields(source) {
    const payload = asObject(source);
    return {
        ...copyScalars(payload, [
            'authorId',
            'authorName',
            'description',
            'imageUrl',
            'thumbnailImageUrl',
            'releaseStatus',
            'capacity',
            'recommendedCapacity',
            'occupants',
            'publicOccupants',
            'privateOccupants',
            'favorites',
            'visits',
            'popularity',
            'heat',
            'created_at',
            'updated_at',
            'publicationDate',
            'labsPublicationDate',
            'version',
            'organization',
            'previewYoutubeId',
            'featured'
        ]),
        tags: textArray(field(payload, 'tags')),
        unityPackages: selectUnityPackages(payload),
        slimInstances: objectArray(field(payload, 'slimInstances'), [
            'id',
            'instanceId',
            'name',
            'type',
            'region',
            'photonRegion',
            'groupAccessType',
            'location',
            'n_users',
            'userCount',
            'occupantCount',
            'capacity',
            'recommendedCapacity',
            'queueEnabled',
            'queueSize',
            'full',
            'active',
            'ageGate',
            'closedAt'
        ])
    };
}

export function selectWebAvatarFields(source) {
    const payload = asObject(source);
    return {
        ...copyScalars(payload, [
            'authorId',
            'authorName',
            'description',
            'imageUrl',
            'thumbnailImageUrl',
            'releaseStatus',
            'created_at',
            'updated_at',
            'version',
            'featured',
            'searchable',
            'productId',
            'lowestPrice',
            'highestPrice'
        ]),
        tags: textArray(field(payload, 'tags')),
        styles: selectAvatarStyles(field(payload, 'styles')),
        unityPackages: selectUnityPackages(payload),
        publishedListings: selectPublishedListings(payload)
    };
}

export function selectWebGroupFields(source) {
    const payload = asObject(source);
    const myMember = asObject(field(payload, 'myMember'));
    const roles = Array.isArray(field(payload, 'roles')) ? field(payload, 'roles') : [];
    return {
        ...copyScalars(payload, [
            'shortCode',
            'discriminator',
            'description',
            'rules',
            'ownerId',
            'iconId',
            'iconUrl',
            'bannerId',
            'bannerUrl',
            'privacy',
            'joinState',
            'membershipStatus',
            'memberVisibility',
            'isRepresenting',
            'memberCount',
            'onlineMemberCount',
            'isVerified',
            'isSearchable',
            'storeId',
            'createdAt',
            'updatedAt'
        ]),
        languages: textArray(field(payload, 'languages')),
        tags: textArray(field(payload, 'tags')),
        links: textArray(field(payload, 'links')),
        roleIds: textArray(field(payload, 'roleIds')),
        roles: roles.map((role) => ({
            ...copyScalars(role, [
                'id',
                'name',
                'description',
                'order',
                'isManagementRole',
                'isDefault',
                'defaultRole',
                'isSelfAssignable',
                'createdAt',
                'updatedAt'
            ]),
            permissions: textArray(field(role, 'permissions'))
        })),
        myMember: {
            ...copyScalars(myMember, [
                'id',
                'groupId',
                'userId',
                'membershipStatus',
                'visibility',
                'isRepresenting',
                'isSubscribedToAnnouncements',
                'isSubscribedToEventAnnouncements',
                'joinedAt',
                'bannedAt'
            ]),
            roleIds: textArray(field(myMember, 'roleIds')),
            permissions: textArray(field(myMember, 'permissions'))
        },
        galleries: selectGroupGalleries(field(payload, 'galleries'))
    };
}

const selectors = Object.freeze({
    user: selectWebUserFields,
    world: selectWebWorldFields,
    avatar: selectWebAvatarFields,
    group: selectWebGroupFields
});

/**
 * Convert the fixed SearchDocument contract into the existing VRCX entity model.
 * No unknown payload field is copied into the browser model.
 */
export function mapWebEntityDocument(document, expectedType) {
    const source = asObject(document);
    const type = firstString(source.type, expectedType).toLowerCase();
    if (!entityTypes.has(type) || (expectedType && type !== expectedType)) {
        throw new Error('Unexpected entity type from VRCX Web Server.');
    }

    const payload = asObject(source.payload);
    const selected = selectors[type](payload);
    const id = firstString(source.id, field(payload, 'id'));
    if (!id) throw new Error('Entity response did not include an id.');

    if (type === 'user') {
        return {
            ...selected,
            id,
            displayName: firstString(source.name, field(payload, 'displayName'), id),
            bio: firstString(selected.bio, source.description),
            isFriend: Boolean(selected.isFriend)
        };
    }

    const entity = {
        ...selected,
        id,
        name: firstString(source.name, field(payload, 'name'), id),
        description: firstString(selected.description, source.description)
    };
    if (type === 'world' || type === 'avatar') {
        entity.authorId = firstString(selected.authorId, source.ownerId);
        entity.updated_at = firstString(selected.updated_at, source.updatedAt);
    } else {
        entity.ownerId = firstString(selected.ownerId, source.ownerId);
        entity.updatedAt = firstString(selected.updatedAt, source.updatedAt);
    }
    return entity;
}

export async function searchWebEntities(type, query, page, options = {}) {
    if (!entityTypes.has(type)) throw new Error('Unsupported web entity type.');
    const normalizedQuery = String(query || '').trim();
    const hasWorldCategory =
        type === 'world' &&
        options &&
        typeof options === 'object' &&
        Object.values(options).some((value) => value !== undefined && value !== null && value !== '');
    if (!normalizedQuery && !hasWorldCategory) {
        return { items: [], total: 0, offset: page?.offset || 0, limit: page?.limit || 0 };
    }

    const response = await webApi.search(type, normalizedQuery, page, options);
    return {
        items: itemsFrom(response).map((document) => mapWebEntityDocument(document, type)),
        total: Number(response?.total) || 0,
        offset: Number(response?.offset) || 0,
        limit: Number(response?.limit) || page?.limit || 0
    };
}

export async function getWebEntity(type, id) {
    if (!entityTypes.has(type)) throw new Error('Unsupported web entity type.');
    const entityId = String(id || '');
    if (!entityId) throw new Error('Entity id is required.');
    const document = await webApi.getSearchDetail(type, entityId);
    const entity = mapWebEntityDocument(document, type);
    if (entity.id !== entityId) throw new Error('Entity response id did not match the request.');
    return entity;
}
