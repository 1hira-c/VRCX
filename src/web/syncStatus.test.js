import { connectionStatusPresentation, syncStatusPresentation } from './syncStatus';

describe('Web sync status presentation', () => {
    it('shows worker reconnection instead of treating an unavailable renderer as expired authentication', () => {
        expect(
            syncStatusPresentation(
                {
                    authenticationRequired: true,
                    sync: { state: 'requires_authentication', stale: true, lastSuccessAt: '2026-11-06T04:30:00Z' }
                },
                'unavailable'
            )
        ).toMatchObject({
            state: 'worker_unavailable',
            lastSuccessAt: '2026-11-06T04:30:00Z',
            stale: true,
            labelKey: 'web.sync.state.worker_unavailable',
            tone: 'warning'
        });
    });

    it('shows healthy data as synced with its actual last successful sync time', () => {
        const lastSuccessAt = '2026-11-06T04:30:00Z';
        expect(syncStatusPresentation({ sync: { state: 'healthy', stale: false, lastSuccessAt } })).toMatchObject({
            state: 'healthy',
            stale: false,
            lastSuccessAt,
            labelKey: 'web.sync.state.healthy',
            tone: 'success'
        });
    });

    it('keeps the recorded time and explicitly warns when healthy-looking data is stale', () => {
        const lastSuccessAt = '2026-11-05T12:00:00Z';
        expect(syncStatusPresentation({ sync: { state: 'healthy', stale: true, lastSuccessAt } })).toMatchObject({
            state: 'stale',
            stale: true,
            lastSuccessAt,
            labelKey: 'web.sync.state.stale',
            tone: 'warning'
        });
    });

    it('prioritizes reauthentication while retaining stale data and the prior sync time', () => {
        expect(
            syncStatusPresentation({
                authenticationRequired: true,
                sync: { state: 'degraded', stale: true, lastSuccessAt: '2026-11-05T12:00:00Z' }
            })
        ).toMatchObject({
            state: 'requires_authentication',
            stale: true,
            lastSuccessAt: '2026-11-05T12:00:00Z',
            tone: 'error'
        });
    });

    it('does not invent a last sync time or connection state from incomplete data', () => {
        expect(syncStatusPresentation({ sync: { lastSuccessAt: 'invalid-date' } })).toMatchObject({
            state: 'unknown',
            lastSuccessAt: null,
            tone: 'neutral'
        });
        expect(connectionStatusPresentation(undefined)).toMatchObject({ state: 'unknown', tone: 'neutral' });
        expect(connectionStatusPresentation('unexpected_future_state')).toMatchObject({ state: 'unknown' });
    });

    it('distinguishes a reconnecting update stream from an open or closed connection', () => {
        expect(connectionStatusPresentation('reconnecting')).toMatchObject({ state: 'reconnecting', tone: 'warning' });
        expect(connectionStatusPresentation('connected')).toMatchObject({ state: 'connected', tone: 'success' });
        expect(connectionStatusPresentation('closed')).toMatchObject({ state: 'closed', tone: 'neutral' });
    });
});
