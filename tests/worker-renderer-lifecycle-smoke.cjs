/** Real Electron regression for hidden-worker file/hash navigation, with no account or network access. */
const { app, BrowserWindow } = require('electron');
const assert = require('node:assert/strict');
const path = require('node:path');
const { attachServerRendererLifecycle } = require('/app/worker/src-electron/serverRendererLifecycle.js');

app.disableHardwareAcceleration();
app.on('window-all-closed', () => {});

let stage = 'starting Electron';
let window;
let lifecycle;
const overallTimeout = setTimeout(() => {
    console.error(`[worker-renderer-smoke] Timed out during ${stage}`);
    app.exit(1);
}, 45_000);

function bounded(promise, label) {
    let timer;
    return Promise.race([
        promise,
        new Promise((_, reject) => {
            timer = setTimeout(() => reject(new Error(`Timed out: ${label}`)), 10_000);
        })
    ]).finally(() => clearTimeout(timer));
}

function waitForEvent(emitter, event, predicate = () => true) {
    return new Promise((resolve) => {
        const listener = (...args) => {
            if (!predicate(...args)) return;
            emitter.removeListener(event, listener);
            resolve(args);
        };
        emitter.on(event, listener);
    });
}

app.whenReady()
    .then(async () => {
        let ready = false;
        let loadingEvents = 0;
        const failures = [];
        window = new BrowserWindow({
            show: false,
            webPreferences: {
                nodeIntegration: false,
                contextIsolation: true,
                sandbox: true,
                backgroundThrottling: false
            }
        });
        lifecycle = attachServerRendererLifecycle(window, {
            markUnavailable: () => {
                ready = false;
            },
            onFailure: (code) => failures.push(code)
        });
        const webContents = window.webContents;
        webContents.on('did-start-loading', () => loadingEvents++);

        stage = 'loading fixture file';
        await bounded(window.loadFile(path.join(__dirname, 'fixtures/worker-renderer.html')), stage);
        assert.equal(lifecycle.markReady(), true);
        ready = true;

        stage = 'same-document file hash navigation';
        const loadingEventsBeforeHash = loadingEvents;
        const hashNavigation = waitForEvent(webContents, 'did-navigate-in-page');
        const hashStoppedLoading = waitForEvent(webContents, 'did-stop-loading');
        await bounded(webContents.executeJavaScript('location.hash = "/feed"'), stage);
        await bounded(Promise.all([hashNavigation, hashStoppedLoading]), stage);
        // file:// hash routing starts Electron's loading spinner even though
        // the VRCX document and its registered IPC handlers remain alive.
        assert.ok(loadingEvents > loadingEventsBeforeHash, 'Fixture did not reproduce the file hash loading event');
        assert.equal(ready, true, 'Hash navigation invalidated the original renderer handshake');
        assert.deepEqual(failures, []);

        stage = 'full document reload';
        const fullNavigation = waitForEvent(
            webContents,
            'did-start-navigation',
            (_event, _url, isInPlace, isMainFrame) => !isInPlace && isMainFrame
        );
        const reloaded = waitForEvent(webContents, 'did-finish-load');
        webContents.reload();
        await bounded(fullNavigation, stage);
        assert.equal(ready, false, 'A new document reused the previous renderer handshake');
        await bounded(reloaded, stage);
        assert.equal(lifecycle.markReady(), true);
        ready = true;
        assert.deepEqual(failures, []);

        stage = 'renderer crash';
        const rendererGone = waitForEvent(webContents, 'render-process-gone');
        webContents.forcefullyCrashRenderer();
        await bounded(rendererGone, stage);
        assert.equal(ready, false);
        assert.deepEqual(failures, ['renderer_process_gone']);
        window.emit('unresponsive');
        assert.deepEqual(failures, ['renderer_process_gone'], 'A single failure requested multiple restarts');
        assert.equal(lifecycle.markReady(), false);

        lifecycle.dispose();
        window.destroy();
        clearTimeout(overallTimeout);
        console.log(
            JSON.stringify({
                ok: true,
                checks: ['file-hash-preserves-readiness', 'full-reload-requires-handshake', 'renderer-crash-detected'],
                hashLoadingEventObserved: loadingEvents > loadingEventsBeforeHash,
                failureCode: failures[0]
            })
        );
        app.exit(0);
    })
    .catch((error) => {
        lifecycle?.dispose();
        clearTimeout(overallTimeout);
        console.error(`[worker-renderer-smoke] ${stage}: ${error.stack || error}`);
        app.exit(1);
    });
