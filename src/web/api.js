const API_ROOT = '/api/v1';

/**
 * Browser-only client for the VRCX Server BFF. This deliberately exposes
 * named resources only; it must never become a generic upstream HTTP proxy.
 */
export class WebApiClient {
    /** @param {string} [baseUrl] */
    constructor(baseUrl = API_ROOT) {
        this.baseUrl = baseUrl.replace(/\/$/, '');
    }

    /**
     * @param {string} path
     * @param {RequestInit} [options]
     */
    async request(path, options = {}) {
        const headers = new Headers(options.headers || {});
        if (options.body && !(options.body instanceof FormData) && !headers.has('Content-Type')) {
            headers.set('Content-Type', 'application/json');
        }

        const response = await fetch(this.baseUrl + path, {
            credentials: 'include',
            headers,
            ...options
        });

        const contentType = response.headers.get('content-type') || '';
        const body = contentType.includes('application/json') ? await response.json() : await response.text();
        if (!response.ok) {
            const message = typeof body === 'object' ? body.message || body.title || response.statusText : body;
            const error = new Error(message || 'Request failed (' + response.status + ')');
            // @ts-ignore - callers need the status to switch to sign-in safely.
            error.status = response.status;
            // @ts-ignore
            error.body = body;
            // Keep only the standard retry delay, not response headers or cookies.
            // @ts-ignore
            error.retryAfterMs = retryAfterMilliseconds(response.headers.get('retry-after'));
            throw error;
        }
        return body;
    }

    get(path) {
        return this.request(path);
    }

    post(path, body) {
        return this.request(path, {
            method: 'POST',
            body: body instanceof FormData ? body : JSON.stringify(body || {})
        });
    }

    getAuthStatus() {
        return this.get('/auth/status');
    }

    bootstrap(password, setupToken, deviceName) {
        return this.post('/auth/bootstrap', { password, setupToken, deviceName: deviceName || undefined });
    }

    login(password, deviceName) {
        return this.post('/auth/login', { password, deviceName: deviceName || undefined });
    }

    recoverAdmin(code, password, deviceName) {
        return this.post('/auth/recovery/redeem', { code, password, deviceName: deviceName || undefined });
    }

    logout() {
        return this.post('/auth/logout');
    }

    createPairing() {
        return this.post('/auth/pairing');
    }

    redeemPairing(code, deviceName) {
        return this.post('/auth/pairing/redeem', { code, deviceName });
    }

    getDevices() {
        return this.get('/auth/devices');
    }

    revokeDevice(sessionId) {
        return this.request('/auth/devices/' + encodeURIComponent(sessionId), { method: 'DELETE' });
    }

    importSnapshot(database, wal, shm) {
        if (!(database instanceof Blob)) throw new Error('A VRCX SQLite snapshot is required.');
        const form = new FormData();
        form.append('database', database, 'VRCX.sqlite3');
        if (wal instanceof Blob) form.append('wal', wal, 'VRCX.sqlite3-wal');
        if (shm instanceof Blob) form.append('shm', shm, 'VRCX.sqlite3-shm');
        return this.post('/setup/import', form);
    }

    getVrchatAuthStatus() {
        return this.get('/vrchat/auth/status');
    }

    loginVrchat(username, password) {
        return this.post('/vrchat/auth/login', { username, password });
    }

    submitVrchatTwoFactor(kind, code) {
        return this.post('/vrchat/auth/two-factor', { kind, code });
    }

    resendVrchatEmailOtp() {
        return this.post('/vrchat/auth/email-otp/resend');
    }

    logoutVrchat() {
        return this.post('/vrchat/auth/logout');
    }

    getStatus() {
        return this.get('/status');
    }

    getFriends(page) {
        return this.get(pagePath('/friends', page));
    }

    getFriend(id) {
        return this.get('/friends/' + encodeURIComponent(id));
    }

    getFriendHistory(id, page) {
        return this.get(pagePath('/friends/' + encodeURIComponent(id) + '/history', page));
    }

    getRecentFriendHistory(page) {
        return this.get(pagePath('/history/friends', page));
    }

    getMemo(entityType, id) {
        return this.get('/memos/' + encodeURIComponent(entityType) + '/' + encodeURIComponent(id));
    }

    getFeed(page) {
        return this.get(pagePath('/feed', page));
    }

    getGameLog(page) {
        return this.get(pagePath('/history/game-log', page));
    }

    getNativeFeed({ search = '', filters = [], favoritesOnly = false, maxEntries, dateFrom = '', dateTo = '' }) {
        const params = historyTableParams(filters, favoritesOnly, maxEntries, FEED_HISTORY_FILTERS);
        appendOptionalHistoryText(params, 'search', search, 512);
        appendOptionalHistoryDate(params, 'dateFrom', dateFrom);
        appendOptionalHistoryDate(params, 'dateTo', dateTo);
        return this.get('/history-native/feed?' + params.toString());
    }

    getNativeGameLog({ search = '', filters = [], favoritesOnly = false, maxEntries }) {
        const params = historyTableParams(filters, favoritesOnly, maxEntries, GAME_LOG_HISTORY_FILTERS);
        appendOptionalHistoryText(params, 'search', search, 512);
        return this.get('/history-native/game-log?' + params.toString());
    }

    getNativeSessionLocations(beforeId, limit) {
        const params = new URLSearchParams({ limit: String(historyInteger(limit, 1, 10_000)) });
        if (beforeId !== null && beforeId !== undefined) {
            params.set('beforeId', String(historyInteger(beforeId, 1, Number.MAX_SAFE_INTEGER)));
        }
        return this.get('/history-native/game-log/sessions/locations?' + params.toString());
    }

    getNativeSessionLocationsByAnchor(sinceDate, limit) {
        const params = new URLSearchParams({
            sinceDate: historyDate(sinceDate),
            limit: String(historyInteger(limit, 1, 10_000))
        });
        return this.get('/history-native/game-log/sessions/anchor?' + params.toString());
    }

    getNativeSessionEvents(locationTags, afterDate, beforeDate) {
        if (!Array.isArray(locationTags) || locationTags.length < 1 || locationTags.length > 1_000) {
            throw new Error('Invalid session locations.');
        }
        const params = new URLSearchParams({
            afterDate: historyDate(afterDate),
            beforeDate: historyDate(beforeDate)
        });
        for (const location of [...new Set(locationTags)]) {
            params.append('location', historyText(location, 2_048));
        }
        return this.get('/history-native/game-log/sessions/events?' + params.toString());
    }

    getNativeUserMemo(userId) {
        return this.get('/history-native/memos/user/' + encodeURIComponent(historyEntityId(userId, 'usr_')));
    }

    getNativeUserMemos() {
        return this.get('/history-native/memos/users');
    }

    getNativeWorldMemo(worldId) {
        return this.get('/history-native/memos/world/' + encodeURIComponent(historyEntityId(worldId, 'wrld_')));
    }

    getNativeAvatarMemo(avatarId) {
        return this.get('/history-native/memos/avatar/' + encodeURIComponent(historyEntityId(avatarId, 'avtr_')));
    }

    getNativeUserStats(userId, displayName, inCurrentWorld) {
        const params = new URLSearchParams({
            displayName: historyText(displayName ?? '', 512, true),
            inCurrentWorld: String(Boolean(inCurrentWorld))
        });
        return this.get(
            '/history-native/users/' +
                encodeURIComponent(historyEntityId(userId, 'usr_')) +
                '/stats?' +
                params.toString()
        );
    }

    getNativeAllUserStats() {
        return this.get('/history-native/users/stats');
    }

    getNativeFriendHistory() {
        return this.get('/history-native/friends');
    }

    getNativeFriendHistoryForUser(userId, types = []) {
        if (!Array.isArray(types) || types.length > FRIEND_HISTORY_TYPES.size) {
            throw new Error('Invalid friend history types.');
        }
        const params = new URLSearchParams();
        for (const type of [...new Set(types)]) {
            if (!FRIEND_HISTORY_TYPES.has(type)) throw new Error('Invalid friend history type.');
            params.append('type', type);
        }
        const suffix = params.size ? '?' + params.toString() : '';
        return this.get('/history-native/friends/' + encodeURIComponent(historyEntityId(userId, 'usr_')) + suffix);
    }

    getNotifications(page) {
        return this.get(pagePath('/notifications', page));
    }

    getFavorites(page) {
        return this.get(pagePath('/favorites', page));
    }

    getFavoriteGroups() {
        return this.get('/favorites/groups');
    }

    getUserPublicProfile(userId) {
        return this.get(userResourcePath(userId, '/public-profile'));
    }

    getUserRepresentedGroup(userId) {
        return this.get(userResourcePath(userId, '/represented-group'));
    }

    getUserGroups(userId) {
        return this.get(userResourcePath(userId, '/groups'));
    }

    getUserWorlds(userId, options) {
        const params = entityPageParams(options);
        appendEntityEnum(params, 'sort', options?.sort, USER_WORLD_SORTS);
        appendEntityEnum(params, 'order', options?.order, SEARCH_ORDERS);
        appendEntityEnum(params, 'releaseStatus', options?.releaseStatus, new Set(['public', 'all']));
        appendEntityEnum(params, 'user', options?.user ?? '', new Set(['', 'me']), true);
        return this.get(userResourcePath(userId, '/worlds') + '?' + params.toString());
    }

    getUserFavoriteGroups(userId, page) {
        return this.get(userResourcePath(userId, '/favorite-groups') + entityPageSuffix(page));
    }

    getUserFavoriteWorlds(userId, tag, page) {
        const params = entityPageParams(page);
        params.set('tag', entityText(tag, 256));
        return this.get(userResourcePath(userId, '/favorite-worlds') + '?' + params.toString());
    }

    getUserOwnedAvatars(userId, options) {
        const params = entityPageParams(options);
        appendEntityEnum(params, 'sort', options?.sort, new Set(['updated']));
        appendEntityEnum(params, 'order', options?.order, SEARCH_ORDERS);
        appendEntityEnum(params, 'releaseStatus', options?.releaseStatus, new Set(['public', 'private', 'all']));
        return this.get(userResourcePath(userId, '/avatars/owned') + '?' + params.toString());
    }

    getUserLicensedAvatars(userId, page) {
        return this.get(userResourcePath(userId, '/avatars/licensed') + entityPageSuffix(page));
    }

    getUserRemoteAvatars(userId) {
        return this.get(userResourcePath(userId, '/avatars/remote'));
    }

    getGroupPosts(groupId, page) {
        return this.get(groupResourcePath(groupId, '/posts') + entityPageSuffix(page));
    }

    getGroupMember(groupId, userId) {
        return this.get(groupResourcePath(groupId, '/members/') + encodeURIComponent(entityId(userId, 'usr_')));
    }

    getGroupMembers(groupId, options) {
        const params = entityPageParams(options);
        appendEntityEnum(params, 'sort', options?.sort, new Set(['joinedAt:asc', 'joinedAt:desc']));
        const roleId = options?.roleId ?? '';
        if (roleId !== '') params.set('roleId', entityId(roleId, 'grol_'));
        return this.get(groupResourcePath(groupId, '/members') + '?' + params.toString());
    }

    searchGroupMembers(groupId, query, page) {
        const params = entityPageParams(page);
        const text = entityText(query, 128).trim();
        if (text.length < 3) throw new Error('Invalid group member search query.');
        params.set('q', text);
        return this.get(groupResourcePath(groupId, '/members/search') + '?' + params.toString());
    }

    getGroupGallery(groupId, galleryId, page) {
        return this.get(
            groupResourcePath(groupId, '/galleries/') +
                encodeURIComponent(entityId(galleryId, 'ggal_')) +
                entityPageSuffix(page)
        );
    }

    getAvatarGallery(avatarId) {
        return this.get('/avatars/' + encodeURIComponent(entityId(avatarId, 'avtr_')) + '/gallery');
    }

    getWorldInstance(worldId, instanceId) {
        const value = publicInstanceId(instanceId);
        return this.get(
            '/worlds/' + encodeURIComponent(entityId(worldId, 'wrld_')) + '/instances/' + encodeURIComponent(value)
        );
    }

    getAnalytics() {
        return this.get('/analytics/summary');
    }

    getInstanceActivityDates() {
        return this.get('/analytics/instance-activity/dates');
    }

    getInstanceActivity(startDate, endDate) {
        const start = analyticsDate(startDate);
        const end = analyticsDate(endDate);
        if (Date.parse(start) > Date.parse(end) || Date.parse(end) - Date.parse(start) > 32 * 24 * 60 * 60 * 1000) {
            throw new Error('Invalid instance activity date range.');
        }
        const params = new URLSearchParams({ startDate: start, endDate: end });
        return this.get('/analytics/instance-activity?' + params.toString());
    }

    getHotWorlds(days, limit = 30) {
        const params = new URLSearchParams({
            days: String(analyticsInteger(days, 1, 365)),
            limit: String(analyticsInteger(limit, 1, 100))
        });
        return this.get('/analytics/hot-worlds?' + params.toString());
    }

    getHotWorldFriendDetail(worldId, days) {
        if (typeof worldId !== 'string' || !/^wrld_[A-Za-z0-9_-]+$/.test(worldId) || worldId.length > 256) {
            throw new Error('Invalid world id.');
        }
        const params = new URLSearchParams({ days: String(analyticsInteger(days, 1, 365)) });
        return this.get('/analytics/hot-worlds/' + encodeURIComponent(worldId) + '/friends?' + params.toString());
    }

    getMutualGraphSnapshot() {
        return this.get('/analytics/mutual-graph/snapshot');
    }

    getMutualGraphMeta() {
        return this.get('/analytics/mutual-graph/meta');
    }

    // Search remains constrained to the fixed server entity types.
    search(type, query, page, options) {
        if (!Object.hasOwn(SEARCH_DETAIL_PATHS, type)) throw new Error('Unsupported search type.');
        const params = new URLSearchParams({ type, q: String(query ?? '') });
        appendPageParams(params, page, type === 'avatar' ? 5_000 : 200);
        appendSearchOptions(params, type, options);
        return this.get('/search?' + params.toString());
    }

    getSearchDetail(type, id) {
        if (!Object.hasOwn(SEARCH_DETAIL_PATHS, type)) throw new Error('Unsupported search type.');
        return this.get('/' + SEARCH_DETAIL_PATHS[type] + '/' + encodeURIComponent(id));
    }
}

function retryAfterMilliseconds(value) {
    if (typeof value !== 'string' || !value.trim()) return null;
    const delay = /^\d+$/.test(value.trim()) ? Number(value) * 1000 : Date.parse(value) - Date.now();
    return Number.isFinite(delay) ? Math.max(0, Math.min(delay, 2_147_483_647)) : null;
}

// Adds only numeric paging parameters to a fixed API path.
function pagePath(path, page) {
    const params = new URLSearchParams();
    appendPageParams(params, page);
    const query = params.toString();
    return query ? path + '?' + query : path;
}

// Clamp paging inputs before they leave the browser.
function appendPageParams(params, page, maximumLimit = 200) {
    if (!page) return;
    if (Number.isFinite(page.offset)) params.set('offset', String(Math.max(0, Math.floor(page.offset))));
    if (Number.isFinite(page.limit)) {
        params.set('limit', String(Math.min(maximumLimit, Math.max(1, Math.floor(page.limit)))));
    }
}

const USER_WORLD_SORTS = new Set(['name', 'updated', 'created', 'favorites', 'popularity']);

function entityPageParams(page) {
    const offset = page?.offset ?? 0;
    const limit = page?.limit ?? page?.n ?? 50;
    if (
        !Number.isInteger(offset) ||
        offset < 0 ||
        offset > 10_000 ||
        !Number.isInteger(limit) ||
        limit < 1 ||
        limit > 100
    ) {
        throw new Error('Invalid entity resource page.');
    }
    return new URLSearchParams({ offset: String(offset), limit: String(limit) });
}

function entityPageSuffix(page) {
    return '?' + entityPageParams(page).toString();
}

function appendEntityEnum(params, name, value, allowed, allowEmpty = false) {
    if (value === undefined || value === null || value === '') {
        if (allowEmpty) params.set(name, '');
        else throw new Error('Invalid entity resource option.');
        return;
    }
    if (typeof value !== 'string' || !allowed.has(value)) throw new Error('Invalid entity resource option.');
    params.set(name, value);
}

function entityText(value, maximumLength) {
    if (
        typeof value !== 'string' ||
        value.length === 0 ||
        value.length > maximumLength ||
        Array.from(value).some((character) => /[\u0000-\u001F\u007F]/.test(character))
    ) {
        throw new Error('Invalid entity resource text.');
    }
    return value;
}

function entityId(value, prefix) {
    const text = entityText(value, 256);
    if (!text.startsWith(prefix) || !/^[A-Za-z0-9_-]+$/.test(text)) {
        throw new Error('Invalid entity resource id.');
    }
    return text;
}

function userResourcePath(userId, suffix) {
    return '/users/' + encodeURIComponent(entityId(userId, 'usr_')) + suffix;
}

function groupResourcePath(groupId, suffix) {
    return '/groups/' + encodeURIComponent(entityId(groupId, 'grp_')) + suffix;
}

function publicInstanceId(value) {
    const text = entityText(value, 2048);
    if (
        text.includes('://') ||
        /~(?:private|friends|hidden)\(/i.test(text) ||
        /~nonce\(/i.test(text) ||
        !/^[A-Za-z0-9_~().,:+-]+$/.test(text)
    ) {
        throw new Error('Invalid public instance id.');
    }
    return text;
}

const WORLD_SEARCH_FIELDS = new Set(['worldOption', 'sort', 'order', 'featured', 'tag', 'user', 'releaseStatus']);
const USER_SEARCH_FIELDS = new Set(['fuzzy', 'sort', 'order', 'customFields']);
const WORLD_OPTIONS = new Set(['active', 'recent', 'favorites']);
const WORLD_SORTS = new Set([
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
const USER_SORTS = new Set(['relevance', 'last_login']);
const SEARCH_ORDERS = new Set(['ascending', 'descending']);

function appendSearchOptions(params, type, options) {
    if (options === undefined || options === null) return;
    if (typeof options !== 'object' || Array.isArray(options)) {
        throw new Error('Search options must be an object.');
    }

    const activeEntries = Object.entries(options).filter(
        ([, value]) => value !== undefined && value !== null && value !== ''
    );
    const allowedFields = type === 'world' ? WORLD_SEARCH_FIELDS : type === 'user' ? USER_SEARCH_FIELDS : new Set();
    if (activeEntries.some(([key]) => !allowedFields.has(key))) {
        throw new Error('Unsupported search option.');
    }

    const addEnum = (name, allowed) => {
        const value = options[name];
        if (value === undefined || value === null || value === '') return;
        if (typeof value !== 'string' || !allowed.has(value)) throw new Error('Invalid search option.');
        params.set(name, value);
    };
    const addBoolean = (name) => {
        const value = options[name];
        if (value === undefined || value === null) return;
        if (typeof value !== 'boolean') throw new Error('Invalid search option.');
        params.set(name, String(value));
    };

    if (type === 'world') {
        addEnum('worldOption', WORLD_OPTIONS);
        addEnum('sort', WORLD_SORTS);
        addEnum('order', SEARCH_ORDERS);
        addBoolean('featured');
        addEnum('user', new Set(['me']));
        addEnum('releaseStatus', new Set(['all']));
        if (options.tag !== undefined && options.tag !== null && options.tag !== '') {
            const tag = String(options.tag);
            if (tag.length > 256 || !tag.split(',').every(isSafeWorldTag)) {
                throw new Error('Invalid search option.');
            }
            params.set('tag', tag);
        }
        return;
    }

    if (type === 'user') {
        addBoolean('fuzzy');
        addEnum('sort', USER_SORTS);
        addEnum('order', SEARCH_ORDERS);
        addEnum('customFields', new Set(['displayName']));
    }
}

function isSafeWorldTag(tag) {
    return (
        tag.length > 0 &&
        tag.length <= 128 &&
        !Array.from(tag).some((character) => /[\u0000-\u001F\u007F]/.test(character))
    );
}

function analyticsInteger(value, minimum, maximum) {
    if (!Number.isInteger(value) || value < minimum || value > maximum) {
        throw new Error('Invalid analytics query.');
    }
    return value;
}

function analyticsDate(value) {
    if (
        typeof value !== 'string' ||
        value.length === 0 ||
        value.length > 128 ||
        value.includes('\0') ||
        !Number.isFinite(Date.parse(value))
    ) {
        throw new Error('Invalid analytics date.');
    }
    return value;
}

const SEARCH_DETAIL_PATHS = Object.freeze({
    user: 'users',
    world: 'worlds',
    avatar: 'avatars',
    group: 'groups'
});

const FEED_HISTORY_FILTERS = new Set(['GPS', 'Status', 'Bio', 'Avatar', 'Online', 'Offline']);
const GAME_LOG_HISTORY_FILTERS = new Set([
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

function historyText(value, maximumLength, allowEmpty = false) {
    if (
        typeof value !== 'string' ||
        (!allowEmpty && value.length === 0) ||
        value.length > maximumLength ||
        value.includes('\0')
    ) {
        throw new Error('Invalid history query.');
    }
    return value;
}

function historyInteger(value, minimum, maximum) {
    if (!Number.isSafeInteger(value) || value < minimum || value > maximum) {
        throw new Error('Invalid history query.');
    }
    return value;
}

function historyDate(value) {
    const text = historyText(value, 128);
    if (!Number.isFinite(Date.parse(text))) throw new Error('Invalid history date.');
    return text;
}

function historyEntityId(value, prefix) {
    const text = historyText(value, 256);
    if (!text.startsWith(prefix) || !/^[A-Za-z0-9_-]+$/.test(text)) {
        throw new Error('Invalid history entity id.');
    }
    return text;
}

function appendOptionalHistoryText(params, name, value, maximumLength) {
    if (value === undefined || value === null || value === '') return;
    params.set(name, historyText(value, maximumLength));
}

function appendOptionalHistoryDate(params, name, value) {
    if (value === undefined || value === null || value === '') return;
    params.set(name, historyDate(value));
}

function historyTableParams(filters, favoritesOnly, maxEntries, allowedFilters) {
    if (!Array.isArray(filters) || filters.length > allowedFilters.size) {
        throw new Error('Invalid history filters.');
    }
    const normalized = [...new Set(filters)];
    if (normalized.some((filter) => !allowedFilters.has(filter))) {
        throw new Error('Invalid history filter.');
    }
    const params = new URLSearchParams({
        favoritesOnly: String(Boolean(favoritesOnly)),
        maxEntries: String(historyInteger(maxEntries, 1, 100_000))
    });
    if (normalized.length) params.set('filters', normalized.join(','));
    return params;
}

export const webApi = new WebApiClient();

/** @param {unknown} payload */
export function itemsFrom(payload) {
    if (Array.isArray(payload)) return payload;
    if (payload && typeof payload === 'object') {
        // @ts-ignore
        return payload.items || payload.results || payload.data || [];
    }
    return [];
}
