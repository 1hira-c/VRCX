const DEFAULT_UNRESPONSIVE_GRACE_MS = 30_000;

/**
 * The hidden renderer owns startup migrations as well as its original router.
 * Invalidate its control handlers only after a new document commits. Slow
 * initialization is not evidence of a crash and has no restart deadline.
 */
function attachServerRendererLifecycle(
    window,
    { markUnavailable, onFailure, unresponsiveGraceMs = DEFAULT_UNRESPONSIVE_GRACE_MS }
) {
    if (!Number.isFinite(unresponsiveGraceMs) || unresponsiveGraceMs <= 0) {
        throw new RangeError('Renderer unresponsive grace period must be positive.');
    }

    const webContents = window.webContents;
    let unresponsiveTimer = null;
    let ready = false;
    let disposed = false;
    let failed = false;

    function clearUnresponsiveTimeout() {
        if (unresponsiveTimer !== null) {
            clearTimeout(unresponsiveTimer);
            unresponsiveTimer = null;
        }
    }

    function fail(reason) {
        if (disposed || failed) return;
        failed = true;
        ready = false;
        clearUnresponsiveTimeout();
        markUnavailable();
        onFailure(reason);
    }

    function onDocumentCommitted() {
        if (disposed || failed) return;
        ready = false;
        clearUnresponsiveTimeout();
        markUnavailable();
    }

    function onRendererGone() {
        fail('renderer_process_gone');
    }

    function onUnresponsive() {
        // Initial migrations/VACUUM/optimize can block the renderer. Never kill
        // that writer merely because its ready handshake has not arrived yet.
        if (disposed || failed || !ready || unresponsiveTimer !== null) return;
        unresponsiveTimer = setTimeout(() => fail('renderer_unresponsive'), unresponsiveGraceMs);
        unresponsiveTimer.unref?.();
    }

    function onResponsive() {
        clearUnresponsiveTimeout();
    }

    function onDestroyed() {
        if (disposed) return;
        markUnavailable();
        dispose();
    }

    function markReady() {
        if (disposed || failed) return false;
        ready = true;
        clearUnresponsiveTimeout();
        return true;
    }

    function dispose() {
        if (disposed) return;
        disposed = true;
        clearUnresponsiveTimeout();
        webContents.removeListener('did-navigate', onDocumentCommitted);
        webContents.removeListener('render-process-gone', onRendererGone);
        webContents.removeListener('destroyed', onDestroyed);
        window.removeListener('unresponsive', onUnresponsive);
        window.removeListener('responsive', onResponsive);
        window.removeListener('closed', onDestroyed);
    }

    // Main-frame-only, committed navigation; same-document/hash changes do
    // not emit this event. No deprecated navigation arguments are read.
    webContents.on('did-navigate', onDocumentCommitted);
    webContents.on('render-process-gone', onRendererGone);
    webContents.on('destroyed', onDestroyed);
    window.on('unresponsive', onUnresponsive);
    window.on('responsive', onResponsive);
    window.on('closed', onDestroyed);

    return { markReady, dispose };
}

module.exports = { attachServerRendererLifecycle };
