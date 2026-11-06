import { reactive } from 'vue';

import { itemsFrom, webApi } from './api';
import { errorMessage } from './format';

const DEFAULT_PAGE = Object.freeze({ offset: 0, limit: 100 });
const WORKER_RECONNECT_DELAY = 30_000;

const resourceRequests = Object.freeze({
    friends: (page) => webApi.getFriends(page),
    friendHistory: (page) => webApi.getRecentFriendHistory(page),
    feed: (page) => webApi.getFeed(page),
    notifications: (page) => webApi.getNotifications(page),
    favorites: (page) => webApi.getFavorites(page),
    gameLog: (page) => webApi.getGameLog(page)
});

const state = reactive({
    ready: false,
    pendingRequests: 0,
    error: '',
    auth: {
        bootstrapped: false,
        authenticated: false,
        device: null,
        expiresAt: null
    },
    vrchatAuth: {
        ok: false,
        status: 'unknown',
        challenge: null,
        currentUser: null,
        errorCode: null
    },
    status: {
        configured: false,
        authenticationRequired: true,
        currentUserId: null,
        sync: {},
        pipeline: {},
        counts: {}
    },
    data: {
        friends: [],
        friendHistory: [],
        feed: [],
        notifications: [],
        favorites: [],
        gameLog: [],
        analytics: {},
        devices: []
    },
    pages: {},
    resourceRevisions: {},
    lastUpdatedAt: null,
    eventsConnection: 'idle',
    workerConnection: 'checking',
    workerFailureReason: null
});

let events;
let lastAuthCheckAt = 0;
let workerReconnectTimer;
let workerFailureMessage = '';
let workerStatusRequest;
let workerStatusGeneration = 0;
let workerStatusNotBefore = 0;

export const webSession = {
    state,
    canBrowse: canBrowseWeb,
    initialize,
    refreshAuth,
    refreshVrchatAuth,
    refreshAll,
    loadResource,
    loadStatus,
    loadAnalytics,
    loadDevices,
    bootstrap,
    login,
    recoverAdmin,
    logout,
    loginVrchat,
    submitVrchatTwoFactor,
    resendVrchatEmailOtp,
    logoutVrchat,
    createPairing,
    revokeDevice,
    redeemPairing,
    clearError,
    closeEvents
};

export function hasExistingWebProjection() {
    if (state.status?.currentUserId) return true;
    if (
        ['friends', 'friendHistory', 'feed', 'notifications', 'favorites', 'gameLog'].some(
            (resource) => Array.isArray(state.data[resource]) && state.data[resource].length > 0
        )
    ) {
        return true;
    }
    return Object.values(state.status?.counts || {}).some((value) => Number(value) > 0);
}

export function canBrowseWeb() {
    return Boolean(
        state.ready &&
        state.auth.authenticated &&
        (state.vrchatAuth.status === 'authenticated' || hasExistingWebProjection())
    );
}

async function initialize() {
    try {
        await refreshAuth();
        if (state.auth.authenticated) {
            await refreshVrchatAuth({ silent: true });
            // The administrator session authorizes these fixed reads. VRChat
            // authentication only controls live sync, so imported/history data
            // remains available while reauthentication is required.
            await refreshAll({ silent: true });
        }
    } catch (error) {
        captureError(error);
    } finally {
        state.ready = true;
    }
}

async function refreshAuth() {
    const result = await webApi.getAuthStatus();
    state.auth.bootstrapped = Boolean(result.bootstrapped);
    state.auth.authenticated = Boolean(result.authenticated);
    state.auth.device = result.device || null;
    state.auth.expiresAt = result.expiresAt || null;
    return result;
}

async function refreshVrchatAuth(options = {}) {
    if (workerStatusRequest) return workerStatusRequest;
    if (Date.now() < workerStatusNotBefore) return undefined;
    const request = runWorkerAuth(() => webApi.getVrchatAuthStatus(), options, workerStatusGeneration);
    workerStatusRequest = request;
    try {
        return await request;
    } finally {
        if (workerStatusRequest === request) workerStatusRequest = undefined;
    }
}

async function refreshAll(options = {}) {
    if (!state.auth.authenticated) return;
    return run(async () => {
        await Promise.all([
            loadStatus({ silent: true }),
            loadResource('friends', DEFAULT_PAGE, { silent: true }),
            loadResource('feed', DEFAULT_PAGE, { silent: true }),
            loadResource('notifications', DEFAULT_PAGE, { silent: true }),
            loadResource('favorites', DEFAULT_PAGE, { silent: true }),
            loadResource('gameLog', DEFAULT_PAGE, { silent: true }),
            loadResource('friendHistory', DEFAULT_PAGE, { silent: true }),
            loadAnalytics({ silent: true })
        ]);
        state.lastUpdatedAt = new Date().toISOString();
        startEvents();
    }, options);
}

async function loadStatus(options = {}) {
    return run(async () => {
        state.status = await webApi.getStatus();
        return state.status;
    }, options);
}

async function loadResource(resource, page = DEFAULT_PAGE, options = {}) {
    const request = resourceRequests[resource];
    if (!request) throw new Error('Unsupported Web resource.');

    return run(async () => {
        const payload = await request(page);
        state.data[resource] = itemsFrom(payload);
        state.pages[resource] = {
            total: Number(payload?.total) || state.data[resource].length,
            offset: Number(payload?.offset) || 0,
            limit: Number(payload?.limit) || page.limit || DEFAULT_PAGE.limit
        };
        state.resourceRevisions[resource] = (state.resourceRevisions[resource] || 0) + 1;
        state.lastUpdatedAt = new Date().toISOString();
        return payload;
    }, options);
}

async function loadAnalytics(options = {}) {
    return run(async () => {
        state.data.analytics = await webApi.getAnalytics();
        return state.data.analytics;
    }, options);
}

async function loadDevices(options = {}) {
    return run(async () => {
        const payload = await webApi.getDevices();
        state.data.devices = itemsFrom(payload).map(mapDeviceSession);
        return state.data.devices;
    }, options);
}

async function bootstrap(password, setupToken, deviceName) {
    await run(() => webApi.bootstrap(password, setupToken, deviceName));
    await refreshAuth();
    await refreshVrchatAuth({ silent: true });
    await refreshAll({ silent: true });
}

async function login(password, deviceName) {
    await run(() => webApi.login(password, deviceName));
    await refreshAuth();
    await refreshVrchatAuth({ silent: true });
    await refreshAll({ silent: true });
}

async function recoverAdmin(code, password, deviceName) {
    await run(() => webApi.recoverAdmin(code, password, deviceName));
    await refreshAuth();
    await refreshVrchatAuth({ silent: true });
    await refreshAll({ silent: true });
}

async function logout() {
    try {
        await webApi.logout();
    } finally {
        closeEvents();
        state.auth.authenticated = false;
        state.auth.device = null;
        state.auth.expiresAt = null;
    }
}

async function redeemPairing(code, deviceName) {
    await run(() => webApi.redeemPairing(code, deviceName));
    await refreshAuth();
    await refreshVrchatAuth({ silent: true });
    await refreshAll({ silent: true });
}

async function loginVrchat(username, password) {
    return runWorkerAuth(() => webApi.loginVrchat(username, password));
}

async function submitVrchatTwoFactor(kind, code) {
    return runWorkerAuth(() => webApi.submitVrchatTwoFactor(kind, code));
}

async function resendVrchatEmailOtp() {
    return runWorkerAuth(() => webApi.resendVrchatEmailOtp());
}

async function logoutVrchat() {
    return runWorkerAuth(() => webApi.logoutVrchat());
}

async function createPairing() {
    return run(() => webApi.createPairing());
}

async function revokeDevice(sessionId) {
    await run(() => webApi.revokeDevice(sessionId));
    if (state.auth.device?.id === sessionId) {
        closeEvents();
        state.auth.authenticated = false;
        state.auth.device = null;
        state.auth.expiresAt = null;
        return;
    }
    await loadDevices({ silent: true });
}

function clearError() {
    state.error = '';
}

async function runWorkerAuth(work, options = {}, statusGeneration) {
    const statusRead = statusGeneration !== undefined;
    return run(async () => {
        try {
            const payload = await work();
            if (statusRead && statusGeneration !== workerStatusGeneration) return undefined;
            return applyVrchatAuth(payload);
        } catch (error) {
            if (statusRead && statusGeneration !== workerStatusGeneration) return undefined;
            // Only the BFF's explicit worker-unavailable response proves an
            // outage. A failed login/OTP or a proxy error does not.
            if (error?.status === 503 && error?.body?.error === 'vrchat_worker_unavailable') {
                state.workerConnection = 'unavailable';
                state.workerFailureReason =
                    error.body.reason === 'database_initialization_failed' ? 'database_initialization_failed' : null;
                workerFailureMessage = errorMessage(error);
                scheduleWorkerReconnect(error);
            } else if (statusRead && error?.status !== 401) {
                state.workerConnection = 'check_failed';
                state.workerFailureReason = null;
                workerFailureMessage = errorMessage(error);
                scheduleWorkerReconnect(error);
            }
            throw error;
        }
    }, options);
}

function scheduleWorkerReconnect(error) {
    if (!state.auth.authenticated) return;
    const retryAfter = Number(error?.retryAfterMs);
    const delay = Number.isFinite(retryAfter) ? Math.max(WORKER_RECONNECT_DELAY, retryAfter) : WORKER_RECONNECT_DELAY;
    const nextCheckAt = Date.now() + Math.min(delay, 2_147_483_647);
    if (workerReconnectTimer) {
        if (nextCheckAt <= workerStatusNotBefore) return;
        window.clearTimeout(workerReconnectTimer);
    }
    workerStatusNotBefore = nextCheckAt;
    workerReconnectTimer = window.setTimeout(
        () => {
            workerReconnectTimer = undefined;
            if (state.auth.authenticated && ['unavailable', 'check_failed'].includes(state.workerConnection)) {
                // This fixed local status read never submits credentials or starts
                // a new VRChat login; the original runtime owns that session.
                void refreshVrchatAuth({ silent: true, keepError: true });
            }
        },
        Math.min(delay, 2_147_483_647)
    );
}

function clearWorkerReconnect() {
    if (workerReconnectTimer) window.clearTimeout(workerReconnectTimer);
    workerReconnectTimer = undefined;
    workerStatusNotBefore = 0;
}

function applyVrchatAuth(payload) {
    const allowedStatuses = new Set([
        'requires_authentication',
        'authenticating',
        'two_factor_required',
        'authenticated'
    ]);
    const allowedKinds = new Set(['totp', 'otp', 'emailOtp']);
    const status = allowedStatuses.has(payload?.status) ? payload.status : 'unknown';
    const kinds = Array.isArray(payload?.challenge?.kinds)
        ? payload.challenge.kinds.filter((kind) => allowedKinds.has(kind))
        : [];
    const currentUserId =
        status === 'authenticated' && typeof payload?.currentUser?.id === 'string' ? payload.currentUser.id : '';

    if (state.error === workerFailureMessage) clearError();
    workerFailureMessage = '';
    state.vrchatAuth = {
        ok: Boolean(payload?.ok),
        status,
        challenge: status === 'two_factor_required' ? { kinds } : null,
        currentUser: currentUserId ? { id: currentUserId } : null,
        errorCode: typeof payload?.errorCode === 'string' ? payload.errorCode : null
    };
    state.workerConnection = 'available';
    state.workerFailureReason = null;
    clearWorkerReconnect();
    return state.vrchatAuth;
}

function mapDeviceSession(device) {
    return {
        id: typeof device?.id === 'string' ? device.id : '',
        deviceName: typeof device?.deviceName === 'string' ? device.deviceName : '',
        issuedAt: typeof device?.issuedAt === 'string' ? device.issuedAt : null,
        expiresAt: typeof device?.expiresAt === 'string' ? device.expiresAt : null,
        revokedAt: typeof device?.revokedAt === 'string' ? device.revokedAt : null,
        lastSeenAt: typeof device?.lastSeenAt === 'string' ? device.lastSeenAt : null
    };
}

async function run(work, options = {}) {
    const silent = Boolean(options.silent);
    if (!silent) state.pendingRequests++;
    if (!silent && !options.keepError) clearError();
    try {
        return await work();
    } catch (error) {
        if (error?.status === 401 && state.auth.authenticated) {
            state.auth.authenticated = false;
            closeEvents();
        }
        captureError(error);
        if (!silent) throw error;
        return undefined;
    } finally {
        if (!silent) state.pendingRequests--;
    }
}

function captureError(error) {
    state.error = errorMessage(error);
}

function startEvents() {
    if (events || !state.auth.authenticated) return;
    const stream = new EventSource('/api/v1/events');
    events = stream;
    state.eventsConnection = 'connecting';
    stream.onopen = () => {
        if (events === stream) state.eventsConnection = 'connected';
    };
    events.addEventListener('status', () => {
        void loadStatus({ silent: true, keepError: true });
        void refreshVrchatAuth({ silent: true, keepError: true });
    });
    events.addEventListener('pipeline', () => void loadStatus({ silent: true, keepError: true }));
    events.addEventListener('data', (event) => {
        const resource = eventResource(event);
        if (resource === 'snapshot') {
            void refreshAll({ silent: true, keepError: true });
            void refreshVrchatAuth({ silent: true, keepError: true });
            return;
        }
        if (resource && Object.hasOwn(resourceRequests, resource)) {
            void loadResource(resource, DEFAULT_PAGE, { silent: true, keepError: true });
        }
        void loadStatus({ silent: true, keepError: true });
    });
    stream.onerror = () => {
        if (events !== stream) return;
        state.eventsConnection = stream.readyState === 2 ? 'closed' : 'reconnecting';
        void revalidateAfterEventFailure();
    };
}

function closeEvents() {
    workerStatusGeneration++;
    workerStatusRequest = undefined;
    clearWorkerReconnect();
    events?.close();
    events = undefined;
    state.eventsConnection = 'closed';
}

async function revalidateAfterEventFailure() {
    const now = Date.now();
    if (now - lastAuthCheckAt < 15_000) return;
    lastAuthCheckAt = now;
    try {
        await refreshAuth();
        if (!state.auth.authenticated) {
            state.error = 'セッションが失効しました。もう一度ログインしてください。';
            closeEvents();
        }
    } catch {
        // EventSource automatically reconnects. Keep the last successful view visible.
    }
}

function eventResource(event) {
    try {
        const data = JSON.parse(event.data);
        return typeof data?.resource === 'string' ? data.resource : '';
    } catch {
        return '';
    }
}
