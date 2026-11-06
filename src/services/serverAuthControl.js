import { authRequest } from '../api';
import { getCurrentUser } from '../coordinators/userCoordinator';
import { AppDebug } from './appConfig';
import { runtimeCapabilities } from './runtimeCapabilities';
import { recordAuthenticationRequired } from './syncTelemetry';
import { watchState } from './watchState';
import { dispatchServerReadControlRequest } from './serverReadControl';

import webApiService from './webapi';

/** @typedef {'totp' | 'otp' | 'emailOtp'} ServerTwoFactorKind */
/**
 * @typedef {object} ServerAuthResponse
 * @property {boolean} ok
 * @property {'requires_authentication' | 'authenticating' | 'two_factor_required' | 'authenticated'} status
 * @property {string | null} currentUserId
 * @property {{ kinds: ServerTwoFactorKind[] } | null} challenge
 * @property {string | null} errorCode
 */

const VALID_TWO_FACTOR_KINDS = new Set(['totp', 'otp', 'emailOtp']);
let originalLoginCompletionDeadline = 0;

function forceVrchatEndpoints() {
    AppDebug.endpointDomain = AppDebug.endpointDomainVrchat;
    AppDebug.websocketDomain = AppDebug.websocketDomainVrchat;
}

function clearTransientLogin(authStore) {
    authStore.loginForm.username = '';
    authStore.loginForm.password = '';
}

/**
 * @param {any} authStore
 * @returns {{ kinds: ServerTwoFactorKind[] } | null}
 */
function getChallenge(authStore) {
    const kinds = authStore.serverAuthChallenge?.kinds;
    if (!Array.isArray(kinds)) {
        return null;
    }
    const safeKinds = /** @type {ServerTwoFactorKind[]} */ (kinds.filter((kind) => VALID_TWO_FACTOR_KINDS.has(kind)));
    return safeKinds.length ? { kinds: safeKinds } : null;
}

/**
 * @param {any} stores
 * @param {boolean} [ok]
 * @param {string | null} [errorCode]
 * @returns {ServerAuthResponse}
 */
function createResponse(stores, ok = true, errorCode = null) {
    const challenge = getChallenge(stores.auth);
    /** @type {ServerAuthResponse['status']} */
    let status = 'requires_authentication';
    /** @type {string | null} */
    let currentUserId = null;
    if (challenge) {
        status = 'two_factor_required';
    } else if (stores.auth.loginForm.loading || stores.auth.attemptingAutoLogin) {
        status = 'authenticating';
    } else if (watchState.isLoggedIn && stores.user.currentUser?.id) {
        originalLoginCompletionDeadline = 0;
        status = 'authenticated';
        currentUserId = stores.user.currentUser.id;
    } else if (originalLoginCompletionDeadline > Date.now() && stores.user.currentUser?.id) {
        status = 'authenticating';
    }
    return {
        ok,
        status,
        currentUserId,
        challenge,
        errorCode
    };
}

function getAuthErrorCode(error) {
    const status = Number(error?.status);
    if (Number.isFinite(status) && status > 0) {
        return 'http_' + status;
    }
    if (error?.name === 'AbortError') {
        return 'request_aborted';
    }
    if (error?.name === 'TypeError') {
        return 'network_error';
    }
    return 'auth_failed';
}

function markOriginalLoginCompleting(stores, failure) {
    if (failure === null && !getChallenge(stores.auth) && stores.user.currentUser?.id) {
        originalLoginCompletionDeadline = Date.now() + 30_000;
    }
}

async function login(stores, payload) {
    const { auth } = stores;
    if (auth.loginForm.loading || auth.attemptingAutoLogin) {
        return createResponse(stores, false, 'operation_in_progress');
    }
    if (watchState.isLoggedIn) {
        await auth.handleLogoutEvent();
    }

    auth.serverAuthChallenge = null;
    auth.credentialsToSave = null;
    forceVrchatEndpoints();
    auth.loginForm.username = payload.username;
    auth.loginForm.password = payload.password;
    auth.loginForm.endpoint = '';
    auth.loginForm.websocket = '';
    // This is deliberately false: server login persists only the VRChat cookie
    // session through the original watcher, never the supplied username/password.
    auth.loginForm.saveCredentials = false;

    let failure = null;
    try {
        await auth.login();
    } catch (error) {
        failure = getAuthErrorCode(error);
    } finally {
        if (!getChallenge(auth)) {
            clearTransientLogin(auth);
        }
    }
    markOriginalLoginCompleting(stores, failure);
    return createResponse(stores, failure === null, failure);
}

async function verifyTwoFactor(stores, payload) {
    const { auth } = stores;
    const challenge = getChallenge(auth);
    if (!challenge || !challenge.kinds.includes(payload.kind)) {
        return createResponse(stores, false, 'two_factor_kind_not_required');
    }
    if (auth.loginForm.loading) {
        return createResponse(stores, false, 'operation_in_progress');
    }

    auth.loginForm.loading = true;
    let failure = null;
    try {
        let result;
        if (payload.kind === 'totp') {
            result = await authRequest.verifyTOTP({ code: payload.code.trim() });
        } else if (payload.kind === 'otp') {
            const code = payload.code.replaceAll('-', '').trim();
            result = await authRequest.verifyOTP({ code: code.slice(0, 4) + '-' + code.slice(4) });
        } else {
            result = await authRequest.verifyEmailOTP({ code: payload.code.trim() });
        }
        if (result?.json?.verified === false) {
            failure = 'two_factor_rejected';
        } else {
            await getCurrentUser();
            if (getChallenge(auth)) {
                failure = 'two_factor_rejected';
            }
        }
    } catch (error) {
        failure = getAuthErrorCode(error);
    } finally {
        auth.loginForm.loading = false;
        if (!getChallenge(auth)) {
            clearTransientLogin(auth);
        }
    }
    markOriginalLoginCompleting(stores, failure);
    return createResponse(stores, failure === null, failure);
}

async function resendEmail(stores) {
    const { auth } = stores;
    const challenge = getChallenge(auth);
    if (!challenge?.kinds.includes('emailOtp')) {
        return createResponse(stores, false, 'email_otp_not_required');
    }
    if (!auth.loginForm.username || !auth.loginForm.password) {
        return createResponse(stores, false, 'login_context_unavailable');
    }
    if (auth.loginForm.loading) {
        return createResponse(stores, false, 'operation_in_progress');
    }

    forceVrchatEndpoints();
    auth.credentialsToSave = null;
    auth.loginForm.endpoint = '';
    auth.loginForm.websocket = '';
    auth.loginForm.saveCredentials = false;
    const previousChallenge = auth.serverAuthChallenge;
    auth.serverAuthChallenge = null;

    let failure = null;
    try {
        // Re-enter the original authLogin path. VRChat reissues the email
        // challenge as part of that request; no separate endpoint is invented.
        await auth.login();
    } catch (error) {
        failure = getAuthErrorCode(error);
        if (!getChallenge(auth)) {
            auth.serverAuthChallenge = previousChallenge;
        }
    } finally {
        if (!getChallenge(auth)) {
            clearTransientLogin(auth);
        }
    }
    markOriginalLoginCompleting(stores, failure);
    return createResponse(stores, failure === null, failure);
}

async function logout(stores) {
    originalLoginCompletionDeadline = 0;
    stores.auth.serverAuthChallenge = null;
    if (watchState.isLoggedIn) {
        await stores.auth.handleLogoutEvent();
    } else {
        await webApiService.clearCookies();
    }
    clearTransientLogin(stores.auth);
    recordAuthenticationRequired();
    return createResponse(stores);
}

export async function dispatchControlRequest(stores, request) {
    switch (request.operation) {
        case 'status':
            return createResponse(stores);
        case 'login':
            return login(stores, request.payload);
        case 'two-factor':
            return verifyTwoFactor(stores, request.payload);
        case 'email-resend':
            return resendEmail(stores);
        case 'logout':
            return logout(stores);
        case 'search':
        case 'entity':
        case 'entity-resource':
        case 'database-read':
            return dispatchServerReadControlRequest(stores, request);
        default:
            return createResponse(stores, false, 'invalid_operation');
    }
}

/**
 * Connects the loopback auth control to the original VRCX renderer auth flow.
 * The preload exposes only this typed request/response pair; there is no generic
 * WebApi, URL, header, or Electron IPC bridge.
 */
export function startServerAuthControl(stores) {
    if (!runtimeCapabilities.serverMode || !window.electron?.onServerAuthControlRequest) {
        return () => {};
    }

    let mutationQueue = Promise.resolve();
    const unsubscribe = window.electron.onServerAuthControlRequest((request) => {
        const run = () => dispatchControlRequest(stores, request);
        /** @type {Promise<any>} */
        let responsePromise;
        if (
            request.operation === 'status' ||
            request.operation === 'search' ||
            request.operation === 'entity' ||
            request.operation === 'entity-resource' ||
            request.operation === 'database-read'
        ) {
            responsePromise = run();
        } else {
            responsePromise = mutationQueue.then(run, run);
            mutationQueue = responsePromise.then(
                () => undefined,
                () => undefined
            );
        }

        void responsePromise
            .catch(() => createResponse(stores, false, 'internal_error'))
            .then((response) => {
                window.electron.respondServerAuthControl(request.requestId, response);
            });
    });
    window.electron.markServerAuthControlReady();
    return unsubscribe;
}
