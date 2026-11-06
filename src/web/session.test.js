import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const webApi = vi.hoisted(() => ({
    getAuthStatus: vi.fn(),
    getVrchatAuthStatus: vi.fn(),
    getStatus: vi.fn(),
    getFriends: vi.fn(),
    getRecentFriendHistory: vi.fn(),
    getFeed: vi.fn(),
    getNotifications: vi.fn(),
    getFavorites: vi.fn(),
    getGameLog: vi.fn(),
    getAnalytics: vi.fn(),
    getDevices: vi.fn(),
    bootstrap: vi.fn(),
    login: vi.fn(),
    recoverAdmin: vi.fn(),
    logout: vi.fn(),
    createPairing: vi.fn(),
    revokeDevice: vi.fn(),
    redeemPairing: vi.fn(),
    loginVrchat: vi.fn(),
    submitVrchatTwoFactor: vi.fn(),
    resendVrchatEmailOtp: vi.fn(),
    logoutVrchat: vi.fn()
}));

vi.mock('./api', () => ({
    webApi,
    itemsFrom: (payload) => (Array.isArray(payload) ? payload : payload?.items || [])
}));

import { canBrowseWeb, webSession } from './session';

class MockEventSource {
    static streams = [];

    constructor(url) {
        this.url = url;
        this.listeners = new Map();
        this.close = vi.fn();
        MockEventSource.streams.push(this);
    }

    addEventListener(type, listener) {
        this.listeners.set(type, listener);
    }

    emit(type, data = '') {
        this.listeners.get(type)?.({ data });
    }
}

function flush() {
    return new Promise((resolve) => setTimeout(resolve, 0));
}

beforeEach(() => {
    vi.clearAllMocks();
    MockEventSource.streams = [];
    vi.stubGlobal('EventSource', MockEventSource);
    webSession.closeEvents();

    const state = webSession.state;
    state.ready = false;
    state.pendingRequests = 0;
    state.error = '';
    state.workerConnection = 'checking';
    state.workerFailureReason = null;
    Object.assign(state.auth, { bootstrapped: false, authenticated: false, device: null, expiresAt: null });
    Object.assign(state.vrchatAuth, {
        ok: false,
        status: 'unknown',
        challenge: null,
        currentUser: null,
        errorCode: null
    });
    Object.assign(state.status, {
        configured: false,
        authenticationRequired: true,
        currentUserId: null,
        sync: {},
        pipeline: {},
        counts: {}
    });
    Object.assign(state.data, {
        friends: [],
        friendHistory: [],
        feed: [],
        notifications: [],
        favorites: [],
        gameLog: [],
        analytics: {},
        devices: []
    });
    state.pages = {};
    state.resourceRevisions = {};
    state.lastUpdatedAt = null;

    webApi.getAuthStatus.mockResolvedValue({ bootstrapped: true, authenticated: true, device: { id: 'browser' } });
    webApi.getVrchatAuthStatus.mockResolvedValue({
        ok: true,
        status: 'authenticated',
        challenge: null,
        currentUser: { id: 'usr_current' },
        errorCode: null
    });
    webApi.getStatus.mockResolvedValue({
        configured: true,
        authenticationRequired: false,
        sync: { state: 'healthy', stale: false },
        pipeline: {},
        counts: {}
    });
    webApi.getFriends.mockResolvedValue({
        items: [{ id: 'usr_1', displayName: 'A' }],
        total: 1,
        offset: 0,
        limit: 100
    });
    webApi.getRecentFriendHistory.mockResolvedValue({ items: [] });
    webApi.getFeed.mockResolvedValue({ items: [] });
    webApi.getNotifications.mockResolvedValue({ items: [] });
    webApi.getFavorites.mockResolvedValue({ items: [] });
    webApi.getGameLog.mockResolvedValue({ items: [] });
    webApi.getAnalytics.mockResolvedValue({});
});

afterEach(() => {
    webSession.closeEvents();
    vi.useRealTimers();
    vi.unstubAllGlobals();
});

describe('webSession', () => {
    it.each([
        ['proxy 502', { status: 502 }],
        ['login timeout', { status: 504 }],
        ['unrelated 503', { status: 503, body: { error: 'upstream_unavailable' } }],
        ['network failure', {}],
        ['rate limit', { status: 429 }]
    ])('does not turn a %s from login or OTP into a worker outage', async (_name, fields) => {
        await webSession.initialize();
        const auth = webSession.state.vrchatAuth;
        const failure = Object.assign(new Error('Original authentication error'), fields);
        webApi.loginVrchat.mockRejectedValueOnce(failure);
        webApi.submitVrchatTwoFactor.mockRejectedValueOnce(failure);

        await expect(webSession.loginVrchat('test-user', 'password-not-retried')).rejects.toBe(failure);
        expect(webSession.state.workerConnection).toBe('available');
        expect(webSession.state.vrchatAuth).toBe(auth);
        expect(webSession.state.error).not.toContain('再接続');
        await expect(webSession.submitVrchatTwoFactor('totp', '123456')).rejects.toBe(failure);
        expect(webSession.state.workerConnection).toBe('available');
        expect(webSession.state.vrchatAuth).toBe(auth);
    });

    it('retries an initial rate-limited status check after Retry-After without inferring an outage', async () => {
        vi.useFakeTimers();
        webApi.getVrchatAuthStatus.mockRejectedValueOnce(
            Object.assign(new Error('Too many requests'), {
                status: 429,
                retryAfterMs: 75_000
            })
        );
        await webSession.initialize();
        expect(webSession.state.workerConnection).toBe('check_failed');
        expect(webSession.state.vrchatAuth.status).toBe('unknown');
        expect(webSession.state.error).toContain('要求が多すぎ');
        const before = webApi.getVrchatAuthStatus.mock.calls.length;

        MockEventSource.streams[0].emit('status');
        await webSession.refreshVrchatAuth({ silent: true });
        expect(webApi.getVrchatAuthStatus).toHaveBeenCalledTimes(before);
        await vi.advanceTimersByTimeAsync(74_999);
        expect(webApi.getVrchatAuthStatus).toHaveBeenCalledTimes(before);
        await vi.advanceTimersByTimeAsync(1);
        expect(webApi.getVrchatAuthStatus).toHaveBeenCalledTimes(before + 1);
        expect(webSession.state.workerConnection).toBe('available');
        expect(webSession.state.error).toBe('');
        expect(webApi.loginVrchat).not.toHaveBeenCalled();
        expect(webApi.submitVrchatTwoFactor).not.toHaveBeenCalled();
    });

    it('shares an in-flight status check across SSE and retry callers', async () => {
        await webSession.initialize();
        let complete;
        webApi.getVrchatAuthStatus.mockImplementationOnce(
            () =>
                new Promise((resolve) => {
                    complete = resolve;
                })
        );
        const before = webApi.getVrchatAuthStatus.mock.calls.length;
        const first = webSession.refreshVrchatAuth({ silent: true });
        const second = webSession.refreshVrchatAuth({ silent: true });
        MockEventSource.streams[0].emit('status');
        expect(webApi.getVrchatAuthStatus).toHaveBeenCalledTimes(before + 1);
        complete({ ok: true, status: 'authenticated', currentUser: { id: 'usr_current' } });
        await Promise.all([first, second]);
        expect(webSession.state.workerConnection).toBe('available');
    });

    it('ignores a status result which arrives after administrator logout', async () => {
        await webSession.initialize();
        let rejectStatus;
        webApi.getVrchatAuthStatus.mockImplementationOnce(
            () =>
                new Promise((_resolve, reject) => {
                    rejectStatus = reject;
                })
        );
        const pending = webSession.refreshVrchatAuth({ silent: true });
        await webSession.logout();
        rejectStatus(
            Object.assign(new Error('Unavailable'), { status: 503, body: { error: 'vrchat_worker_unavailable' } })
        );
        await pending;
        expect(webSession.state.auth.authenticated).toBe(false);
        expect(webSession.state.workerConnection).toBe('available');
        expect(webSession.state.error).toBe('');
    });

    it('explains database initialization failure without requesting new credentials', async () => {
        webApi.getVrchatAuthStatus.mockRejectedValueOnce(
            Object.assign(new Error('Unavailable'), {
                status: 503,
                body: { error: 'vrchat_worker_unavailable', reason: 'database_initialization_failed' }
            })
        );
        await webSession.initialize();
        expect(webSession.state.workerConnection).toBe('unavailable');
        expect(webSession.state.workerFailureReason).toBe('database_initialization_failed');
        expect(webSession.state.error).toContain('データベース');
        expect(webApi.loginVrchat).not.toHaveBeenCalled();
    });

    it('preserves confirmed authentication and saved history when the worker returns 503', async () => {
        await webSession.initialize();
        const auth = webSession.state.vrchatAuth;
        const friends = webSession.state.data.friends;
        webSession.state.data.feed = [{ id: 'saved-feed' }];
        const feed = webSession.state.data.feed;
        const unavailable = Object.assign(new Error('Service Unavailable'), {
            status: 503,
            body: { error: 'vrchat_worker_unavailable' }
        });
        webApi.getVrchatAuthStatus.mockRejectedValueOnce(unavailable);

        await webSession.refreshVrchatAuth({ silent: true });

        expect(webSession.state.workerConnection).toBe('unavailable');
        expect(webSession.state.vrchatAuth).toBe(auth);
        expect(webSession.state.vrchatAuth.status).toBe('authenticated');
        expect(webSession.state.auth.authenticated).toBe(true);
        expect(webSession.state.data.friends).toBe(friends);
        expect(webSession.state.data.feed).toBe(feed);
        expect(canBrowseWeb()).toBe(true);
        expect(webSession.state.error).toContain('認証切れとは限りません');
    });

    it('does not infer expired VRChat authentication from an initial worker outage', async () => {
        webApi.getVrchatAuthStatus.mockRejectedValueOnce(
            Object.assign(new Error('Unavailable'), { status: 503, body: { error: 'vrchat_worker_unavailable' } })
        );
        webApi.getFriends.mockResolvedValueOnce({ items: [] });

        await webSession.initialize();

        expect(webSession.state.workerConnection).toBe('unavailable');
        expect(webSession.state.vrchatAuth.status).toBe('unknown');
        expect(webSession.state.auth.authenticated).toBe(true);
    });

    it('detects worker recovery with a bounded local status read without resubmitting credentials', async () => {
        vi.useFakeTimers();
        await webSession.initialize();
        webApi.getVrchatAuthStatus.mockRejectedValueOnce(
            Object.assign(new Error('Unavailable'), { status: 503, body: { error: 'vrchat_worker_unavailable' } })
        );
        await webSession.refreshVrchatAuth({ silent: true });
        const statusCalls = webApi.getVrchatAuthStatus.mock.calls.length;

        await vi.advanceTimersByTimeAsync(29_999);
        expect(webApi.getVrchatAuthStatus).toHaveBeenCalledTimes(statusCalls);
        await vi.advanceTimersByTimeAsync(1);

        expect(webApi.getVrchatAuthStatus).toHaveBeenCalledTimes(statusCalls + 1);
        expect(webSession.state.workerConnection).toBe('available');
        expect(webSession.state.vrchatAuth.status).toBe('authenticated');
        expect(webSession.state.error).toBe('');
        expect(webApi.loginVrchat).not.toHaveBeenCalled();
        await vi.advanceTimersByTimeAsync(30_000);
        expect(webApi.getVrchatAuthStatus).toHaveBeenCalledTimes(statusCalls + 1);
    });

    it('stops worker reconnect status checks when the administrator logs out', async () => {
        vi.useFakeTimers();
        await webSession.initialize();
        webApi.getVrchatAuthStatus.mockRejectedValueOnce(
            Object.assign(new Error('Unavailable'), { status: 503, body: { error: 'vrchat_worker_unavailable' } })
        );
        await webSession.refreshVrchatAuth({ silent: true });
        await webSession.logout();
        const statusCalls = webApi.getVrchatAuthStatus.mock.calls.length;

        await vi.advanceTimersByTimeAsync(30_000);

        expect(webApi.getVrchatAuthStatus).toHaveBeenCalledTimes(statusCalls);
        expect(webSession.state.auth.authenticated).toBe(false);
    });

    it('reports the actual event stream lifecycle and keeps existing data while reconnecting', async () => {
        await webSession.initialize();
        const stream = MockEventSource.streams[0];
        expect(webSession.state.eventsConnection).toBe('connecting');

        stream.onopen();
        expect(webSession.state.eventsConnection).toBe('connected');
        const friends = webSession.state.data.friends;

        const clock = vi.spyOn(Date, 'now').mockReturnValue(100_000);
        try {
            stream.onerror();
            await flush();
        } finally {
            clock.mockRestore();
        }
        expect(webSession.state.eventsConnection).toBe('reconnecting');
        expect(webSession.state.data.friends).toBe(friends);
        expect(stream.close).not.toHaveBeenCalled();

        stream.onopen();
        expect(webSession.state.eventsConnection).toBe('connected');

        await webSession.logout();
        expect(webSession.state.eventsConnection).toBe('closed');
        expect(stream.close).toHaveBeenCalledOnce();
        stream.onopen();
        expect(webSession.state.eventsConnection).toBe('closed');
    });

    it('reports a terminal EventSource failure as closed rather than connected', async () => {
        await webSession.initialize();
        const stream = MockEventSource.streams[0];
        stream.readyState = 2;
        const clock = vi.spyOn(Date, 'now').mockReturnValue(100_000);
        try {
            stream.onerror();
            await flush();
        } finally {
            clock.mockRestore();
        }

        expect(webSession.state.eventsConnection).toBe('closed');
        expect(webSession.state.data.friends).toEqual([{ id: 'usr_1', displayName: 'A' }]);
    });

    it('keeps named resources in the browser and refreshes the affected view from SSE', async () => {
        await webSession.initialize();

        expect(webSession.state.data.friends).toEqual([{ id: 'usr_1', displayName: 'A' }]);
        const stream = MockEventSource.streams[0];
        expect(stream.url).toBe('/api/v1/events');

        const calls = webApi.getFriends.mock.calls.length;
        const revision = webSession.state.resourceRevisions.friends;
        stream.emit('data', JSON.stringify({ resource: 'friends' }));
        await flush();

        expect(webApi.getFriends.mock.calls.length).toBeGreaterThan(calls);
        expect(webSession.state.resourceRevisions.friends).toBeGreaterThan(revision);
    });

    it('refreshes every fixed read when the projection publisher emits the snapshot resource', async () => {
        await webSession.initialize();
        const stream = MockEventSource.streams[0];
        const before = Object.fromEntries(
            [
                'getStatus',
                'getFriends',
                'getRecentFriendHistory',
                'getFeed',
                'getNotifications',
                'getFavorites',
                'getGameLog',
                'getAnalytics'
            ].map((name) => [name, webApi[name].mock.calls.length])
        );

        stream.emit('data', JSON.stringify({ resource: 'snapshot' }));
        await flush();

        for (const [name, callCount] of Object.entries(before)) {
            expect(webApi[name].mock.calls.length, name).toBeGreaterThan(callCount);
        }
    });

    it('does not treat an unknown SSE resource as an arbitrary read endpoint', async () => {
        await webSession.initialize();
        const stream = MockEventSource.streams[0];
        const friendCalls = webApi.getFriends.mock.calls.length;

        stream.emit('data', JSON.stringify({ resource: '/caller-controlled/path' }));
        await flush();

        expect(webApi.getFriends).toHaveBeenCalledTimes(friendCalls);
    });

    it('recovers the administrator session before refreshing the authenticated Web state', async () => {
        webApi.recoverAdmin.mockResolvedValue({ ok: true });

        await webSession.recoverAdmin('host-issued-code', 'new-administrator-password', 'Recovered browser');

        expect(webApi.recoverAdmin).toHaveBeenCalledWith(
            'host-issued-code',
            'new-administrator-password',
            'Recovered browser'
        );
        expect(webSession.state.auth.authenticated).toBe(true);
    });

    it('keeps administrator login errors separate from VRChat authentication errors', async () => {
        const error = Object.assign(new Error('Unauthorized'), {
            status: 401,
            body: { error: 'invalid_admin_credentials' }
        });
        webApi.login.mockRejectedValueOnce(error);

        await expect(webSession.login('wrong administrator password', 'Living-room browser')).rejects.toBe(error);

        expect(webApi.login).toHaveBeenCalledWith('wrong administrator password', 'Living-room browser');
        expect(webSession.state.error).toBe('管理者パスワードが正しくありません。');
    });

    it('returns to an unauthenticated state when the server revokes the browser session', async () => {
        await webSession.initialize();
        const stream = MockEventSource.streams[0];
        webApi.getAuthStatus.mockResolvedValueOnce({ bootstrapped: true, authenticated: false });

        stream.onerror?.();
        await flush();

        expect(webSession.state.auth.authenticated).toBe(false);
        expect(stream.close).toHaveBeenCalledOnce();
        expect(webSession.state.error).toContain('セッションが失効しました');
    });

    it('loads fixed reads and starts SSE when VRChat requires reauthentication', async () => {
        webApi.getVrchatAuthStatus.mockResolvedValueOnce({
            ok: false,
            status: 'requires_authentication',
            challenge: null,
            currentUser: null,
            errorCode: 'authentication_required'
        });
        webApi.getStatus.mockResolvedValueOnce({
            configured: true,
            authenticationRequired: true,
            sync: { state: 'stale', stale: true },
            pipeline: {},
            counts: {}
        });

        await webSession.initialize();

        expect(webSession.state.auth.authenticated).toBe(true);
        expect(webSession.state.vrchatAuth.status).toBe('requires_authentication');
        expect(webSession.state.data.friends).toEqual([{ id: 'usr_1', displayName: 'A' }]);
        expect(MockEventSource.streams).toHaveLength(1);
        expect(canBrowseWeb()).toBe(true);
    });

    it('only redirects a completely fresh VRChat session to login', async () => {
        webApi.getVrchatAuthStatus.mockResolvedValueOnce({
            ok: false,
            status: 'requires_authentication',
            challenge: null,
            currentUser: null,
            errorCode: 'authentication_required'
        });
        webApi.getStatus.mockResolvedValueOnce({
            configured: true,
            authenticationRequired: true,
            currentUserId: null,
            sync: { state: 'stale', stale: true },
            pipeline: {},
            counts: {}
        });
        webApi.getFriends.mockResolvedValueOnce({ items: [] });
        webApi.getRecentFriendHistory.mockResolvedValueOnce({ items: [] });
        webApi.getFeed.mockResolvedValueOnce({ items: [] });
        webApi.getNotifications.mockResolvedValueOnce({ items: [] });
        webApi.getFavorites.mockResolvedValueOnce({ items: [] });
        webApi.getGameLog.mockResolvedValueOnce({ items: [] });

        await webSession.initialize();
        webSession.state.data.devices = [{ id: 'admin-session' }];

        expect(canBrowseWeb()).toBe(false);

        webSession.state.status.currentUserId = 'usr_existing';
        expect(canBrowseWeb()).toBe(true);
    });

    it('maps only explicit device session fields', async () => {
        webApi.getDevices.mockResolvedValue({
            items: [
                {
                    id: 'session-1',
                    deviceName: 'Phone',
                    issuedAt: '2026-01-01T00:00:00Z',
                    expiresAt: '2026-02-01T00:00:00Z',
                    revokedAt: null,
                    lastSeenAt: '2026-01-02T00:00:00Z',
                    rawCookie: 'must-not-cross-the-adapter'
                }
            ]
        });

        await webSession.loadDevices();

        expect(webSession.state.data.devices).toEqual([
            {
                id: 'session-1',
                deviceName: 'Phone',
                issuedAt: '2026-01-01T00:00:00Z',
                expiresAt: '2026-02-01T00:00:00Z',
                revokedAt: null,
                lastSeenAt: '2026-01-02T00:00:00Z'
            }
        ]);
    });
});
