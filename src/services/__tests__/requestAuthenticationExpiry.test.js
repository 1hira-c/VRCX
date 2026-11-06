import { beforeEach, describe, expect, it, vi } from 'vitest';

const mocks = vi.hoisted(() => ({
    execute: vi.fn(),
    getCurrentUser: vi.fn(),
    watchState: { isLoggedIn: true },
    runtimeCapabilities: { serverMode: true },
    authStore: {
        handleAutoLogin: vi.fn(),
        handleLogoutEvent: vi.fn(),
        twoFactorAuthDialogVisible: false,
        serverAuthChallenge: null
    }
}));

vi.mock('../../stores', () => ({
    useAuthStore: () => mocks.authStore,
    useModalStore: () => ({}),
    useNotificationStore: () => ({}),
    useUpdateLoopStore: () => ({})
}));

vi.mock('../../coordinators/userCoordinator', () => ({
    getCurrentUser: mocks.getCurrentUser
}));

vi.mock('../webapi.js', () => ({
    default: { execute: mocks.execute }
}));

vi.mock('../watchState', () => ({ watchState: mocks.watchState }));
vi.mock('../runtimeCapabilities', () => ({ runtimeCapabilities: mocks.runtimeCapabilities }));
vi.mock('../appConfig.js', () => ({
    AppDebug: { endpointDomain: 'https://vrchat.example.invalid/api/1' },
    isApiLogSuppressed: () => true,
    logWebRequest: vi.fn()
}));
vi.mock('vue-sonner', () => ({ toast: { error: vi.fn() } }));

import { request } from '../request';

function rejectAuthentication(message = '"Unauthorized"', status = 401) {
    mocks.execute.mockImplementation(async () => ({
        status,
        data: JSON.stringify({ error: { status_code: status, message } })
    }));
}

describe('server cookie-session expiry', () => {
    beforeEach(() => {
        vi.clearAllMocks();
        mocks.runtimeCapabilities.serverMode = true;
        mocks.watchState.isLoggedIn = true;
        mocks.authStore.twoFactorAuthDialogVisible = false;
        mocks.authStore.serverAuthChallenge = null;
        rejectAuthentication();
    });

    it.each(['"Unauthorized"', '"Missing Credentials"'])(
        'uses the original auth-expiry handler when auth/user returns %s',
        async (message) => {
            rejectAuthentication(message);

            await expect(request('auth/user')).rejects.toMatchObject({ status: 401, endpoint: 'auth/user' });

            expect(mocks.authStore.handleAutoLogin).toHaveBeenCalledTimes(1);
            expect(mocks.getCurrentUser).not.toHaveBeenCalled();
        }
    );

    it('keeps the original current-user recheck for other Unauthorized endpoints', async () => {
        await expect(request('users/usr_friend')).rejects.toMatchObject({ status: 401 });

        expect(mocks.getCurrentUser).toHaveBeenCalledTimes(1);
        expect(mocks.authStore.handleAutoLogin).not.toHaveBeenCalled();
    });

    it('keeps a successful current-user 2FA challenge', async () => {
        mocks.execute.mockResolvedValue({
            status: 200,
            data: JSON.stringify({ requiresTwoFactorAuth: ['totp'] })
        });

        await expect(request('auth/user')).resolves.toEqual({ requiresTwoFactorAuth: ['totp'] });

        expect(mocks.authStore.handleAutoLogin).not.toHaveBeenCalled();
    });

    it('does not treat an incorrect initial password as an expired logged-in session', async () => {
        mocks.watchState.isLoggedIn = false;

        await expect(request('auth/user')).rejects.toMatchObject({ status: 401 });

        expect(mocks.authStore.handleAutoLogin).not.toHaveBeenCalled();
    });

    it.each([false, true])('preserves a pending server 2FA challenge when isLoggedIn is %s', async (isLoggedIn) => {
        mocks.watchState.isLoggedIn = isLoggedIn;
        mocks.authStore.serverAuthChallenge = { kinds: ['totp'] };

        await expect(request('auth/user')).rejects.toMatchObject({ status: 401 });
        await expect(request('auth/twofactorauth/totp/verify', { method: 'POST' })).rejects.toMatchObject({
            status: 401
        });

        expect(mocks.authStore.handleAutoLogin).not.toHaveBeenCalled();
        expect(mocks.authStore.serverAuthChallenge).toEqual({ kinds: ['totp'] });
    });

    it('keeps the desktop auth/user failure path unchanged', async () => {
        mocks.runtimeCapabilities.serverMode = false;

        await expect(request('auth/user')).rejects.toMatchObject({ status: 401 });

        expect(mocks.authStore.handleAutoLogin).not.toHaveBeenCalled();
        expect(mocks.getCurrentUser).not.toHaveBeenCalled();
    });

    it('does not add logout handling to unrelated auth/user 401 errors', async () => {
        rejectAuthentication('Check your email');

        await expect(request('auth/user')).rejects.toMatchObject({ status: 401 });

        expect(mocks.authStore.handleAutoLogin).not.toHaveBeenCalled();
    });

    it('does not invalidate an authenticated session on an upstream outage', async () => {
        rejectAuthentication('Bad Gateway', 502);

        await expect(request('auth/user')).rejects.toMatchObject({ status: 502 });

        expect(mocks.authStore.handleAutoLogin).not.toHaveBeenCalled();
    });

    it('releases failed GET requests without adding an unhandled rejection', async () => {
        await expect(request('auth/user')).rejects.toMatchObject({ status: 401 });
        await expect(request('auth/user')).rejects.toMatchObject({ status: 401 });

        expect(mocks.execute).toHaveBeenCalledTimes(2);
    });
});
