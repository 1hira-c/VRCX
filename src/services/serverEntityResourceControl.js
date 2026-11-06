import { avatarRequest, favoriteRequest, groupRequest, instanceRequest, userRequest, worldRequest } from '../api';
import { lookupAvatars } from '../coordinators/avatarCoordinator';
import { selectWebAvatarFields, selectWebUserFields, selectWebWorldFields } from '../web/entities';
import { watchState } from './watchState';

const RESOURCE_NAMES = new Set([
    'userPublicProfile',
    'userRepresentedGroup',
    'userGroups',
    'userWorlds',
    'userFavoriteGroups',
    'userFavoriteWorlds',
    'userOwnedAvatars',
    'userLicensedAvatars',
    'userRemoteAvatars',
    'groupPosts',
    'groupMember',
    'groupMembers',
    'groupMembersSearch',
    'groupGallery',
    'avatarGallery',
    'worldInstance'
]);

function asObject(value) {
    return value && typeof value === 'object' && !Array.isArray(value) ? value : {};
}

function field(source, name) {
    const object = asObject(source);
    const snakeCase = name.replace(/[A-Z]/g, (letter) => `_${letter.toLowerCase()}`);
    return object[name] ?? object[snakeCase];
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

function textArray(value, limit = 256) {
    return Array.isArray(value) ? value.filter((item) => typeof item === 'string').slice(0, limit) : [];
}

function selectPublicProfile(value) {
    const source = asObject(value);
    return {
        ...copyScalars(source, [
            'id',
            'displayName',
            'bio',
            'status',
            'statusDescription',
            'pronouns',
            'ageVerified',
            'ageVerificationStatus',
            'bannerColor',
            'bannerCustomUrl',
            'bannerType',
            'bannerUrl',
            'currentAvatar',
            'currentAvatarAuthorName',
            'currentAvatarImageUrl',
            'currentAvatarName',
            'currentAvatarThumbnailImageUrl',
            'hasVrcPlus',
            'iconFrame',
            'iconType',
            'iconUrl',
            'isEconomyCreator',
            'nameplateEffect',
            'profileEffect',
            'themeButtonColor',
            'themeIconColor',
            'themeId',
            'themeSubtextColor',
            'totalPublicWorldsCount',
            'userIcon'
        ]),
        bioLinks: textArray(field(source, 'bioLinks'), 64),
        currentAvatarTags: textArray(field(source, 'currentAvatarTags')),
        languages: textArray(field(source, 'languages'), 64),
        trustTags: textArray(field(source, 'trustTags')),
        badges: Array.isArray(field(source, 'badges'))
            ? field(source, 'badges')
                  .slice(0, 256)
                  .map((badge) =>
                      copyScalars(badge, [
                          'badgeId',
                          'badgeName',
                          'badgeDescription',
                          'badgeImageUrl',
                          'hidden',
                          'showcased'
                      ])
                  )
            : [],
        themes: Array.isArray(field(source, 'themes'))
            ? field(source, 'themes')
                  .slice(0, 64)
                  .map((theme) => copyScalars(theme, ['id', 'name', 'buttonColor', 'iconColor', 'subtextColor']))
            : []
    };
}

function selectRepresentedGroup(value) {
    return copyScalars(value, [
        'id',
        'groupId',
        'name',
        'shortCode',
        'discriminator',
        'description',
        'ownerId',
        'iconId',
        'iconUrl',
        'bannerId',
        'bannerUrl',
        'privacy',
        'memberVisibility',
        'memberCount',
        'isRepresenting'
    ]);
}

function selectLimitedGroup(value) {
    return copyScalars(value, [
        'id',
        'groupId',
        'name',
        'shortCode',
        'discriminator',
        'description',
        'ownerId',
        'iconId',
        'iconUrl',
        'bannerId',
        'bannerUrl',
        'privacy',
        'memberVisibility',
        'memberCount',
        'membershipStatus',
        'isRepresenting',
        'mutualGroup',
        'lastPostCreatedAt',
        'lastPostReadAt',
        'storeId'
    ]);
}

function selectWorld(value) {
    const source = asObject(value);
    return {
        ...selectWebWorldFields(source),
        ...copyScalars(source, ['id', 'name', 'favoriteGroup', 'favoriteId', 'storeId'])
    };
}

function selectAvatar(value) {
    const source = asObject(value);
    return {
        ...selectWebAvatarFields(source),
        ...copyScalars(source, ['id', 'name', 'listingDate', 'lock', 'unityPackageUrl'])
    };
}

function selectFavoriteGroup(value) {
    const source = asObject(value);
    return {
        ...copyScalars(source, [
            'id',
            'name',
            'displayName',
            'ownerId',
            'ownerDisplayName',
            'type',
            'visibility',
            'numFavorites'
        ]),
        tags: textArray(field(source, 'tags'))
    };
}

function selectGroupPost(value) {
    const source = asObject(value);
    return {
        ...copyScalars(source, [
            'id',
            'groupId',
            'authorId',
            'editorId',
            'title',
            'text',
            'imageId',
            'imageUrl',
            'visibility',
            'createdAt',
            'updatedAt'
        ]),
        roleIds: textArray(field(source, 'roleIds'))
    };
}

function selectMemberUser(value) {
    const source = asObject(value);
    return {
        ...selectWebUserFields(source),
        ...copyScalars(source, ['id', 'displayName', 'bannerColor', 'bannerType', 'bannerUrl'])
    };
}

function selectGroupMember(value) {
    const source = asObject(value);
    const user = field(source, 'user');
    return {
        ...copyScalars(source, [
            'id',
            'groupId',
            'userId',
            'acceptedByDisplayName',
            'acceptedById',
            'bannedAt',
            'createdAt',
            'hasJoinedFromPurchase',
            'isRepresenting',
            'isSubscribedToAnnouncements',
            'isSubscribedToEventAnnouncements',
            'joinedAt',
            'lastPostReadAt',
            'managerNotes',
            'membershipStatus',
            'visibility'
        ]),
        roleIds: textArray(field(source, 'roleIds')),
        mRoleIds: textArray(field(source, 'mRoleIds')),
        ...(user && typeof user === 'object' ? { user: selectMemberUser(user) } : {})
    };
}

function selectGroupGalleryImage(value) {
    return copyScalars(value, [
        'id',
        'groupId',
        'galleryId',
        'fileId',
        'imageUrl',
        'approved',
        'approvedAt',
        'approvedByUserId',
        'createdAt',
        'submittedByUserId'
    ]);
}

function selectFileData(value) {
    return copyScalars(value, ['url', 'fileName', 'sizeInBytes', 'status', 'category']);
}

function selectGalleryFile(value) {
    const source = asObject(value);
    const versions = Array.isArray(field(source, 'versions')) ? field(source, 'versions') : [];
    return {
        ...copyScalars(source, [
            'id',
            'name',
            'ownerId',
            'extension',
            'mimeType',
            'animationStyle',
            'frames',
            'framesOverTime',
            'loopStyle',
            'maskTag',
            'order'
        ]),
        tags: textArray(field(source, 'tags')),
        versions: versions.slice(0, 256).map((version) => ({
            ...copyScalars(version, ['created_at', 'deleted', 'status', 'version']),
            ...(field(version, 'file') ? { file: selectFileData(field(version, 'file')) } : {})
        }))
    };
}

function unsafePrivateLocation(value) {
    if (typeof value !== 'string') return false;
    return /~(?:private|friends|hidden)\(/i.test(value) || /~nonce\(/i.test(value);
}

function selectPublicWorldInstance(value) {
    const source = asObject(value);
    const type = field(source, 'type');
    const groupAccessType = field(source, 'groupAccessType');
    const location = field(source, 'location');
    if (unsafePrivateLocation(location) || (type !== 'public' && !(type === 'group' && groupAccessType === 'public'))) {
        return null;
    }
    return {
        ...copyScalars(source, [
            'id',
            'instanceId',
            'worldId',
            'location',
            'name',
            'displayName',
            'description',
            'type',
            'groupAccessType',
            'ownerId',
            'creatorId',
            'region',
            'photonRegion',
            'capacity',
            'recommendedCapacity',
            'n_users',
            'userCount',
            'queueEnabled',
            'queueSize',
            'full',
            'active',
            'permanent',
            'ageGate',
            'closedAt',
            'roleRestricted',
            'minimumAvatarPerformance',
            'dominantLanguage'
        ]),
        languages: textArray(field(source, 'languages'), 64),
        languagesIso639: textArray(field(source, 'languagesIso639'), 64),
        tags: textArray(field(source, 'tags')),
        vibeIds: textArray(field(source, 'vibeIds'))
    };
}

function errorCode(error) {
    const status = Number(error?.status);
    if (Number.isInteger(status) && status >= 400 && status <= 599) {
        return status === 404 ? 'not_found' : `http_${status}`;
    }
    if (error?.name === 'AbortError') return 'request_aborted';
    if (error?.name === 'TypeError') return 'network_error';
    return 'upstream_error';
}

function success(result) {
    return { ok: true, result, errorCode: null };
}

function failure(code) {
    return { ok: false, result: null, errorCode: code };
}

async function getResource(stores, payload) {
    const args = payload.arguments;
    switch (payload.name) {
        case 'userPublicProfile':
            return selectPublicProfile((await userRequest.getPublicProfile({ userId: args.userId })).json);
        case 'userRepresentedGroup':
            return selectRepresentedGroup((await groupRequest.getRepresentedGroup({ userId: args.userId })).json);
        case 'userGroups':
            return (await groupRequest.getGroups({ userId: args.userId })).json.map(selectLimitedGroup);
        case 'userWorlds':
            return (await worldRequest.getWorlds(args)).json.map(selectWorld);
        case 'userFavoriteGroups':
            return (
                await favoriteRequest.getFavoriteGroups({ ownerId: args.userId, n: args.n, offset: args.offset })
            ).json.map(selectFavoriteGroup);
        case 'userFavoriteWorlds':
            return (
                await favoriteRequest.getFavoriteWorlds({
                    userId: args.userId,
                    tag: args.tag,
                    n: args.n,
                    offset: args.offset
                })
            ).json.map(selectWorld);
        case 'userOwnedAvatars':
            if (args.userId !== stores.user.currentUser.id) throw Object.assign(new Error(), { code: 'wrong_user' });
            return (
                await avatarRequest.getAvatars({
                    n: args.n,
                    offset: args.offset,
                    sort: args.sort,
                    order: args.order,
                    releaseStatus: args.releaseStatus,
                    user: 'me'
                })
            ).json.map(selectAvatar);
        case 'userLicensedAvatars':
            if (args.userId !== stores.user.currentUser.id) throw Object.assign(new Error(), { code: 'wrong_user' });
            return (await avatarRequest.getLicensedAvatars({ n: args.n, offset: args.offset })).json.map(selectAvatar);
        case 'userRemoteAvatars': {
            const avatars = Array.from((await lookupAvatars('authorId', args.userId)).values());
            return avatars.slice(args.offset, args.offset + args.n).map(selectAvatar);
        }
        case 'groupPosts': {
            const response = (await groupRequest.getGroupPosts(args)).json;
            const posts = Array.isArray(response?.posts) ? response.posts.map(selectGroupPost) : [];
            return {
                posts,
                total:
                    Number.isInteger(response?.total) && response.total >= posts.length ? response.total : posts.length
            };
        }
        case 'groupMember':
            return selectGroupMember((await groupRequest.getGroupMember(args)).json);
        case 'groupMembers':
            return (await groupRequest.getGroupMembers(args)).json.map(selectGroupMember);
        case 'groupMembersSearch': {
            const response = (await groupRequest.getGroupMembersSearch(args)).json;
            const results = Array.isArray(response?.results) ? response.results.map(selectGroupMember) : [];
            return {
                results,
                total:
                    Number.isInteger(response?.total) && response.total >= results.length
                        ? response.total
                        : results.length
            };
        }
        case 'groupGallery': {
            const response = (await groupRequest.getGroupGallery(args)).json;
            const items = Array.isArray(response) ? response : Array.isArray(response?.results) ? response.results : [];
            return items.map(selectGroupGalleryImage);
        }
        case 'avatarGallery':
            return (await avatarRequest.getAvatarGallery(args.avatarId)).json.map(selectGalleryFile);
        case 'worldInstance': {
            const selected = selectPublicWorldInstance((await instanceRequest.getInstance(args)).json);
            if (!selected) throw Object.assign(new Error(), { code: 'not_public' });
            return selected;
        }
        default:
            return null;
    }
}

/**
 * Executes only named, read-only VRCX entity resources through the original
 * request modules. URL, headers, cookies and raw upstream objects are never
 * accepted from or returned to the browser-facing control plane.
 */
export async function entityResource(stores, payload) {
    if (!watchState.isLoggedIn || !stores.user.currentUser?.id) {
        return failure('requires_authentication');
    }
    if (!RESOURCE_NAMES.has(payload?.name) || !asObject(payload?.arguments)) {
        return failure('invalid_entity_resource');
    }
    try {
        const result = await getResource(stores, payload);
        return result === null ? failure('invalid_entity_resource') : success(result);
    } catch (error) {
        return failure(error?.code || errorCode(error));
    }
}

export const entityResourceNames = Object.freeze(Array.from(RESOURCE_NAMES));

export const entityResourceSelectors = Object.freeze({
    publicProfile: selectPublicProfile,
    representedGroup: selectRepresentedGroup,
    limitedGroup: selectLimitedGroup,
    world: selectWorld,
    avatar: selectAvatar,
    favoriteGroup: selectFavoriteGroup,
    groupPost: selectGroupPost,
    groupMember: selectGroupMember,
    groupGalleryImage: selectGroupGalleryImage,
    galleryFile: selectGalleryFile,
    publicWorldInstance: selectPublicWorldInstance
});
