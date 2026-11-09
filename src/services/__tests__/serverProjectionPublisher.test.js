const mocks = vi.hoisted(() => ({
    executeTransaction: vi.fn(),
    execute: vi.fn(),
    watchState: {
        isLoggedIn: true,
        isFriendsLoaded: true,
        isFavoritesLoaded: true
    },
    snapshotQueue: [],
    intervalCallback: null,
    clearInterval: vi.fn(),
    dbVars: {
        userPrefix: 'usrme'
    },
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

vi.mock('../sqlite', () => ({
    default: {
        executeTransaction: mocks.executeTransaction,
        execute: mocks.execute
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
    wsState: mocks.wsState
}));

vi.mock('../serverProjection', () => ({
    buildServerProjectionSnapshot: vi.fn(() => mocks.snapshotQueue.shift())
}));

vi.mock('../database', () => ({
    dbVars: mocks.dbVars
}));

vi.mock('worker-timers', () => ({
    setInterval: vi.fn((callback) => {
        mocks.intervalCallback = callback;
        return 1;
    }),
    clearInterval: mocks.clearInterval
}));

let startServerProjectionPublisher;

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

function notificationInvalidationOperation(operations) {
    return operations.find(
        (operation) =>
            operation.args?.['@resource'] === 'notifications' &&
            operation.sql.includes('vrcx_web_notification_projection_state')
    );
}

function notificationRevisionStateOperation(operations) {
    return operations.find((operation) => operation.sql.includes('INSERT INTO vrcx_web_notification_projection_state'));
}

describe('serverProjectionPublisher', () => {
    beforeEach(async () => {
        vi.resetModules();
        mocks.executeTransaction.mockReset();
        mocks.executeTransaction.mockResolvedValue(0);
        mocks.execute.mockReset();
        mocks.execute.mockImplementation(async (callback, sql) => {
            for (const [, tableName] of sql.matchAll(/'([^']+_notifications(?:_v2)?)'/g)) {
                callback([tableName]);
            }
        });
        mocks.snapshotQueue.length = 0;
        mocks.intervalCallback = null;
        mocks.clearInterval.mockClear();
        mocks.watchState.isLoggedIn = true;
        mocks.watchState.isFriendsLoaded = true;
        mocks.watchState.isFavoritesLoaded = true;
        mocks.dbVars.userPrefix = 'usrme';
        Object.assign(mocks.wsState, {
            connected: true,
            connecting: false,
            connectedAt: '2026-01-01T00:00:00.000Z',
            lastEventAt: null,
            lastAttemptAt: '2026-01-01T00:00:00.000Z',
            nextRetryAt: null,
            errorCode: null
        });
        ({ startServerProjectionPublisher } = await import('../serverProjectionPublisher'));
    });

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
        const triggerOperations = mocks.executeTransaction.mock.calls[1][0];
        expect(triggerOperations.filter((operation) => operation.sql.startsWith('CREATE TRIGGER'))).toHaveLength(6);
        const authenticatedOperations = mocks.executeTransaction.mock.calls[2][0];
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
        await vi.waitFor(() => expect(mocks.executeTransaction).toHaveBeenCalledTimes(4));
        const loggedOutOperations = mocks.executeTransaction.mock.calls[3][0];
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
        await vi.waitFor(() => expect(mocks.executeTransaction).toHaveBeenCalledTimes(5));
        const reauthenticatedOperations = mocks.executeTransaction.mock.calls[4][0];
        expect(containsDomainSnapshotWrite(reauthenticatedOperations)).toBe(false);
        expect(reauthenticatedOperations.some((operation) => operation.args?.['@resource'] === 'snapshot')).toBe(false);

        stop();
        expect(mocks.clearInterval).toHaveBeenCalledWith(1);
    });

    it('emits one notifications invalidation from a committed revision without rewriting snapshots', async () => {
        mocks.dbVars.userPrefix = 'usrnotifications';
        const snapshot = {
            currentUserId: 'usr_notifications',
            users: [
                {
                    userId: 'usr_notifications',
                    displayName: 'Notifications User',
                    state: 'online',
                    isFriend: false,
                    updatedAt: '2026-01-01T00:00:00.000Z',
                    payload: { id: 'usr_notifications', displayName: 'Notifications User' }
                }
            ],
            entities: [],
            favorites: [],
            favoriteGroups: [],
            favoriteFingerprint: 'usr_notifications:empty-favorites',
            fingerprint: 'notification-only-domain'
        };
        mocks.snapshotQueue.push(snapshot, snapshot, snapshot);
        const revisionByPrefix = new Map();
        const observedRevisionByPrefix = new Map();
        const emittedNotificationEvents = [];
        mocks.executeTransaction.mockImplementation(async (operations) => {
            for (const operation of operations) {
                const userPrefix = operation.args?.['@user_prefix'];
                if (operation.sql.includes('INSERT OR IGNORE INTO vrcx_web_notification_revision')) {
                    revisionByPrefix.set(userPrefix, revisionByPrefix.get(userPrefix) ?? 0);
                }
                if (
                    operation.args?.['@resource'] === 'notifications' &&
                    operation.sql.includes('vrcx_web_notification_projection_state')
                ) {
                    const revision = revisionByPrefix.get(userPrefix);
                    if (
                        observedRevisionByPrefix.has(userPrefix) &&
                        observedRevisionByPrefix.get(userPrefix) !== revision
                    ) {
                        emittedNotificationEvents.push(operation.args);
                    }
                }
                if (operation.sql.includes('INSERT INTO vrcx_web_notification_projection_state')) {
                    observedRevisionByPrefix.set(userPrefix, revisionByPrefix.get(userPrefix));
                }
            }
            return 0;
        });
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
        const triggerOperations = mocks.executeTransaction.mock.calls[1][0];
        expect(triggerOperations.filter((operation) => operation.sql.startsWith('CREATE TRIGGER'))).toHaveLength(6);
        expect(
            triggerOperations.every(
                (operation) =>
                    !operation.sql.startsWith('CREATE TRIGGER') ||
                    /AFTER (?:INSERT|UPDATE|DELETE) ON usrnotifications_notifications(?:_v2)?/.test(operation.sql)
            )
        ).toBe(true);

        const initialOperations = mocks.executeTransaction.mock.calls[2][0];
        expect(notificationInvalidationOperation(initialOperations)).toBeUndefined();
        expect(notificationRevisionStateOperation(initialOperations).args).toEqual({
            '@user_prefix': 'usrnotifications'
        });
        expect(emittedNotificationEvents).toEqual([]);

        // This models a successful original-table write, whose trigger has already
        // committed the new revision before the publisher's next transaction.
        revisionByPrefix.set('usrnotifications', 1);
        mocks.wsState.lastEventAt = '2026-01-01T00:00:10.000Z';
        mocks.intervalCallback();
        await vi.waitFor(() => expect(mocks.executeTransaction).toHaveBeenCalledTimes(4));
        const notificationOnlyOperations = mocks.executeTransaction.mock.calls[3][0];
        const notificationInvalidation = notificationInvalidationOperation(notificationOnlyOperations);
        expect(containsDomainSnapshotWrite(notificationOnlyOperations)).toBe(false);
        expect(containsFavoriteSnapshotWrite(notificationOnlyOperations)).toBe(false);
        expect(notificationOnlyOperations.some((operation) => operation.args?.['@resource'] === 'snapshot')).toBe(
            false
        );
        expect(notificationOnlyOperations.some((operation) => operation.args?.['@resource'] === 'pipeline')).toBe(true);
        expect(notificationInvalidation.sql).toContain('LEFT JOIN vrcx_web_notification_projection_state');
        expect(notificationInvalidation.sql).toContain('COALESCE(state.observed_revision, -1) <> revision.revision');
        expect(notificationOnlyOperations.indexOf(notificationInvalidation)).toBeLessThan(
            notificationOnlyOperations.indexOf(notificationRevisionStateOperation(notificationOnlyOperations))
        );
        expect(emittedNotificationEvents).toHaveLength(1);
        expect(emittedNotificationEvents[0]).toMatchObject({
            '@type': 'data',
            '@resource': 'notifications',
            '@payload_json': '{}'
        });

        mocks.intervalCallback();
        await vi.waitFor(() => expect(mocks.executeTransaction).toHaveBeenCalledTimes(5));
        expect(emittedNotificationEvents).toHaveLength(1);

        stop();
    });

    it('continues publishing the domain projection when the source database has no notification tables', async () => {
        mocks.execute.mockImplementation(async () => {});
        const snapshot = {
            currentUserId: 'usr_missing_notifications',
            users: [],
            entities: [],
            favorites: [],
            favoriteGroups: [],
            favoriteFingerprint: 'usr_missing_notifications:empty-favorites',
            fingerprint: 'missing-notification-tables'
        };
        mocks.dbVars.userPrefix = 'usrmissingnotifications';
        mocks.snapshotQueue.push(snapshot);
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
        expect(mocks.executeTransaction).toHaveBeenCalledTimes(2);
        const publishOperations = mocks.executeTransaction.mock.calls[1][0];
        expect(containsDomainSnapshotWrite(publishOperations)).toBe(true);
        expect(notificationInvalidationOperation(publishOperations)).toBeUndefined();
        stop();
    });
});
