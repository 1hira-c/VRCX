import { resolveWebSessionRoute } from './sessionRouting';

const session = {
    ready: true,
    serverAuthenticated: true,
    vrchatStatus: 'requires_authentication',
    workerConnection: 'available',
    browseAllowed: true,
    route: { name: 'login', fullPath: '/login', query: { reauth: '1', redirect: '/social/friend-list' } }
};

describe('Web session routing', () => {
    it('preserves the requested view when an explicit worker outage interrupts reauthentication', () => {
        expect(resolveWebSessionRoute({ ...session, workerConnection: 'unavailable' })).toBe('/social/friend-list');
    });

    it('keeps the real login/OTP error visible when status is unknown or a check failed', () => {
        expect(resolveWebSessionRoute({ ...session, workerConnection: 'check_failed' })).toBeNull();
        expect(resolveWebSessionRoute({ ...session, workerConnection: 'checking' })).toBeNull();
        expect(resolveWebSessionRoute(session)).toBeNull();
    });

    it('retains non-login routes during an outage and waits when no projection can be shown', () => {
        expect(
            resolveWebSessionRoute({
                ...session,
                workerConnection: 'unavailable',
                route: { name: 'search', fullPath: '/search', query: {} }
            })
        ).toBeNull();
        expect(
            resolveWebSessionRoute({ ...session, workerConnection: 'unavailable', browseAllowed: false })
        ).toBeNull();
    });

    it.each(['//example.invalid/', 'https://example.invalid/', '/login?redirect=/feed', ''])(
        'falls back to Feed for an unsafe or recursive destination: %s',
        (redirect) => {
            expect(
                resolveWebSessionRoute({
                    ...session,
                    workerConnection: 'unavailable',
                    route: { ...session.route, query: { redirect } }
                })
            ).toEqual({ name: 'feed' });
        }
    );

    it('returns an authenticated browser to its intended view', () => {
        expect(resolveWebSessionRoute({ ...session, vrchatStatus: 'authenticated' })).toBe('/social/friend-list');
    });

    it('keeps the destination when confirmed authentication requires sign-in', () => {
        expect(
            resolveWebSessionRoute({
                ...session,
                browseAllowed: false,
                route: { name: 'search', fullPath: '/search?type=world', query: {} }
            })
        ).toEqual({ name: 'login', query: { redirect: '/search?type=world' } });
    });
});
