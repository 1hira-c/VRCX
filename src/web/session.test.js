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
    Object.assign(state.auth, { bootstrapped: false, authenticated: false, device: null, expiresAt: null });
    Object.assign(state.vrchatAuth, {
        ok: false,
        status: 'requires_authentication',
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
    vi.unstubAllGlobals();
});

describe('webSession', () => {
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
