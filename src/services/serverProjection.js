import {
    selectWebAvatarFields,
    selectWebGroupFields,
    selectWebUserFields,
    selectWebWorldFields
} from '../web/entities';

const SENSITIVE_KEY_EXACT = new Set([
    'auth',
    'authorization',
    'authentication',
    'headers',
    'loginparams',
    'proxy',
    'proxyserver'
]);

function isSensitiveProjectionKey(key) {
    const normalized = String(key)
        .toLowerCase()
        .replaceAll(/[^a-z0-9]/g, '');
    if (SENSITIVE_KEY_EXACT.has(normalized)) {
        return true;
    }
    return (
        normalized.includes('friendkey') ||
        normalized.includes('password') ||
        normalized.includes('passwd') ||
        normalized.includes('cookie') ||
        normalized.includes('credential') ||
        normalized.includes('secret') ||
        normalized.includes('apikey') ||
        normalized.includes('privatekey') ||
        normalized.includes('sessionkey') ||
        normalized === 'token' ||
        normalized.endsWith('token')
    );
}

/**
 * Copies a VRCX domain object for the browser read model. Rich profile and
 * location fields are retained; only credential-bearing keys and values which
 * JSON cannot represent are removed.
 *
 * @param {any} value
 * @param {WeakSet<object>} [ancestors]
 * @returns {any}
 */
export function sanitizeProjectionValue(value, ancestors = new WeakSet()) {
    if (value === null || typeof value === 'string' || typeof value === 'number' || typeof value === 'boolean') {
        return value;
    }
    if (typeof value === 'bigint') {
        return value.toString();
    }
    if (typeof value === 'undefined' || typeof value === 'function' || typeof value === 'symbol') {
        return undefined;
    }
    if (value instanceof Date) {
        return value.toJSON();
    }
    if (value?.__v_isRef) {
        return sanitizeProjectionValue(value.value, ancestors);
    }
    if (ancestors.has(value)) {
        return undefined;
    }

    ancestors.add(value);
    try {
        if (Array.isArray(value)) {
            return value.map((item) => sanitizeProjectionValue(item, ancestors)).filter((item) => item !== undefined);
        }
        if (value instanceof Set) {
            return Array.from(value, (item) => sanitizeProjectionValue(item, ancestors)).filter(
                (item) => item !== undefined
            );
        }
        if (value instanceof Map) {
            const result = {};
            for (const [key, item] of value) {
                if (isSensitiveProjectionKey(key)) {
                    continue;
                }
                const sanitized = sanitizeProjectionValue(item, ancestors);
                if (sanitized !== undefined) {
                    result[String(key)] = sanitized;
                }
            }
            return result;
        }

        const result = {};
        for (const [key, item] of Object.entries(value)) {
            if (isSensitiveProjectionKey(key)) {
                continue;
            }
            const sanitized = sanitizeProjectionValue(item, ancestors);
            if (sanitized !== undefined) {
                result[key] = sanitized;
            }
        }
        return result;
    } finally {
        ancestors.delete(value);
    }
}

function asMap(value) {
    if (value instanceof Map) {
        return value;
    }
    if (value?.value instanceof Map) {
        return value.value;
    }
    return new Map();
}

function asArray(value) {
    if (Array.isArray(value)) {
        return value;
    }
    if (Array.isArray(value?.value)) {
        return value.value;
    }
    return [];
}

function asFiniteInteger(value, fallback = 0) {
    const number = Number(value);
    return Number.isFinite(number) ? Math.max(0, Math.floor(number)) : fallback;
}

function asProjectionText(value, maxLength = 512) {
    return typeof value === 'string' ? value.slice(0, maxLength) : '';
}

const favoriteTypes = new Set(['friend', 'world', 'vrcPlusWorld', 'avatar']);

function buildFavoriteProjection(stores, usersById, entitiesByKey) {
    const favoriteStore = stores.favorite ?? {};
    const sortOrder = asArray(favoriteStore.favoritesSortOrder)
        .filter((id) => typeof id === 'string' && id)
        .slice(0, 5_000);
    const sortIndexes = new Map(sortOrder.map((id, index) => [id, index]));
    const favorites = [];

    for (const favorite of asMap(favoriteStore.cachedFavorites).values()) {
        const id = asProjectionText(favorite?.id, 128);
        const type = asProjectionText(favorite?.type, 32);
        const favoriteId = asProjectionText(favorite?.favoriteId, 128);
        if (!id || !favoriteId || !favoriteTypes.has(type)) {
            continue;
        }
        const tags = asArray(favorite?.tags)
            .filter((tag) => typeof tag === 'string')
            .slice(0, 32)
            .map((tag) => tag.slice(0, 256));
        const derivedGroupKey = `${type}:${String(tags[0] ?? '')}`;
        const candidateGroupKey = asProjectionText(favorite?.$groupKey, 320);
        const groupKey = candidateGroupKey.startsWith(`${type}:`) ? candidateGroupKey : derivedGroupKey;
        const target =
            type === 'friend'
                ? usersById.get(favoriteId)?.payload
                : entitiesByKey.get(`${type === 'vrcPlusWorld' ? 'world' : type}:${favoriteId}`)?.payload;
        const targetPayload = target ?? { id: favoriteId };
        const targetName = asProjectionText(targetPayload.displayName ?? targetPayload.name ?? favoriteId, 512);
        const order = sortIndexes.get(favoriteId);
        favorites.push({
            id,
            type,
            favoriteId,
            tags,
            groupKey,
            sortOrder: order ?? sortOrder.length + favorites.length,
            targetName,
            targetPayload
        });
    }
    favorites.sort((a, b) => a.sortOrder - b.sortOrder || a.id.localeCompare(b.id));

    const groups = [];
    const groupLists = [
        asArray(favoriteStore.favoriteFriendGroups),
        asArray(favoriteStore.favoriteWorldGroups),
        asArray(favoriteStore.favoriteAvatarGroups)
    ];
    let groupOrder = 0;
    for (const list of groupLists) {
        for (const group of list) {
            const type = asProjectionText(group?.type, 32);
            const name = asProjectionText(group?.name, 256);
            if (!name || !favoriteTypes.has(type)) {
                continue;
            }
            const derivedKey = `${type}:${name}`;
            const candidateKey = asProjectionText(group?.key, 320);
            groups.push({
                key: candidateKey.startsWith(`${type}:`) ? candidateKey : derivedKey,
                type,
                name,
                displayName: asProjectionText(group?.displayName, 512) || name,
                capacity: asFiniteInteger(group?.capacity),
                count: asFiniteInteger(group?.count),
                visibility: asProjectionText(group?.visibility, 64),
                sortOrder: groupOrder++
            });
        }
    }

    return { favorites, groups };
}

function hasSourceField(source, name) {
    if (Object.hasOwn(source, name)) {
        return true;
    }
    const snakeCase = name.replace(/[A-Z]/g, (letter) => `_${letter.toLowerCase()}`);
    return Object.hasOwn(source, snakeCase);
}

function mergeSelectedProjection(existing, source, selected) {
    const merged = { ...existing };
    for (const [name, value] of Object.entries(selected)) {
        // Entity selectors intentionally return empty arrays/objects for easy UI
        // consumption. Do not let a partial cache record erase richer data that
        // was already selected from another original VRCX store.
        if ((Array.isArray(value) || (value && typeof value === 'object')) && !hasSourceField(source, name)) {
            continue;
        }
        merged[name] = value;
    }
    return merged;
}

function addUser(usersById, value, options = {}) {
    const source = sanitizeProjectionValue(value) ?? {};
    const selected = /** @type {Record<string, any>} */ (selectWebUserFields(source));
    const id = String(options.id ?? source.id ?? source.userId ?? '');
    if (!id) {
        return;
    }
    const existing = usersById.get(id)?.payload ?? {};
    const payload = {
        ...mergeSelectedProjection(existing, source, selected),
        id,
        displayName: options.displayName ?? source.displayName ?? source.name ?? existing.displayName ?? id,
        worldName: selected.worldName ?? source.$location?.worldName ?? existing.worldName ?? '',
        state: options.state ?? selected.state ?? existing.state ?? 'offline',
        isFriend: Boolean(options.isFriend ?? selected.isFriend ?? existing.isFriend)
    };
    usersById.set(id, {
        userId: id,
        displayName: payload.displayName,
        state: payload.state,
        isFriend: payload.isFriend,
        payload
    });
}

function addEntity(entitiesByKey, type, fallbackId, value) {
    const source = sanitizeProjectionValue(value) ?? {};
    const selector = {
        world: selectWebWorldFields,
        avatar: selectWebAvatarFields,
        group: selectWebGroupFields
    }[type];
    if (!selector) {
        return;
    }
    const selected = selector(source);
    const id = String(source.id ?? source.groupId ?? fallbackId ?? '');
    if (!id) {
        return;
    }
    const key = `${type}:${id}`;
    const existing = entitiesByKey.get(key)?.payload ?? {};
    const merged = {
        ...mergeSelectedProjection(existing, source, selected),
        id,
        name: source.name ?? existing.name ?? id,
        displayName: source.displayName ?? existing.displayName
    };
    entitiesByKey.set(key, {
        entityType: type,
        entityId: id,
        displayName: merged.name ?? merged.displayName ?? merged.shortCode ?? id,
        ownerId: merged.authorId ?? merged.author_id ?? merged.ownerId ?? merged.owner_id ?? null,
        description: merged.description ?? merged.bio ?? '',
        payload: merged
    });
}

/**
 * Builds a deterministic snapshot from the original VRCX stores. This function
 * never mutates those stores and does not introduce a second domain model.
 *
 * @param {ReturnType<import('../stores').createGlobalStores>} stores
 * @param {string} updatedAt
 */
export function buildServerProjectionSnapshot(stores, updatedAt) {
    const usersById = new Map();
    const cachedProfiles = asMap(stores.user.cachedProfiles);

    for (const [id, user] of asMap(stores.user.cachedUsers)) {
        addUser(usersById, { ...(sanitizeProjectionValue(cachedProfiles.get(id)) ?? {}), ...user });
    }
    for (const [id, friend] of asMap(stores.friend.friends)) {
        const ref = friend?.ref ?? {};
        addUser(
            usersById,
            { ...(sanitizeProjectionValue(cachedProfiles.get(id)) ?? {}), ...ref },
            {
                id,
                displayName: ref.displayName ?? friend?.name,
                state: friend?.state ?? ref.state,
                isFriend: true
            }
        );
    }

    const currentUser = stores.user.currentUser;
    const currentUserId = String(currentUser?.id ?? '');
    if (currentUserId) {
        addUser(
            usersById,
            { ...(sanitizeProjectionValue(cachedProfiles.get(currentUserId)) ?? {}), ...currentUser },
            { id: currentUserId, isFriend: false }
        );
    }

    const users = Array.from(usersById.values(), (row) => ({ ...row, updatedAt })).sort((a, b) =>
        a.userId.localeCompare(b.userId)
    );

    const entitiesByKey = new Map();
    for (const [id, world] of asMap(stores.world.cachedWorlds)) {
        addEntity(entitiesByKey, 'world', id, world);
    }
    for (const [id, avatar] of asMap(stores.avatar.cachedAvatars)) {
        addEntity(entitiesByKey, 'avatar', id, avatar);
    }
    for (const [id, group] of asMap(stores.group.cachedGroups)) {
        addEntity(entitiesByKey, 'group', id, group);
    }
    for (const [id, membership] of asMap(stores.group.currentUserGroups)) {
        addEntity(entitiesByKey, 'group', id, membership);
    }
    const entities = Array.from(entitiesByKey.values(), (row) => ({ ...row, updatedAt })).sort((a, b) =>
        `${a.entityType}:${a.entityId}`.localeCompare(`${b.entityType}:${b.entityId}`)
    );
    const favoriteProjection = buildFavoriteProjection(stores, usersById, entitiesByKey);

    return {
        currentUserId,
        users,
        entities,
        favorites: favoriteProjection.favorites,
        favoriteGroups: favoriteProjection.groups,
        favoriteFingerprint: JSON.stringify({
            currentUserId,
            favorites: favoriteProjection.favorites,
            groups: favoriteProjection.groups
        }),
        fingerprint: JSON.stringify({
            users: users.map(({ updatedAt: _updatedAt, ...user }) => user),
            entities: entities.map(({ updatedAt: _updatedAt, ...entity }) => entity)
        })
    };
}
