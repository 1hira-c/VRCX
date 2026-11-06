const mocks = vi.hoisted(() => ({
    executeTransaction: vi.fn(),
    watchState: {
        isLoggedIn: true,
        isFriendsLoaded: true,
        isFavoritesLoaded: true
    },
    snapshotQueue: [],
    intervalCallback: null,
    clearInterval: vi.fn()
}));

vi.mock('../sqlite', () => ({
    default: {
        executeTransaction: mocks.executeTransaction
    }
}));

vi.mock('../runtimeCapabilities', () => ({
    runtimeCapabilities: {
        serverMode: true
    }
}));

vi.mock('../watchState', () => ({
    watchState: mocks.watchState
}));

vi.mock('../syncTelemetry', () => ({
    syncTelemetry: {
        state: 'healthy',
        lastSuccessAt: '2026-01-01T00:00:00.000Z',
        lastAttemptAt: '2026-01-01T00:00:00.000Z',
        nextRetryAt: null,
        errorCode: null
    }
}));

vi.mock('../websocket', () => ({
    wsState: {
        connected: true,
        connecting: false,
        connectedAt: '2026-01-01T00:00:00.000Z',
        lastEventAt: null,
        lastAttemptAt: '2026-01-01T00:00:00.000Z',
        nextRetryAt: null,
        errorCode: null
    }
}));

vi.mock('../serverProjection', () => ({
    buildServerProjectionSnapshot: vi.fn(() => mocks.snapshotQueue.shift())
}));

vi.mock('worker-timers', () => ({
    setInterval: vi.fn((callback) => {
        mocks.intervalCallback = callback;
        return 1;
    }),
    clearInterval: mocks.clearInterval
}));

import { startServerProjectionPublisher } from '../serverProjectionPublisher';

function containsDomainSnapshotWrite(operations) {
    return operations.some((operation) =>
        /(?:DELETE FROM|INSERT INTO) vrcx_web_(?:user|entity)_snapshot/.test(operation.sql)
    );
}

function containsFavoriteSnapshotWrite(operations) {
    return operations.some((operation) =>
        /(?:DELETE FROM|INSERT INTO) vrcx_web_favorite_(?:snapshot|group_snapshot|projection_state)/.test(operation.sql)
    );
}

describe('serverProjectionPublisher', () => {
    it('keeps the last authenticated domain snapshot while logged out', async () => {
        const authenticatedSnapshot = {
            currentUserId: 'usr_me',
            users: [
                {
                    userId: 'usr_me',
                    displayName: 'Me',
                    state: 'online',
                    isFriend: false,
                    updatedAt: '2026-01-01T00:00:00.000Z',
                    payload: { id: 'usr_me', displayName: 'Me' }
                }
            ],
            entities: [],
            favorites: [],
            favoriteGroups: [],
            favoriteFingerprint: 'usr_me:empty-favorites',
            fingerprint: 'authenticated-snapshot'
        };
        const loggedOutSnapshot = {
            currentUserId: '',
            users: [],
            entities: [],
            favorites: [],
            favoriteGroups: [],
            favoriteFingerprint: ':empty-favorites',
            fingerprint: 'empty-snapshot'
        };
        mocks.snapshotQueue.push(authenticatedSnapshot, loggedOutSnapshot, authenticatedSnapshot);
        mocks.executeTransaction.mockResolvedValue(0);
        const stores = {
            auth: {
                loginForm: { loading: false },
                attemptingAutoLogin: false
            },
            friend: {
                isRefreshFriendsLoading: false
            }
        };

        const stop = await startServerProjectionPublisher(stores);
        const authenticatedOperations = mocks.executeTransaction.mock.calls[1][0];
        expect(containsDomainSnapshotWrite(authenticatedOperations)).toBe(true);
        expect(containsFavoriteSnapshotWrite(authenticatedOperations)).toBe(true);
        expect(
            authenticatedOperations.some(
                (operation) =>
                    operation.sql.includes('vrcx_web_favorite_projection_state') &&
                    operation.args?.['@current_user_id'] === 'usr_me'
            )
        ).toBe(true);
        expect(
            authenticatedOperations.some(
                (operation) => operation.args?.['@resource'] === 'favorites' && operation.args?.['@type'] === 'data'
            )
        ).toBe(true);

        mocks.watchState.isLoggedIn = false;
        mocks.intervalCallback();
        await vi.waitFor(() => expect(mocks.executeTransaction).toHaveBeenCalledTimes(3));
        const loggedOutOperations = mocks.executeTransaction.mock.calls[2][0];
        expect(containsDomainSnapshotWrite(loggedOutOperations)).toBe(false);
        expect(containsFavoriteSnapshotWrite(loggedOutOperations)).toBe(false);
        expect(loggedOutOperations.some((operation) => operation.args?.['@resource'] === 'snapshot')).toBe(false);
        expect(loggedOutOperations[0].args['@current_user_id']).toBe('');
        expect(
            loggedOutOperations.some(
                (operation) => operation.args?.['@type'] === 'status' && operation.args?.['@resource'] === 'status'
            )
        ).toBe(true);

        mocks.watchState.isLoggedIn = true;
        mocks.intervalCallback();
        await vi.waitFor(() => expect(mocks.executeTransaction).toHaveBeenCalledTimes(4));
        const reauthenticatedOperations = mocks.executeTransaction.mock.calls[3][0];
        expect(containsDomainSnapshotWrite(reauthenticatedOperations)).toBe(false);
        expect(reauthenticatedOperations.some((operation) => operation.args?.['@resource'] === 'snapshot')).toBe(false);

        stop();
        expect(mocks.clearInterval).toHaveBeenCalledWith(1);
    });
});
