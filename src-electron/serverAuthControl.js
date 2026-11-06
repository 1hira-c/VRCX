const http = require('http');
const { validateEntityResourcePayload } = require('./serverEntityResourceValidation');
const {
    HISTORY_DATABASE_READ_NAMES,
    normalizeHistoryDatabaseReadResult,
    validateHistoryDatabaseReadArguments
} = require('./serverHistoryReadValidation');

const CONTROL_HOST = '127.0.0.1';
const CONTROL_PORT = 18081;
const MAX_BODY_BYTES = 2 * 1024 * 1024;
const MAX_RESPONSE_BYTES = 2 * 1024 * 1024;
const MAX_DATABASE_READ_RESPONSE_BYTES = 64 * 1024 * 1024;
const VALID_STATUSES = new Set(['requires_authentication', 'authenticating', 'two_factor_required', 'authenticated']);
const VALID_TWO_FACTOR_KINDS = new Set(['totp', 'otp', 'emailOtp']);
const ENTITY_ID_PREFIXES = Object.freeze({
    user: 'usr_',
    world: 'wrld_',
    avatar: 'avtr_',
    group: 'grp_'
});
const WORLD_SEARCH_OPTIONS = new Set(['active', 'recent', 'favorites']);
const WORLD_SEARCH_SORTS = new Set([
    'order',
    'popularity',
    'updated',
    'created',
    'publicationDate',
    'shuffle',
    'relevance',
    'labsPublicationDate',
    'heat'
]);
const USER_SEARCH_SORTS = new Set(['relevance', 'last_login']);
const SEARCH_ORDERS = new Set(['ascending', 'descending']);
const WORLD_SEARCH_FIELDS = new Set(['worldOption', 'sort', 'order', 'featured', 'tag', 'user', 'releaseStatus']);
const USER_SEARCH_FIELDS = new Set(['fuzzy', 'sort', 'order', 'customFields']);
const DATABASE_READ_NAMES = new Set([
    'getDateOfInstanceActivity',
    'getInstanceActivity',
    'getHotWorlds',
    'getHotWorldFriendDetail',
    'getMutualGraphSnapshot',
    'getMutualGraphMeta',
    ...HISTORY_DATABASE_READ_NAMES
]);
const SENSITIVE_KEY_EXACT = new Set([
    'auth',
    'authorization',
    'authentication',
    'headers',
    'loginparams',
    'proxy',
    'proxyserver'
]);

const ROUTES = new Map([
    ['GET /control/v1/status', { operation: 'status', fields: [] }],
    ['POST /control/v1/login', { operation: 'login', fields: ['username', 'password'] }],
    ['POST /control/v1/two-factor', { operation: 'two-factor', fields: ['kind', 'code'] }],
    ['POST /control/v1/email/resend', { operation: 'email-resend', fields: [] }],
    ['POST /control/v1/logout', { operation: 'logout', fields: [] }],
    ['POST /control/v1/search', { operation: 'search', fields: ['type', 'query', 'offset', 'limit', 'options'] }],
    ['POST /control/v1/entity', { operation: 'entity', fields: ['type', 'id'] }],
    ['POST /control/v1/entity-resource', { operation: 'entity-resource', fields: ['name', 'arguments'] }],
    ['POST /control/v1/database-read', { operation: 'database-read', fields: ['name', 'arguments'] }]
]);
const KNOWN_PATHS = new Set(Array.from(ROUTES.keys(), (route) => route.slice(route.indexOf(' ') + 1)));

function createErrorResponse(errorCode) {
    return {
        ok: false,
        status: 'requires_authentication',
        currentUserId: null,
        challenge: null,
        errorCode
    };
}

function isPlainObject(value) {
    return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function hasExactFields(value, fields) {
    if (!isPlainObject(value)) {
        return false;
    }
    const keys = Object.keys(value).sort();
    const expected = [...fields].sort();
    return keys.length === expected.length && keys.every((key, index) => key === expected[index]);
}

function isBoundedString(value, maxLength) {
    return typeof value === 'string' && value.length > 0 && value.length <= maxLength && !value.includes('\0');
}

function isEntityType(value) {
    return typeof value === 'string' && Object.hasOwn(ENTITY_ID_PREFIXES, value);
}

function isEntityId(type, value) {
    return (
        isEntityType(type) &&
        isBoundedString(value, 256) &&
        value.startsWith(ENTITY_ID_PREFIXES[type]) &&
        /^[A-Za-z0-9_-]+$/.test(value)
    );
}

function isSafeWorldTag(tag) {
    return (
        typeof tag === 'string' &&
        tag.length <= 256 &&
        tag
            .split(',')
            .every(
                (part) =>
                    part.length > 0 &&
                    part.length <= 128 &&
                    !Array.from(part).some((character) => /[\u0000-\u001F\u007F]/.test(character))
            )
    );
}

function validateSearchOptions(type, value) {
    if (!isPlainObject(value)) return null;
    const allowedFields = type === 'world' ? WORLD_SEARCH_FIELDS : type === 'user' ? USER_SEARCH_FIELDS : new Set();
    if (Object.keys(value).some((key) => !allowedFields.has(key))) return null;

    const normalized = {};
    const copyEnum = (name, allowed) => {
        if (!Object.hasOwn(value, name)) return true;
        if (typeof value[name] !== 'string' || !allowed.has(value[name])) return false;
        normalized[name] = value[name];
        return true;
    };
    const copyBoolean = (name) => {
        if (!Object.hasOwn(value, name)) return true;
        if (typeof value[name] !== 'boolean') return false;
        normalized[name] = value[name];
        return true;
    };

    if (type === 'world') {
        if (
            !copyEnum('worldOption', WORLD_SEARCH_OPTIONS) ||
            !copyEnum('sort', WORLD_SEARCH_SORTS) ||
            !copyEnum('order', SEARCH_ORDERS) ||
            !copyBoolean('featured') ||
            !copyEnum('user', new Set(['me'])) ||
            !copyEnum('releaseStatus', new Set(['all']))
        ) {
            return null;
        }
        if (Object.hasOwn(value, 'tag')) {
            if (!isSafeWorldTag(value.tag)) return null;
            normalized.tag = value.tag;
        }
    } else if (type === 'user') {
        if (
            !copyBoolean('fuzzy') ||
            !copyEnum('sort', USER_SEARCH_SORTS) ||
            !copyEnum('order', SEARCH_ORDERS) ||
            !copyEnum('customFields', new Set(['displayName']))
        ) {
            return null;
        }
    }
    return normalized;
}

function isBoundedInteger(value, minimum, maximum) {
    return Number.isInteger(value) && value >= minimum && value <= maximum;
}

function isBoundedText(value, maximumLength, { allowEmpty = false } = {}) {
    return (
        typeof value === 'string' &&
        (allowEmpty || value.length > 0) &&
        value.length <= maximumLength &&
        !value.includes('\0')
    );
}

function isIsoDateInput(value) {
    return isBoundedText(value, 128) && Number.isFinite(Date.parse(value));
}

function validateDatabaseReadArguments(name, value) {
    if (!DATABASE_READ_NAMES.has(name) || !isPlainObject(value)) return null;
    if (HISTORY_DATABASE_READ_NAMES.has(name)) {
        return validateHistoryDatabaseReadArguments(name, value);
    }
    if (name === 'getDateOfInstanceActivity' || name === 'getMutualGraphSnapshot' || name === 'getMutualGraphMeta') {
        return hasExactFields(value, []) ? {} : null;
    }
    if (name === 'getInstanceActivity') {
        if (
            !hasExactFields(value, ['startDate', 'endDate']) ||
            !isIsoDateInput(value.startDate) ||
            !isIsoDateInput(value.endDate)
        ) {
            return null;
        }
        const start = Date.parse(value.startDate);
        const end = Date.parse(value.endDate);
        if (start > end || end - start > 32 * 24 * 60 * 60 * 1000) return null;
        return { startDate: value.startDate, endDate: value.endDate };
    }
    if (name === 'getHotWorlds') {
        if (
            !hasExactFields(value, ['days', 'limit']) ||
            !isBoundedInteger(value.days, 1, 365) ||
            !isBoundedInteger(value.limit, 1, 100)
        ) {
            return null;
        }
        return { days: value.days, limit: value.limit };
    }
    if (name === 'getHotWorldFriendDetail') {
        if (
            !hasExactFields(value, ['worldId', 'days']) ||
            !isEntityId('world', value.worldId) ||
            !isBoundedInteger(value.days, 1, 365)
        ) {
            return null;
        }
        return { worldId: value.worldId, days: value.days };
    }
    return null;
}

function containsSensitiveKey(value, seen = new WeakSet()) {
    if (value === null || typeof value !== 'object') return false;
    if (seen.has(value)) return true;
    seen.add(value);
    try {
        const entries = Array.isArray(value) ? value.entries() : Object.entries(value);
        for (const [key, item] of entries) {
            if (!Array.isArray(value)) {
                const normalized = String(key)
                    .toLowerCase()
                    .replaceAll(/[^a-z0-9]/g, '');
                if (
                    SENSITIVE_KEY_EXACT.has(normalized) ||
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
                ) {
                    return true;
                }
            }
            if (containsSensitiveKey(item, seen)) return true;
        }
        return false;
    } finally {
        seen.delete(value);
    }
}

function validatePayload(route, payload) {
    if (!hasExactFields(payload, route.fields)) {
        return null;
    }
    if (route.operation === 'login') {
        if (!isBoundedString(payload.username, 512) || !isBoundedString(payload.password, 4096)) {
            return null;
        }
        return { username: payload.username, password: payload.password };
    }
    if (route.operation === 'two-factor') {
        if (!VALID_TWO_FACTOR_KINDS.has(payload.kind) || !isBoundedString(payload.code, 256)) {
            return null;
        }
        return { kind: payload.kind, code: payload.code };
    }
    if (route.operation === 'search') {
        const options = validateSearchOptions(payload.type, payload.options);
        if (
            !isEntityType(payload.type) ||
            typeof payload.query !== 'string' ||
            payload.query.length > 128 ||
            payload.query.includes('\0') ||
            options === null ||
            (payload.query.trim().length === 0 && !(payload.type === 'world' && Object.keys(options).length > 0)) ||
            !Number.isInteger(payload.offset) ||
            payload.offset < 0 ||
            payload.offset > 10_000 ||
            !Number.isInteger(payload.limit) ||
            payload.limit < 1 ||
            payload.limit > 200
        ) {
            return null;
        }
        return {
            type: payload.type,
            query: payload.query.trim(),
            offset: payload.offset,
            limit: payload.limit,
            options
        };
    }
    if (route.operation === 'entity') {
        if (!isEntityId(payload.type, payload.id)) {
            return null;
        }
        return { type: payload.type, id: payload.id };
    }
    if (route.operation === 'entity-resource') {
        return validateEntityResourcePayload(payload);
    }
    if (route.operation === 'database-read') {
        const argumentsValue = validateDatabaseReadArguments(payload.name, payload.arguments);
        if (!argumentsValue) return null;
        return { name: payload.name, arguments: argumentsValue };
    }
    return {};
}

function isErrorCode(value) {
    return value === null || (typeof value === 'string' && /^[a-z0-9_]{1,64}$/.test(value));
}

function normalizeRendererResponse(value) {
    if (!hasExactFields(value, ['ok', 'status', 'currentUserId', 'challenge', 'errorCode'])) {
        throw new Error('Invalid renderer auth-control response');
    }
    if (typeof value.ok !== 'boolean' || !VALID_STATUSES.has(value.status)) {
        throw new Error('Invalid renderer auth-control response');
    }
    if (
        value.currentUserId !== null &&
        (!isBoundedString(value.currentUserId, 256) || !value.currentUserId.startsWith('usr_'))
    ) {
        throw new Error('Invalid renderer auth-control response');
    }
    if (value.errorCode !== null && !isErrorCode(value.errorCode)) {
        throw new Error('Invalid renderer auth-control response');
    }

    let challenge = null;
    if (value.challenge !== null) {
        if (
            !hasExactFields(value.challenge, ['kinds']) ||
            !Array.isArray(value.challenge.kinds) ||
            value.challenge.kinds.length === 0 ||
            value.challenge.kinds.some((kind) => !VALID_TWO_FACTOR_KINDS.has(kind))
        ) {
            throw new Error('Invalid renderer auth-control response');
        }
        challenge = { kinds: [...new Set(value.challenge.kinds)] };
    }

    if (value.status === 'authenticated' && (!value.currentUserId || challenge)) {
        throw new Error('Invalid renderer auth-control response');
    }
    if (value.status === 'two_factor_required' && (!challenge || value.currentUserId)) {
        throw new Error('Invalid renderer auth-control response');
    }
    if (
        value.status !== 'authenticated' &&
        value.status !== 'two_factor_required' &&
        (value.currentUserId || challenge)
    ) {
        throw new Error('Invalid renderer auth-control response');
    }

    return {
        ok: value.ok,
        status: value.status,
        currentUserId: value.currentUserId,
        challenge,
        errorCode: value.errorCode
    };
}

function normalizeEntityDocument(value, expectedType = null) {
    if (!hasExactFields(value, ['type', 'id', 'name', 'ownerId', 'description', 'updatedAt', 'payload'])) {
        throw new Error('Invalid renderer entity response');
    }
    if (!isEntityType(value.type) || (expectedType !== null && value.type !== expectedType)) {
        throw new Error('Invalid renderer entity response');
    }
    if (
        !isEntityId(value.type, value.id) ||
        !isBoundedString(value.name, 512) ||
        (value.ownerId !== null && !isBoundedString(value.ownerId, 256)) ||
        (value.description !== null && (typeof value.description !== 'string' || value.description.length > 16_384)) ||
        (value.updatedAt !== null && !isBoundedString(value.updatedAt, 128)) ||
        !isPlainObject(value.payload) ||
        containsSensitiveKey(value.payload)
    ) {
        throw new Error('Invalid renderer entity response');
    }
    return {
        type: value.type,
        id: value.id,
        name: value.name,
        ownerId: value.ownerId,
        description: value.description,
        updatedAt: value.updatedAt,
        payload: value.payload
    };
}

function normalizeSearchResponse(value, expectedType) {
    if (!hasExactFields(value, ['ok', 'items', 'total', 'offset', 'limit', 'errorCode'])) {
        throw new Error('Invalid renderer search response');
    }
    if (
        typeof value.ok !== 'boolean' ||
        !isErrorCode(value.errorCode) ||
        (value.ok ? value.errorCode !== null : value.errorCode === null) ||
        !Array.isArray(value.items) ||
        !Number.isSafeInteger(value.total) ||
        value.total < 0 ||
        !Number.isInteger(value.offset) ||
        value.offset < 0 ||
        value.offset > 10_000 ||
        !Number.isInteger(value.limit) ||
        value.limit < 1 ||
        value.limit > 200 ||
        value.items.length > value.limit
    ) {
        throw new Error('Invalid renderer search response');
    }
    return {
        ok: value.ok,
        items: value.items.map((item) => normalizeEntityDocument(item, expectedType)),
        total: value.total,
        offset: value.offset,
        limit: value.limit,
        errorCode: value.errorCode
    };
}

function normalizeEntityResponse(value, expectedType) {
    if (!hasExactFields(value, ['ok', 'item', 'errorCode'])) {
        throw new Error('Invalid renderer entity response');
    }
    if (
        typeof value.ok !== 'boolean' ||
        !isErrorCode(value.errorCode) ||
        (value.ok ? value.errorCode !== null || value.item === null : value.errorCode === null || value.item !== null)
    ) {
        throw new Error('Invalid renderer entity response');
    }
    return {
        ok: value.ok,
        item: value.item === null ? null : normalizeEntityDocument(value.item, expectedType),
        errorCode: value.errorCode
    };
}

function normalizeEntityResourceResponse(value) {
    if (!hasExactFields(value, ['ok', 'result', 'errorCode'])) {
        throw new Error('Invalid renderer entity-resource response');
    }
    if (
        typeof value.ok !== 'boolean' ||
        !isErrorCode(value.errorCode) ||
        (value.ok
            ? value.errorCode !== null || value.result === null || value.result === undefined
            : value.errorCode === null || value.result !== null) ||
        containsSensitiveKey(value.result)
    ) {
        throw new Error('Invalid renderer entity-resource response');
    }
    return {
        ok: value.ok,
        result: value.result,
        errorCode: value.errorCode
    };
}

function normalizeActivityRow(value) {
    if (!hasExactFields(value, ['id', 'created_at', 'type', 'display_name', 'location', 'user_id', 'time'])) {
        throw new Error('Invalid renderer database-read response');
    }
    if (
        !Number.isSafeInteger(value.id) ||
        value.id < 0 ||
        !isBoundedText(value.created_at, 128) ||
        !isBoundedText(value.type, 64) ||
        !isBoundedText(value.display_name, 512, { allowEmpty: true }) ||
        !isBoundedText(value.location, 2048) ||
        !isBoundedText(value.user_id, 256, { allowEmpty: true }) ||
        !Number.isFinite(value.time) ||
        Math.abs(value.time) > 366 * 24 * 60 * 60 * 1000
    ) {
        throw new Error('Invalid renderer database-read response');
    }
    return {
        id: value.id,
        created_at: value.created_at,
        type: value.type,
        display_name: value.display_name,
        location: value.location,
        user_id: value.user_id,
        time: value.time
    };
}

function normalizeActivityResult(value) {
    if (!hasExactFields(value, ['currentUserData', 'detailData'])) {
        throw new Error('Invalid renderer database-read response');
    }
    if (
        !Array.isArray(value.currentUserData) ||
        value.currentUserData.length > 100_000 ||
        !Array.isArray(value.detailData) ||
        value.detailData.length > 20_000
    ) {
        throw new Error('Invalid renderer database-read response');
    }
    let rowCount = value.currentUserData.length;
    const locations = new Set();
    const detailData = value.detailData.map((entry) => {
        if (
            !hasExactFields(entry, ['location', 'rows']) ||
            !isBoundedText(entry.location, 2048) ||
            locations.has(entry.location) ||
            !Array.isArray(entry.rows)
        ) {
            throw new Error('Invalid renderer database-read response');
        }
        locations.add(entry.location);
        rowCount += entry.rows.length;
        if (rowCount > 200_000) throw new Error('Invalid renderer database-read response');
        return { location: entry.location, rows: entry.rows.map(normalizeActivityRow) };
    });
    return {
        currentUserData: value.currentUserData.map(normalizeActivityRow),
        detailData
    };
}

function normalizeHotWorld(value) {
    if (!hasExactFields(value, ['worldId', 'worldName', 'visitCount', 'uniqueFriends', 'lastVisited', 'trend'])) {
        throw new Error('Invalid renderer database-read response');
    }
    if (
        !isEntityId('world', value.worldId) ||
        !isBoundedText(value.worldName, 512) ||
        !isBoundedInteger(value.visitCount, 0, Number.MAX_SAFE_INTEGER) ||
        !isBoundedInteger(value.uniqueFriends, 0, Number.MAX_SAFE_INTEGER) ||
        !isBoundedText(value.lastVisited, 128) ||
        !['rising', 'cooling', 'stable'].includes(value.trend)
    ) {
        throw new Error('Invalid renderer database-read response');
    }
    return {
        worldId: value.worldId,
        worldName: value.worldName,
        visitCount: value.visitCount,
        uniqueFriends: value.uniqueFriends,
        lastVisited: value.lastVisited,
        trend: value.trend
    };
}

function normalizeHotWorldFriend(value) {
    if (!hasExactFields(value, ['userId', 'displayName', 'visitCount', 'lastVisit'])) {
        throw new Error('Invalid renderer database-read response');
    }
    if (
        !isEntityId('user', value.userId) ||
        !isBoundedText(value.displayName, 512, { allowEmpty: true }) ||
        !isBoundedInteger(value.visitCount, 0, Number.MAX_SAFE_INTEGER) ||
        !isBoundedText(value.lastVisit, 128)
    ) {
        throw new Error('Invalid renderer database-read response');
    }
    return {
        userId: value.userId,
        displayName: value.displayName,
        visitCount: value.visitCount,
        lastVisit: value.lastVisit
    };
}

function normalizeMutualSnapshot(value) {
    if (!Array.isArray(value) || value.length > 50_000) {
        throw new Error('Invalid renderer database-read response');
    }
    let edgeCount = 0;
    const friendIds = new Set();
    return value.map((entry) => {
        if (
            !hasExactFields(entry, ['friendId', 'mutualIds']) ||
            !isEntityId('user', entry.friendId) ||
            friendIds.has(entry.friendId) ||
            !Array.isArray(entry.mutualIds)
        ) {
            throw new Error('Invalid renderer database-read response');
        }
        friendIds.add(entry.friendId);
        edgeCount += entry.mutualIds.length;
        if (edgeCount > 250_000 || entry.mutualIds.some((id) => !isEntityId('user', id))) {
            throw new Error('Invalid renderer database-read response');
        }
        return { friendId: entry.friendId, mutualIds: [...entry.mutualIds] };
    });
}

function normalizeMutualMeta(value) {
    if (!Array.isArray(value) || value.length > 50_000) {
        throw new Error('Invalid renderer database-read response');
    }
    const friendIds = new Set();
    return value.map((entry) => {
        if (
            !hasExactFields(entry, ['friendId', 'lastFetchedAt', 'optedOut']) ||
            !isEntityId('user', entry.friendId) ||
            friendIds.has(entry.friendId) ||
            (entry.lastFetchedAt !== null && !isBoundedText(entry.lastFetchedAt, 128)) ||
            typeof entry.optedOut !== 'boolean'
        ) {
            throw new Error('Invalid renderer database-read response');
        }
        friendIds.add(entry.friendId);
        return {
            friendId: entry.friendId,
            lastFetchedAt: entry.lastFetchedAt,
            optedOut: entry.optedOut
        };
    });
}

function normalizeDatabaseReadResult(name, value, requestArguments) {
    if (HISTORY_DATABASE_READ_NAMES.has(name)) {
        return normalizeHistoryDatabaseReadResult(name, value, requestArguments);
    }
    switch (name) {
        case 'getDateOfInstanceActivity':
            if (!Array.isArray(value) || value.length > 100_000 || value.some((date) => !isBoundedText(date, 128))) {
                throw new Error('Invalid renderer database-read response');
            }
            return [...value];
        case 'getInstanceActivity':
            return normalizeActivityResult(value);
        case 'getHotWorlds':
            if (!Array.isArray(value) || value.length > requestArguments.limit) {
                throw new Error('Invalid renderer database-read response');
            }
            return value.map(normalizeHotWorld);
        case 'getHotWorldFriendDetail':
            if (!Array.isArray(value) || value.length > 50_000) {
                throw new Error('Invalid renderer database-read response');
            }
            return value.map(normalizeHotWorldFriend);
        case 'getMutualGraphSnapshot':
            return normalizeMutualSnapshot(value);
        case 'getMutualGraphMeta':
            return normalizeMutualMeta(value);
        default:
            throw new Error('Invalid renderer database-read response');
    }
}

function normalizeDatabaseReadResponse(value, name, requestArguments) {
    if (!hasExactFields(value, ['ok', 'result', 'errorCode'])) {
        throw new Error('Invalid renderer database-read response');
    }
    if (
        typeof value.ok !== 'boolean' ||
        !isErrorCode(value.errorCode) ||
        (value.ok
            ? value.errorCode !== null || value.result === null
            : value.errorCode === null || value.result !== null)
    ) {
        throw new Error('Invalid renderer database-read response');
    }
    return {
        ok: value.ok,
        result: value.ok ? normalizeDatabaseReadResult(name, value.result, requestArguments) : null,
        errorCode: value.errorCode
    };
}

function writeJson(response, statusCode, body, maximumBytes = MAX_RESPONSE_BYTES) {
    const encoded = Buffer.from(JSON.stringify(body));
    if (encoded.length > maximumBytes) {
        throw new Error('Response exceeds control boundary limit');
    }
    response.writeHead(statusCode, {
        'Content-Type': 'application/json; charset=utf-8',
        'Content-Length': encoded.length,
        'Cache-Control': 'no-store',
        'X-Content-Type-Options': 'nosniff'
    });
    response.end(encoded);
}

function readRequestBody(request) {
    return new Promise((resolve, reject) => {
        const declaredLength = Number(request.headers['content-length'] || 0);
        if (!Number.isFinite(declaredLength) || declaredLength < 0 || declaredLength > MAX_BODY_BYTES) {
            reject(Object.assign(new Error('Invalid body'), { code: 'invalid_body' }));
            request.resume();
            return;
        }

        const chunks = [];
        let length = 0;
        request.on('data', (chunk) => {
            length += chunk.length;
            if (length > MAX_BODY_BYTES) {
                reject(Object.assign(new Error('Invalid body'), { code: 'invalid_body' }));
                return;
            }
            chunks.push(chunk);
        });
        request.on('end', () => resolve(Buffer.concat(chunks).toString('utf8')));
        request.on('error', reject);
    });
}

function parseBody(text) {
    if (!text) {
        return {};
    }
    return JSON.parse(text);
}

function createServerAuthControl({
    host = CONTROL_HOST,
    port = CONTROL_PORT,
    isRendererReady,
    getUnavailableErrorCode = () => null,
    dispatch
}) {
    const server = http.createServer(async (request, response) => {
        let pathname;
        try {
            const url = new URL(request.url, 'http://' + CONTROL_HOST);
            if (url.search || url.hash) {
                writeJson(response, 404, createErrorResponse('not_found'));
                return;
            }
            pathname = url.pathname;
        } catch {
            writeJson(response, 400, createErrorResponse('invalid_request'));
            return;
        }

        const route = ROUTES.get(request.method + ' ' + pathname);
        if (!route) {
            const statusCode = KNOWN_PATHS.has(pathname) ? 405 : 404;
            writeJson(
                response,
                statusCode,
                createErrorResponse(statusCode === 405 ? 'method_not_allowed' : 'not_found')
            );
            return;
        }

        let payload;
        try {
            const text = await readRequestBody(request);
            if (request.method === 'GET' && text.length) {
                throw new Error('GET body is not allowed');
            }
            payload = validatePayload(route, parseBody(text));
            if (!payload) {
                throw new Error('Invalid payload');
            }
        } catch {
            if (!response.writableEnded) {
                writeJson(response, 400, createErrorResponse('invalid_request'));
            }
            return;
        }

        if (!isRendererReady()) {
            const code =
                getUnavailableErrorCode() === 'database_initialization_failed'
                    ? 'database_initialization_failed'
                    : 'renderer_unavailable';
            writeJson(response, 503, createErrorResponse(code));
            return;
        }

        try {
            const rawResponse = await dispatch({ operation: route.operation, payload });
            const rendererResponse =
                route.operation === 'search'
                    ? normalizeSearchResponse(rawResponse, payload.type)
                    : route.operation === 'entity'
                      ? normalizeEntityResponse(rawResponse, payload.type)
                      : route.operation === 'entity-resource'
                        ? normalizeEntityResourceResponse(rawResponse)
                        : route.operation === 'database-read'
                          ? normalizeDatabaseReadResponse(rawResponse, payload.name, payload.arguments)
                          : normalizeRendererResponse(rawResponse);
            const statusCode = rendererResponse.errorCode === 'internal_error' ? 500 : 200;
            writeJson(
                response,
                statusCode,
                rendererResponse,
                route.operation === 'database-read' ? MAX_DATABASE_READ_RESPONSE_BYTES : MAX_RESPONSE_BYTES
            );
        } catch (error) {
            if (error?.code === 'renderer_timeout') {
                writeJson(response, 504, createErrorResponse('renderer_timeout'));
            } else if (error?.code === 'renderer_unavailable') {
                writeJson(response, 503, createErrorResponse('renderer_unavailable'));
            } else {
                writeJson(response, 500, createErrorResponse('internal_error'));
            }
        }
    });

    server.maxHeadersCount = 32;
    server.requestTimeout = 35_000;
    server.headersTimeout = 10_000;

    return {
        listen() {
            return new Promise((resolve, reject) => {
                const onError = (error) => {
                    server.off('listening', onListening);
                    reject(error);
                };
                const onListening = () => {
                    server.off('error', onError);
                    resolve();
                };
                server.once('error', onError);
                server.once('listening', onListening);
                server.listen(port, host);
            });
        },
        address() {
            return server.address();
        },
        close() {
            return new Promise((resolve) => {
                if (!server.listening) {
                    resolve();
                    return;
                }
                server.close(() => resolve());
            });
        }
    };
}

module.exports = {
    CONTROL_HOST,
    CONTROL_PORT,
    createErrorResponse,
    createServerAuthControl,
    normalizeRendererResponse,
    normalizeSearchResponse,
    normalizeEntityResponse,
    normalizeEntityResourceResponse,
    validatePayload
};
