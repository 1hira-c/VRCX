import { reactive } from 'vue';

export const syncTelemetry = reactive({
    activeAttempts: 0,
    state: 'starting',
    lastSuccessAt: null,
    lastAttemptAt: null,
    nextRetryAt: null,
    errorCode: null
});

function getSyncErrorCode(error) {
    const status = Number(error?.status);
    if (Number.isFinite(status) && status > 0) {
        return `http_${status}`;
    }
    if (typeof error?.name === 'string' && error.name) {
        return error.name.toLowerCase().replaceAll(/[^a-z0-9]+/g, '_');
    }
    return 'sync_failed';
}

export function recordSyncAttempt() {
    syncTelemetry.activeAttempts++;
    syncTelemetry.state = 'syncing';
    syncTelemetry.lastAttemptAt = new Date().toJSON();
    syncTelemetry.nextRetryAt = null;
    syncTelemetry.errorCode = null;
}

export function recordSyncSuccess() {
    syncTelemetry.activeAttempts = Math.max(0, syncTelemetry.activeAttempts - 1);
    syncTelemetry.lastSuccessAt = new Date().toJSON();
    syncTelemetry.errorCode = null;
    if (syncTelemetry.activeAttempts === 0) {
        syncTelemetry.state = 'healthy';
    }
}

export function recordSyncFailure(error) {
    syncTelemetry.activeAttempts = Math.max(0, syncTelemetry.activeAttempts - 1);
    syncTelemetry.state = 'error';
    syncTelemetry.errorCode = getSyncErrorCode(error);
    syncTelemetry.nextRetryAt = null;
}

export function recordAuthenticationRequired() {
    syncTelemetry.activeAttempts = 0;
    syncTelemetry.state = 'requires_authentication';
    syncTelemetry.nextRetryAt = null;
}
