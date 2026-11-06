const SYNC_STATES = new Set([
    'healthy',
    'starting',
    'authenticating',
    'syncing',
    'connecting',
    'rate_limited',
    'degraded',
    'error',
    'requires_authentication',
    'not_configured'
]);
const CONNECTION_STATES = new Set([
    'idle',
    'connecting',
    'connected',
    'reconnecting',
    'disconnected',
    'closed',
    'error'
]);

/** Build the browser status indicator from reported state, without inferring connectivity. */
export function syncStatusPresentation(status, workerConnection = 'available') {
    const sync = status?.sync;
    const state =
        workerConnection === 'unavailable'
            ? 'worker_unavailable'
            : status?.authenticationRequired
              ? 'requires_authentication'
              : sync?.stale
                ? 'stale'
                : SYNC_STATES.has(sync?.state)
                  ? sync.state
                  : 'unknown';
    const lastSuccessAt =
        typeof sync?.lastSuccessAt === 'string' && Number.isFinite(Date.parse(sync.lastSuccessAt))
            ? sync.lastSuccessAt
            : null;
    return {
        state,
        stale: sync?.stale === true,
        lastSuccessAt,
        labelKey: `web.sync.state.${state}`,
        compactLabelKey: `web.sync.compact.${state}`,
        tone:
            state === 'healthy'
                ? 'success'
                : ['requires_authentication', 'error'].includes(state)
                  ? 'error'
                  : state === 'unknown' || state === 'not_configured'
                    ? 'neutral'
                    : 'warning'
    };
}

/** Report unknown until the server or EventSource has provided an explicit state. */
export function connectionStatusPresentation(value) {
    const state = CONNECTION_STATES.has(value) ? value : 'unknown';
    return {
        state,
        labelKey: `web.sync.connection.${state}`,
        tone:
            state === 'connected'
                ? 'success'
                : state === 'error'
                  ? 'error'
                  : state === 'connecting' || state === 'reconnecting'
                    ? 'warning'
                    : 'neutral'
    };
}
