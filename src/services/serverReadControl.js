import { groupRequest, queryRequest, userRequest, worldRequest } from '../api';
import { lookupAvatars } from '../coordinators/avatarCoordinator';
import { database, dbVars } from './database';
import { executeHistoryDatabaseRead, historyDatabaseReadNames } from './serverHistoryReadControl';
import { entityResource } from './serverEntityResourceControl';
import {
    selectWebAvatarFields,
    selectWebGroupFields,
    selectWebUserFields,
    selectWebWorldFields
} from '../web/entities';
import { watchState } from './watchState';
import { runtimeCapabilities } from './runtimeCapabilities';
import { readServerDataOwner } from './serverDataOwner';

const ENTITY_ID_PREFIXES = Object.freeze({
    user: 'usr_',
    world: 'wrld_',
    avatar: 'avtr_',
    group: 'grp_'
});

const ENTITY_SELECTORS = Object.freeze({
    user: selectWebUserFields,
    world: selectWebWorldFields,
    avatar: selectWebAvatarFields,
    group: selectWebGroupFields
});

// These original repository reads interpolate dbVars.userPrefix. Global
// game-log and memo reads intentionally remain available without an owner.
const DATABASE_READS_REQUIRING_DATA_OWNER = new Set([
    'lookupFeedDatabase',
    'searchFeedDatabase',
    'getAllUserStats',
    'getFriendLogHistory',
    'getFriendLogHistoryForUserId',
    'getHotWorlds',
    'getHotWorldFriendDetail',
    'getMutualGraphSnapshot',
    'getMutualGraphMeta'
]);

// These use global game-log tables, but also filter self activity with
// dbVars.userId. Restore an available owner for correct analytics; without
// one, preserve their global-read behavior instead of returning 503.
const DATABASE_READS_WITH_OPTIONAL_DATA_OWNER = new Set(['getDateOfInstanceActivity', 'getInstanceActivity']);

function isTrustedUserId(userId) {
    return (
        typeof userId === 'string' &&
        userId.length > 4 &&
        userId.length <= 128 &&
        userId.startsWith('usr_') &&
        /^[A-Za-z0-9_-]+$/.test(userId)
    );
}

async function prepareDatabaseReadContext(stores, name) {
    const requiresDataOwner = DATABASE_READS_REQUIRING_DATA_OWNER.has(name);
    const usesOptionalDataOwner = DATABASE_READS_WITH_OPTIONAL_DATA_OWNER.has(name);
    if (!runtimeCapabilities.serverMode || (!requiresDataOwner && !usesOptionalDataOwner)) {
        return true;
    }

    // A live authenticated session is authoritative. In normal operation
    // loginComplete already initialized this exact context; the fallback is a
    // defensive repair for an interrupted renderer state, never imported A.
    const restoreAuthenticatedContext = () => {
        if (!watchState.isLoggedIn) {
            return null;
        }
        const activeUserId = stores.user.currentUser?.id;
        if (!isTrustedUserId(activeUserId)) {
            return false;
        }
        if (dbVars.userId !== activeUserId || !dbVars.userPrefix) {
            return database.setReadOnlyDataOwner(activeUserId) === true;
        }
        return true;
    };

    const activeContext = restoreAuthenticatedContext();
    if (activeContext !== null) return activeContext;

    // Do not replace a previously initialized native context. This is what
    // keeps a just-authenticated B session from being reset to imported A.
    if (dbVars.userPrefix) {
        return true;
    }

    const storedUserId = await readServerDataOwner();

    // The metadata read is asynchronous. A native login can complete while it
    // is in flight, so re-check before applying imported context and retain B.
    const completedActiveContext = restoreAuthenticatedContext();
    if (completedActiveContext !== null) return completedActiveContext;
    if (dbVars.userPrefix) {
        return true;
    }
    if (storedUserId !== null) {
        return database.setReadOnlyDataOwner(storedUserId) === true;
    }
    return !requiresDataOwner;
}

function asObject(value) {
    return value && typeof value === 'object' && !Array.isArray(value) ? value : {};
}

function firstString(...values) {
    return values.find((value) => typeof value === 'string' && value.length > 0) || '';
}

function nullableString(value) {
    return typeof value === 'string' && value.length > 0 ? value : null;
}

function isExpectedId(type, id) {
    return (
        typeof id === 'string' &&
        id.startsWith(ENTITY_ID_PREFIXES[type]) &&
        id.length <= 256 &&
        /^[A-Za-z0-9_-]+$/.test(id)
    );
}

/**
 * Convert an original VRCX/VRChat entity to the fixed browser read contract.
 * Selectors are shared with the projection publisher, so unknown response
 * fields (including credentials) never cross the worker boundary.
 */
export function createServerEntityDocument(type, value) {
    const source = asObject(value);
    const selector = ENTITY_SELECTORS[type];
    if (!selector) return null;

    const id = firstString(source.id, source.userId, source.groupId);
    if (!isExpectedId(type, id)) return null;

    const selected = selector(source);
    if (type === 'user') {
        const displayName = firstString(source.displayName, source.name, id);
        const payload = { ...selected, id, displayName };
        return {
            type,
            id,
            name: displayName,
            ownerId: null,
            description: nullableString(selected.bio),
            updatedAt: nullableString(selected.last_activity || selected.last_login),
            payload
        };
    }

    const name = firstString(source.name, source.displayName, selected.shortCode, id);
    const payload = { ...selected, id, name };
    const ownerId = type === 'group' ? selected.ownerId : selected.authorId;
    const updatedAt = type === 'group' ? selected.updatedAt : selected.updated_at;
    return {
        type,
        id,
        name,
        ownerId: nullableString(ownerId),
        description: nullableString(selected.description),
        updatedAt: nullableString(updatedAt),
        payload
    };
}

function errorCode(error) {
    if (['avatar_remote_database_disabled', 'avatar_remote_database_limit_exceeded'].includes(error?.code)) {
        return error.code;
    }
    const status = Number(error?.status);
    if (Number.isInteger(status) && status >= 400 && status <= 599) {
        return status === 404 ? 'not_found' : `http_${status}`;
    }
    if (error?.name === 'AbortError') return 'request_aborted';
    if (error?.name === 'TypeError') return 'network_error';
    return 'upstream_error';
}

function failedSearch(payload, code) {
    return {
        ok: false,
        items: [],
        total: 0,
        offset: payload.offset,
        limit: payload.limit,
        errorCode: code
    };
}

async function searchOriginalVrcx(payload) {
    const options = asObject(payload.options);
    switch (payload.type) {
        case 'user': {
            // Same key order as the original searchUserByDisplayName /
            // useSearchUser params, since they become the query string verbatim.
            const params = {
                n: payload.limit,
                offset: payload.offset
            };
            if (Object.hasOwn(options, 'fuzzy')) params.fuzzy = options.fuzzy;
            params.search = payload.query;
            for (const field of ['customFields', 'sort', 'order']) {
                if (Object.hasOwn(options, field)) params[field] = options[field];
            }
            return (await userRequest.getUsers(params)).json;
        }
        case 'world': {
            // Same key order as the original useSearchWorld params.
            const params = {
                n: payload.limit,
                offset: payload.offset
            };
            for (const field of ['sort', 'featured']) {
                if (Object.hasOwn(options, field)) params[field] = options[field];
            }
            if (payload.query) params.search = payload.query;
            for (const field of ['order', 'user', 'releaseStatus', 'tag']) {
                if (Object.hasOwn(options, field)) params[field] = options[field];
            }
            return (await worldRequest.getWorlds(params, options.worldOption || '')).json;
        }
        case 'group':
            return (
                await groupRequest.groupSearch({
                    n: payload.limit,
                    offset: payload.offset,
                    query: payload.query
                })
            ).json;
        case 'avatar': {
            // VRCX intentionally uses its configured avatar provider for this
            // tab. The browser supplies only the search term; provider URL and
            // VRCX identification remain owned by the original worker.
            const avatars = await lookupAvatars('search', payload.query);
            if (avatars.size > 5_000) {
                throw Object.assign(new Error(), { code: 'avatar_remote_database_limit_exceeded' });
            }
            return Array.from(avatars.values()).slice(payload.offset, payload.offset + payload.limit);
        }
        default:
            return [];
    }
}

async function search(stores, payload) {
    if (!watchState.isLoggedIn || !stores.user.currentUser?.id) {
        return failedSearch(payload, 'requires_authentication');
    }

    try {
        const originalItems = await searchOriginalVrcx(payload);
        const items = originalItems
            .map((item) => createServerEntityDocument(payload.type, item))
            .filter((item) => item !== null)
            .slice(0, payload.limit);
        const hasAnotherPage = originalItems.length >= payload.limit;
        return {
            ok: true,
            items,
            // VRChat search responses do not expose an exact count. This value
            // is only a safe paging hint; the existing UI pages by result size.
            total: payload.offset + items.length + (hasAnotherPage ? 1 : 0),
            offset: payload.offset,
            limit: payload.limit,
            errorCode: null
        };
    } catch (error) {
        return failedSearch(payload, errorCode(error));
    }
}

async function getOriginalVrcxEntity(payload) {
    switch (payload.type) {
        case 'user':
            return (await queryRequest.fetch('user', { userId: payload.id })).json;
        case 'world':
            return (await queryRequest.fetch('world', { worldId: payload.id })).json;
        case 'avatar':
            return (await queryRequest.fetch('avatar.dialog', { avatarId: payload.id })).json;
        case 'group':
            return (await queryRequest.fetch('group', { groupId: payload.id, includeRoles: true })).json;
        default:
            return null;
    }
}

async function entity(stores, payload) {
    if (!watchState.isLoggedIn || !stores.user.currentUser?.id) {
        return { ok: false, item: null, errorCode: 'requires_authentication' };
    }

    try {
        const item = createServerEntityDocument(payload.type, await getOriginalVrcxEntity(payload));
        return item
            ? { ok: true, item, errorCode: null }
            : { ok: false, item: null, errorCode: 'invalid_upstream_response' };
    } catch (error) {
        return { ok: false, item: null, errorCode: errorCode(error) };
    }
}

function databaseReadFailure(code = 'database_read_failed') {
    return { ok: false, result: null, errorCode: code };
}

function databaseReadSuccess(result) {
    return { ok: true, result, errorCode: null };
}

function serializeInstanceActivity(result) {
    const source = asObject(result);
    const detailData = source.detailData instanceof Map ? source.detailData : new Map();
    return {
        currentUserData: Array.isArray(source.currentUserData) ? source.currentUserData : [],
        detailData: Array.from(detailData, ([location, rows]) => ({
            location,
            rows: Array.isArray(rows) ? rows : []
        }))
    };
}

function serializeMutualSnapshot(snapshot) {
    if (!(snapshot instanceof Map)) return [];
    return Array.from(snapshot, ([friendId, mutualIds]) => ({
        friendId,
        mutualIds: Array.isArray(mutualIds) ? mutualIds : Array.from(mutualIds || [])
    }));
}

function serializeMutualMeta(meta) {
    if (!(meta instanceof Map)) return [];
    return Array.from(meta, ([friendId, value]) => ({
        friendId,
        lastFetchedAt: value?.lastFetchedAt ?? null,
        optedOut: value?.optedOut === true
    }));
}

/**
 * Execute one of the explicitly named local SQLite reads used by the original
 * VRCX chart components. This is intentionally a closed switch: callers can
 * neither supply SQL nor select an arbitrary database method.
 */
async function databaseRead(stores, payload) {
    try {
        if (!(await prepareDatabaseReadContext(stores, payload.name))) {
            return databaseReadFailure('data_owner_unavailable');
        }
        if (historyDatabaseReadNames.has(payload.name)) {
            return databaseReadSuccess(await executeHistoryDatabaseRead(stores, database, payload));
        }
        switch (payload.name) {
            case 'getDateOfInstanceActivity':
                return databaseReadSuccess(await database.getDateOfInstanceActivity());
            case 'getInstanceActivity':
                return databaseReadSuccess(
                    serializeInstanceActivity(
                        await database.getInstanceActivity(payload.arguments.startDate, payload.arguments.endDate)
                    )
                );
            case 'getHotWorlds':
                return databaseReadSuccess(
                    await database.getHotWorlds(payload.arguments.days, payload.arguments.limit)
                );
            case 'getHotWorldFriendDetail':
                return databaseReadSuccess(
                    await database.getHotWorldFriendDetail(payload.arguments.worldId, payload.arguments.days)
                );
            case 'getMutualGraphSnapshot':
                return databaseReadSuccess(serializeMutualSnapshot(await database.getMutualGraphSnapshot()));
            case 'getMutualGraphMeta':
                return databaseReadSuccess(serializeMutualMeta(await database.getMutualGraphMeta()));
            default:
                return databaseReadFailure('invalid_database_read');
        }
    } catch {
        return databaseReadFailure();
    }
}

export function dispatchServerReadControlRequest(stores, request) {
    if (request.operation === 'search') return search(stores, request.payload);
    if (request.operation === 'entity') return entity(stores, request.payload);
    if (request.operation === 'entity-resource') return entityResource(stores, request.payload);
    if (request.operation === 'database-read') return databaseRead(stores, request.payload);
    throw new Error('Unsupported server read operation');
}
