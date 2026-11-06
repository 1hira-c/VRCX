const DEFAULT_READY_TIMEOUT_MS = 120_000;

/**
 * Track the hidden renderer's document lifetime without treating the original
 * VRCX router's same-document hash transitions as a loss of readiness.
 * Failure reasons are fixed codes; renderer event details never leave here.
 */
function attachServerRendererLifecycle(
    window,
    { markUnavailable, onFailure, readyTimeoutMs = DEFAULT_READY_TIMEOUT_MS }
) {
    if (!Number.isFinite(readyTimeoutMs) || readyTimeoutMs <= 0) {
        throw new RangeError('Renderer readiness timeout must be positive.');
    }

    const webContents = window.webContents;
    let readyTimer = null;
    let disposed = false;
    let failed = false;

    function clearReadyTimeout() {
        if (readyTimer !== null) {
            clearTimeout(readyTimer);
            readyTimer = null;
        }
    }

    function fail(reason) {
        if (disposed || failed) return;
        failed = true;
        clearReadyTimeout();
        markUnavailable();
        onFailure(reason);
    }

    function armReadyTimeout() {
        clearReadyTimeout();
        readyTimer = setTimeout(() => fail('renderer_ready_timeout'), readyTimeoutMs);
        readyTimer.unref?.();
    }

    function onNavigation(_event, _url, isInPlace, isMainFrame) {
        if (disposed || failed || isInPlace !== false || isMainFrame !== true) return;
        markUnavailable();
        armReadyTimeout();
    }

    function onRendererGone() {
        fail('renderer_process_gone');
    }

    function onUnresponsive() {
        fail('renderer_unresponsive');
    }

    function onDestroyed() {
        if (disposed) return;
        markUnavailable();
        dispose();
    }

    function markReady() {
        if (disposed || failed) return false;
        clearReadyTimeout();
        return true;
    }

    function dispose() {
        if (disposed) return;
        disposed = true;
        clearReadyTimeout();
        webContents.removeListener('did-start-navigation', onNavigation);
        webContents.removeListener('render-process-gone', onRendererGone);
        webContents.removeListener('destroyed', onDestroyed);
        window.removeListener('unresponsive', onUnresponsive);
        window.removeListener('closed', onDestroyed);
    }

    webContents.on('did-start-navigation', onNavigation);
    webContents.on('render-process-gone', onRendererGone);
    webContents.on('destroyed', onDestroyed);
    window.on('unresponsive', onUnresponsive);
    window.on('closed', onDestroyed);
    armReadyTimeout();

    return { markReady, dispose };
}

module.exports = { attachServerRendererLifecycle };
