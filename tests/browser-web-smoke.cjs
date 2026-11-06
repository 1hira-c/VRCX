/**
 * Real Chromium smoke for the compiled Web UI, using Electron only as a browser.
 *
 * Run with the runtime image's xvfb-run and /app/worker/electron/electron.
 * VRCX_SMOKE_URL selects an existing Web deployment. VRCX_SMOKE_OUTPUT selects
 * an artifact directory. For HTTPS, VRCX_SMOKE_CA_FILE must contain its public CA.
 * Optional VRCX_SMOKE_STATIC_ROOT serves fixture UI assets from a local Web
 * build directory instead of proxying assets from the deployment under test.
 * The first window reads the real anonymous setup/login page. The second uses
 * explicit API fixtures and never signs in, sends credentials, or changes data.
 */
const { app, BrowserWindow } = require('electron');
const assert = require('node:assert/strict');
const { X509Certificate } = require('node:crypto');
const fs = require('node:fs/promises');
const http = require('node:http');
const https = require('node:https');
const path = require('node:path');

// Windows are intentionally replaced between isolated scenarios.
app.on('window-all-closed', () => {});
app.disableHardwareAcceleration();

const target = new URL(process.env.VRCX_SMOKE_URL || 'http://vrcx-server:8080/');
const output = process.env.VRCX_SMOKE_OUTPUT || '/tmp/vrcx-web-smoke';
const now = new Date().toISOString();
const me = 'usr_00000000-0000-4000-8000-000000000001';
const friend = 'usr_00000000-0000-4000-8000-000000000002';
const world = 'wrld_00000000-0000-4000-8000-000000000003';
const location = `${world}:12345`;
const requests = new Set();
const requestCounts = new Map();
const failures = [];
const networkFailures = [];
const fixtureHttpLog = [];
const checkpoints = [];
let trustedCa;
let currentStage = 'loading harness';
function progress(stage) {
    currentStage = stage;
    console.log(`[web-smoke] ${stage}`);
}
function bounded(promise, label, timeout = 15_000) {
    let timer;
    return Promise.race([
        promise,
        new Promise((_, reject) => {
            timer = setTimeout(() => reject(new Error(`Timed out: ${label}`)), timeout);
        })
    ]).finally(() => clearTimeout(timer));
}
progress(`harness loaded (Electron ${process.versions.electron || 'unknown'})`);
const overallTimeout = setTimeout(() => {
    console.error(`[web-smoke] Overall timeout during ${currentStage}`);
    app.exit(1);
}, 120_000);
const users = [
    { id: me, displayName: 'Smoke Viewer', state: 'offline', location: 'offline', isFriend: false },
    {
        id: friend,
        displayName: 'Smoke Friend',
        state: 'online',
        location,
        status: 'active',
        statusDescription: 'Browser smoke fixture',
        bio: 'Fixture profile',
        tags: ['system_trust_known'],
        isFriend: true
    }
];
const feed = [
    {
        rowId: 1,
        created_at: now,
        type: 'GPS',
        userId: friend,
        displayName: 'Smoke Friend',
        location,
        worldName: 'Smoke World',
        previousLocation: 'offline'
    }
];
const gameLog = [{ rowId: 1, created_at: now, type: 'Location', location, worldId: world, worldName: 'Smoke World' }];
const page = (items) => ({ items, total: items.length, offset: 0, limit: 200 });

function fixture(url, state) {
    const { authenticated, stale } = state;
    const route = url.pathname.slice('/api/v1'.length);
    requests.add(route);
    requestCounts.set(route, (requestCounts.get(route) || 0) + 1);
    if (route === '/auth/status')
        return {
            bootstrapped: true,
            authenticated: true,
            device: { id: 'smoke-device', deviceName: 'Fixture browser' }
        };
    if (route === '/vrchat/auth/status')
        return authenticated
            ? { ok: true, status: 'authenticated', currentUser: { id: me } }
            : { ok: false, status: 'requires_authentication', currentUser: null };
    if (route === '/status')
        return {
            configured: authenticated,
            authenticationRequired: !authenticated,
            currentUserId: authenticated ? me : null,
            sync: {
                state: authenticated ? (stale ? 'degraded' : 'healthy') : 'requires_authentication',
                stale: stale || !authenticated,
                lastSuccessAt: authenticated ? now : null
            },
            pipeline: { state: authenticated ? 'connected' : 'disconnected', connected: authenticated },
            counts: authenticated ? { friends: 1, feed: 1, notifications: 1, gameLog: 1 } : {}
        };
    if (
        !authenticated &&
        ['/friends', '/feed', '/notifications', '/favorites', '/history/game-log', '/history/friends'].includes(route)
    )
        return page([]);
    if (route === '/friends') return page([{ ...users[1], raw: users[1] }]);
    if (route.startsWith('/users/')) {
        const user = users.find((item) => route === `/users/${item.id}`);
        if (user) return { id: user.id, type: 'user', name: user.displayName, payload: user };
    }
    if (route === `/worlds/${world}`)
        return {
            id: world,
            type: 'world',
            name: 'Smoke World',
            description: 'Browser smoke fixture world',
            payload: {
                id: world,
                name: 'Smoke World',
                authorId: me,
                authorName: 'Smoke Viewer',
                capacity: 40,
                recommendedCapacity: 20,
                releaseStatus: 'public',
                tags: [],
                instances: []
            }
        };
    if (route === '/feed')
        return page([
            { id: 1, kind: 'gps', createdAt: now, userId: friend, displayName: 'Smoke Friend', payload: feed[0] }
        ]);
    if (route === '/history/game-log') return page([{ id: 1, kind: 'location', createdAt: now, payload: gameLog[0] }]);
    if (route === '/notifications')
        return page([
            {
                id: 'smoke-invite',
                type: 'invite',
                createdAt: now,
                message: 'Join Smoke World',
                seen: false,
                payload: {
                    senderUserId: friend,
                    senderUsername: 'Smoke Friend',
                    receiverUserId: me,
                    receiverUsername: 'Smoke Viewer',
                    details: { worldId: world, worldName: 'Smoke World', location, inviteMessage: 'Join Smoke World' }
                }
            }
        ]);
    if (route === '/favorites' || route === '/history/friends' || route === '/search') return page([]);
    if (route === '/favorites/groups') return { ready: true, items: [] };
    if (route === '/analytics/summary') return {};
    if (route === '/history-native/feed') return feed;
    if (route === '/history-native/game-log') return gameLog;
    if (
        route === '/history-native/friends' ||
        route === '/history-native/users/stats' ||
        route === '/history-native/memos/users'
    )
        return [];
    if (route.startsWith('/history-native/game-log/sessions/')) return [];
    if (route === '/analytics/instance-activity/dates') return [now];
    if (route === '/analytics/instance-activity') return { currentUserData: [], detailData: [] };
    if (
        route === '/analytics/hot-worlds' ||
        route === '/analytics/mutual-graph/snapshot' ||
        route === '/analytics/mutual-graph/meta'
    )
        return [];
    if (route === '/events') return { eventStream: true };
    throw new Error(`Unspecified fixture API: ${route}`);
}

async function verifyTls() {
    if (target.protocol !== 'https:') return null;
    assert.ok(process.env.VRCX_SMOKE_CA_FILE, 'HTTPS smoke requires VRCX_SMOKE_CA_FILE');
    const ca = await fs.readFile(process.env.VRCX_SMOKE_CA_FILE);
    trustedCa = ca;
    return new Promise((resolve, reject) => {
        const req = https.get(target, { ca, rejectUnauthorized: true }, (res) => {
            const cert = res.socket.getPeerCertificate();
            assert.equal(res.socket.authorized, true);
            res.resume();
            resolve(new X509Certificate(cert.raw).fingerprint256);
        });
        req.setTimeout(10_000, () => req.destroy(new Error('HTTPS verification timed out')));
        req.on('error', reject);
    });
}

async function startFixtureServer(state) {
    const staticRoot = process.env.VRCX_SMOKE_STATIC_ROOT
        ? await fs.realpath(process.env.VRCX_SMOKE_STATIC_ROOT)
        : null;
    progress(`fixture assets: ${staticRoot ? 'local Web build' : 'deployment'}`);
    const contentTypes = {
        '.html': 'text/html; charset=utf-8',
        '.js': 'application/javascript',
        '.css': 'text/css',
        '.json': 'application/json',
        '.map': 'application/json',
        '.svg': 'image/svg+xml',
        '.png': 'image/png',
        '.webp': 'image/webp',
        '.jpg': 'image/jpeg',
        '.jpeg': 'image/jpeg',
        '.gif': 'image/gif',
        '.ico': 'image/x-icon',
        '.woff2': 'font/woff2',
        '.woff': 'font/woff',
        '.ttf': 'font/ttf',
        '.otf': 'font/otf',
        '.wasm': 'application/wasm',
        '.txt': 'text/plain',
        '.md': 'text/markdown'
    };
    async function serveStatic(url, response) {
        try {
            const pathname = decodeURIComponent(url.pathname);
            const file = path.resolve(staticRoot, pathname === '/' ? 'web.html' : `.${pathname}`);
            if (pathname.includes('\0') || !file.startsWith(`${staticRoot}${path.sep}`)) {
                response.writeHead(400).end();
                return;
            }
            const realFile = await fs.realpath(file);
            if (!realFile.startsWith(`${staticRoot}${path.sep}`)) {
                response.writeHead(400).end();
                return;
            }
            const body = await fs.readFile(realFile);
            response.writeHead(200, {
                'Content-Type': contentTypes[path.extname(realFile).toLowerCase()] || 'application/octet-stream',
                'Content-Length': body.length,
                'Cache-Control': 'no-store',
                'X-Content-Type-Options': 'nosniff'
            });
            response.end(body);
        } catch (error) {
            response.writeHead(error.code === 'ENOENT' || error.code === 'EISDIR' ? 404 : 400).end();
        }
    }
    const server = http.createServer((request, response) => {
        const entry = { at: new Date().toISOString(), method: request.method, path: request.url };
        fixtureHttpLog.push(entry);
        response.once('finish', () => {
            entry.status = response.statusCode;
            entry.finished = true;
        });
        response.once('close', () => {
            entry.closedBeforeFinish = !response.writableFinished;
        });
        response.on('error', (error) => {
            entry.responseError = error.code || error.message;
            if (!state.closing)
                console.error(`[web-smoke] fixture response error ${request.url}: ${entry.responseError}`);
        });
        request.on('aborted', () => {
            entry.aborted = true;
        });
        request.on('error', (error) => {
            entry.requestError = error.code || error.message;
            if (!state.closing)
                console.error(`[web-smoke] fixture request error ${request.url}: ${entry.requestError}`);
        });
        if (request.method !== 'GET') {
            failures.push(`Fixture UI attempted mutation: ${request.method} ${request.url}`);
            response.writeHead(405).end();
            return;
        }
        const url = new URL(request.url, target);
        if (url.origin !== target.origin) {
            failures.push('Invalid fixture proxy target');
            response.writeHead(400).end();
            return;
        }
        if (url.pathname.startsWith('/api/v1/')) {
            try {
                const payload = fixture(url, state);
                const statusFailure = url.pathname === '/api/v1/vrchat/auth/status' ? state.authStatusFailure : null;
                if (url.pathname === '/api/v1/vrchat/auth/status')
                    state.statusAttempts = (state.statusAttempts || 0) + 1;
                response.setHeader('Cache-Control', 'no-store');
                if (payload?.eventStream) {
                    response.writeHead(200, { 'Content-Type': 'text/event-stream' });
                    const sendStatus = () => response.write('event: status\ndata: {}\n\n');
                    sendStatus();
                    const timer = setInterval(sendStatus, 1000);
                    response.on('close', () => clearInterval(timer));
                    return;
                }
                const body = JSON.stringify(statusFailure?.body || payload);
                response.writeHead(statusFailure?.status || 200, {
                    'Content-Type': 'application/json',
                    'Content-Length': Buffer.byteLength(body),
                    ...(statusFailure?.retryAfter ? { 'Retry-After': statusFailure.retryAfter } : {})
                });
                response.end(body);
            } catch (error) {
                failures.push(error.message);
                response.writeHead(500, { 'Content-Type': 'application/json' });
                response.end(JSON.stringify({ error: 'unspecified_fixture' }));
            }
            return;
        }

        if (staticRoot) {
            void serveStatic(url, response);
            return;
        }

        // Only public compiled assets go to the real server. API requests and
        // browser cookies/credentials are never forwarded from fixture windows.
        const transport = target.protocol === 'https:' ? https : http;
        const upstream = transport.get(url, { ca: trustedCa, rejectUnauthorized: true }, (upstreamResponse) => {
            response.writeHead(upstreamResponse.statusCode || 502, upstreamResponse.headers);
            upstreamResponse.pipe(response);
        });
        upstream.setTimeout(10_000, () => upstream.destroy(new Error('Static asset proxy timeout')));
        upstream.on('error', (error) => {
            failures.push(`Static asset proxy: ${error.message}`);
            if (!response.headersSent) response.writeHead(502);
            response.end();
        });
    });
    // Keep connections alive for the complete short smoke. Otherwise a browser
    // request can race Node's five-second idle close while switching views.
    server.keepAliveTimeout = 60_000;
    server.headersTimeout = 65_000;
    server.on('clientError', (error, socket) => {
        fixtureHttpLog.push({ clientError: error.code || error.message, closing: Boolean(state.closing) });
        if (!state.closing) console.error(`[web-smoke] fixture client error: ${error.code || error.message}`);
        socket.destroy();
    });
    await bounded(
        new Promise((resolve, reject) => {
            server.once('error', reject);
            server.listen(0, '127.0.0.1', resolve);
        }),
        'start loopback fixture server'
    );
    return { server, url: new URL(`http://127.0.0.1:${server.address().port}/`) };
}

async function createWindow(name, fingerprint, useFixtures, authenticated = true, fixtureOverrides = {}) {
    progress(`${name}: create browser window`);
    const fixtureState = { authenticated, stale: false, ...fixtureOverrides };
    const fixtureServer = useFixtures ? await startFixtureServer(fixtureState) : null;
    const pageOrigin = fixtureServer?.url || target;
    const win = new BrowserWindow({
        width: 1440,
        height: 960,
        show: true,
        webPreferences: {
            partition: `vrcx-smoke-${name}-${Date.now()}`,
            nodeIntegration: false,
            contextIsolation: true,
            sandbox: true,
            backgroundThrottling: false
        }
    });
    // Main-process test state, never exposed as a renderer global or bridge.
    win.smokeFixtureState = fixtureState;
    win.smokeUrl = pageOrigin;
    if (fixtureServer) {
        win.on('closed', () => {
            fixtureState.closing = true;
            fixtureServer.server.close();
            fixtureServer.server.closeAllConnections();
        });
    }
    if (fingerprint && !useFixtures) {
        win.webContents.session.setCertificateVerifyProc((request, callback) => {
            try {
                // Trust only the exact leaf already verified by Node against the
                // supplied CA, with its normal hostname and validity checks.
                callback(
                    request.hostname === target.hostname &&
                        new X509Certificate(request.certificate.data).fingerprint256 === fingerprint
                        ? 0
                        : -3
                );
            } catch {
                callback(-2);
            }
        });
    }
    win.webContents.on('console-message', ({ level, message, sourceId }) => {
        const expectedStatus = fixtureState.authStatusFailure?.status;
        if (
            expectedStatus &&
            sourceId === `${pageOrigin.origin}/api/v1/vrchat/auth/status` &&
            message.startsWith(`Failed to load resource: the server responded with a status of ${expectedStatus}`)
        )
            return;
        if (level === 'error') failures.push(`${name}: ${message}`);
    });
    win.webContents.session.webRequest.onBeforeRequest((details, callback) => {
        const url = new URL(details.url);
        const external = ['http:', 'https:'].includes(url.protocol) && url.origin !== pageOrigin.origin;
        if (external) failures.push(`${name}: unexpected external browser request to ${url.origin}`);
        callback({ cancel: external });
    });
    win.webContents.session.webRequest.onErrorOccurred((details) => {
        const entry = { window: name, url: details.url, error: details.error, closing: Boolean(fixtureState.closing) };
        networkFailures.push(entry);
        console.error(`[web-smoke] browser request failed: ${JSON.stringify(entry)}`);
        if (
            !entry.closing &&
            details.error !== 'net::ERR_ABORTED' &&
            new URL(details.url).pathname.startsWith('/api/v1/')
        ) {
            failures.push(`${name}: API transport failed: ${details.error}`);
        }
    });
    win.webContents.on('render-process-gone', (_event, details) =>
        failures.push(`${name}: renderer ${details.reason}`)
    );
    win.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
    // Hidden windows can lack a renderer before their first document. Loading
    // about:blank first prevents Page.enable from waiting on an absent process.
    await bounded(win.loadURL('about:blank'), `${name}: initial blank document`);
    progress(`${name}: attach browser instrumentation`);
    win.webContents.debugger.attach('1.3');
    await bounded(win.webContents.debugger.sendCommand('Page.enable'), `${name}: Page.enable`);
    await bounded(
        win.webContents.debugger.sendCommand('Page.addScriptToEvaluateOnNewDocument', {
            source: `
        window.__vrcxSmokeErrors = [];
        addEventListener('error', event => window.__vrcxSmokeErrors.push(event.message));
        addEventListener('unhandledrejection', event => window.__vrcxSmokeErrors.push(String(event.reason?.stack || event.reason)));
    `
        }),
        `${name}: install browser error listeners`
    );
    return win;
}

async function evaluate(win, source) {
    return bounded(win.webContents.executeJavaScript(source), 'evaluate browser state');
}

async function waitFor(win, expression, label) {
    const deadline = Date.now() + 20_000;
    while (Date.now() < deadline) {
        if (await evaluate(win, expression)) return;
        await new Promise((resolve) => setTimeout(resolve, 100));
    }
    throw new Error(`Timed out: ${label}; text=${(await evaluate(win, 'document.body.innerText')).slice(0, 1000)}`);
}

async function click(win, expression) {
    const rect = await evaluate(
        win,
        `(() => {
        const element = ${expression};
        if (!element) throw new Error('Smoke click target not found');
        const rect = element.getBoundingClientRect();
        return { x: Math.round(rect.x + rect.width / 2), y: Math.round(rect.y + rect.height / 2) };
    })()`
    );
    win.webContents.sendInputEvent({ type: 'mouseMove', ...rect });
    win.webContents.sendInputEvent({ type: 'mouseDown', button: 'left', clickCount: 1, ...rect });
    win.webContents.sendInputEvent({ type: 'mouseUp', button: 'left', clickCount: 1, ...rect });
}

async function hover(win, expression) {
    const point = await evaluate(
        win,
        `(() => {
        const rect = (${expression}).getBoundingClientRect();
        return { x: Math.round(rect.x + rect.width / 2), y: Math.round(rect.y + rect.height / 2) };
    })()`
    );
    win.webContents.sendInputEvent({ type: 'mouseMove', ...point });
}

async function checkpoint(win, name, saveScreenshot = true) {
    progress(`checkpoint ${name}`);
    await new Promise((resolve) => setTimeout(resolve, 300));
    const result = await evaluate(
        win,
        `({ hash: location.hash, textLength: document.body.innerText.length,
        nativeGlobals: ['AppApi', 'WebApi', 'VRCXStorage', 'electron'].filter(name => typeof window[name] !== 'undefined'),
        errors: window.__vrcxSmokeErrors, viewport: innerWidth, documentWidth: document.documentElement.scrollWidth })`
    );
    assert.deepEqual(result.nativeGlobals, [], `${name}: native bridge reached the browser`);
    assert.deepEqual(result.errors, [], `${name}: browser exceptions`);
    assert.ok(result.textLength > 100, `${name}: blank or incomplete page`);
    if (saveScreenshot) {
        const screenshot = await win.webContents.capturePage();
        await fs.writeFile(path.join(output, `${name}.png`), screenshot.toPNG());
    }
    checkpoints.push({ name, ...result });
}

app.whenReady()
    .then(async () => {
        progress('Electron ready');
        await fs.mkdir(output, { recursive: true });
        const fingerprint = await verifyTls();
        const anonymous = await createWindow('anonymous', fingerprint, false);
        progress('anonymous: load real setup/login page');
        await bounded(anonymous.loadURL(target.href), 'load real setup/login page', 30_000);
        await waitFor(
            anonymous,
            `Boolean(document.querySelector('input[autocomplete="new-password"], input[autocomplete="current-password"]'))`,
            'real administrator setup/login form'
        );
        await checkpoint(anonymous, 'anonymous-setup');
        anonymous.destroy();

        const unauthenticated = await createWindow('vrchat-login', fingerprint, true, false);
        progress('vrchat-login: load fixture page');
        await bounded(
            unauthenticated.loadURL(new URL('#/login', unauthenticated.smokeUrl).href),
            'load VRChat login fixture page',
            30_000
        );
        await waitFor(
            unauthenticated,
            `Boolean(document.querySelector('.web-import-actions'))`,
            'snapshot setup before VRChat sign-in'
        );
        await click(
            unauthenticated,
            `Array.from(document.querySelectorAll('.web-import-actions button')).find(button => button.innerText.includes('今は移行せず'))`
        );
        await waitFor(
            unauthenticated,
            `Boolean(document.querySelector('#login-form-username'))`,
            'original VRCX login form'
        );
        await checkpoint(unauthenticated, 'fixture-vrchat-login');
        unauthenticated.destroy();

        // Negative GET-only scenarios never submit or retain login credentials.
        const rateLimited = await createWindow('status-rate-limit', fingerprint, true, false, {
            authStatusFailure: { status: 429, retryAfter: '60', body: { error: 'rate_limited' } }
        });
        await bounded(
            rateLimited.loadURL(new URL('#/login', rateLimited.smokeUrl).href),
            'load rate-limited status fixture'
        );
        await waitFor(
            rateLimited,
            `Boolean(document.querySelector('[data-testid="web-worker-unavailable"]')) && document.body.innerText.includes('要求が多すぎ')`,
            'initial status 429 stays unknown and shows its actual error'
        );
        assert.equal(
            await evaluate(rateLimited, `Boolean(document.querySelector('#login-form'))`),
            false,
            'Unknown status incorrectly requested VRChat credentials'
        );
        await checkpoint(rateLimited, 'fixture-initial-status-rate-limit', false);
        assert.equal(rateLimited.smokeFixtureState.statusAttempts, 1, 'SSE ignored the Retry-After cooldown');
        rateLimited.destroy();

        const unavailable = await createWindow('worker-outage-tooltip', fingerprint, true, true, {
            authStatusFailure: { status: 503, body: { error: 'vrchat_worker_unavailable' } }
        });
        await bounded(unavailable.loadURL(new URL('#/feed', unavailable.smokeUrl).href), 'load worker outage fixture');
        await waitFor(
            unavailable,
            `document.querySelector('[data-testid="web-sync-status"]')?.getAttribute('data-sync-state') === 'worker_unavailable'`,
            'confirmed worker outage retains the existing read view'
        );
        const outageButton = `Array.from(document.querySelectorAll('button[aria-disabled="true"]')).find(button => button.innerText.includes('VRChat'))`;
        assert.equal(
            await evaluate(unavailable, `(${outageButton}).disabled`),
            false,
            'Native disabled prevented the worker explanation tooltip'
        );
        await hover(unavailable, outageButton);
        await waitFor(
            unavailable,
            `Array.from(document.querySelectorAll('[role="tooltip"]')).some(tooltip => tooltip.textContent.includes('background runtime is temporarily unavailable'))`,
            'unavailable button still opens the explanation tooltip'
        );
        await evaluate(unavailable, `(${outageButton}).click()`);
        assert.equal(
            await evaluate(unavailable, 'location.hash'),
            '#/feed',
            'Unavailable status button initiated reauthentication'
        );
        await checkpoint(unavailable, 'fixture-worker-outage-tooltip', false);
        unavailable.destroy();

        const fixtureWindow = await createWindow('fixtures', fingerprint, true);
        progress('fixtures: load original feed');
        await bounded(
            fixtureWindow.loadURL(new URL('#/feed', fixtureWindow.smokeUrl).href),
            'load authenticated fixture page',
            30_000
        );
        await waitFor(fixtureWindow, `Boolean(document.querySelector('#x-app .feed'))`, 'original VRCX feed');
        await waitFor(
            fixtureWindow,
            `(() => {
            const text = document.querySelector('#x-app .feed table')?.innerText || '';
            return text.includes('Smoke Friend') && text.includes('Smoke World');
        })()`,
            'initial original feed rows include friend and destination'
        );
        await checkpoint(fixtureWindow, 'fixture-feed');
        assert.ok((requestCounts.get('/status') || 0) >= 2, 'SSE status event did not refresh the fixed status API');

        // Exercise the existing menu's theme handler rather than changing classes
        // directly, so an accidental native AppApi leaf fails this smoke.
        await click(fixtureWindow, `document.querySelector('.ri-settings-3-line').closest('button')`);
        await waitFor(
            fixtureWindow,
            `Array.from(document.querySelectorAll('[role="menuitem"]')).some(element => element.innerText === 'Theme')`,
            'theme submenu'
        );
        await click(
            fixtureWindow,
            `Array.from(document.querySelectorAll('[role="menuitem"]')).find(element => element.innerText === 'Theme')`
        );
        await waitFor(
            fixtureWindow,
            `Array.from(document.querySelectorAll('[role="menuitemcheckbox"]')).some(element => element.innerText === 'Light')`,
            'light theme choice'
        );
        await evaluate(
            fixtureWindow,
            `Array.from(document.querySelectorAll('[role="menuitemcheckbox"]')).find(element => element.innerText === 'Light').click()`
        );
        await waitFor(
            fixtureWindow,
            `document.documentElement.getAttribute('data-theme') === 'light'`,
            'original theme selection handler'
        );
        await checkpoint(fixtureWindow, 'fixture-light-theme');

        // The fixture server sends regular events through a real EventSource.
        // Its next status event refreshes this changed read model and must retain
        // the mounted original feed while making staleness visible.
        fixtureWindow.smokeFixtureState.stale = true;
        await waitFor(
            fixtureWindow,
            `document.querySelector('[data-testid="web-sync-status"]')?.getAttribute('data-stale') === 'true'`,
            'SSE stale-state update'
        );
        assert.ok(
            await evaluate(fixtureWindow, `Boolean(document.querySelector('#x-app .feed'))`),
            'Stale sync discarded the existing feed'
        );
        await checkpoint(fixtureWindow, 'fixture-stale-feed');
        fixtureWindow.smokeFixtureState.stale = false;
        await waitFor(
            fixtureWindow,
            `document.querySelector('[data-testid="web-sync-status"]')?.getAttribute('data-stale') === 'false'`,
            'SSE recovery update'
        );
        for (const [name, route, selector] of [
            ['friends', '/social/friend-list', '.x-container table'],
            ['notifications', '/notification', '.x-container table'],
            ['game-log', '/game-log', '.x-container table'],
            ['instance-activity', '/charts/instance', '#chart'],
            ['hot-worlds', '/charts/hot-worlds', '.x-container'],
            ['mutual-graph', '/charts/mutual', '.x-container'],
            ['search', '/search', '.x-container']
        ]) {
            await evaluate(fixtureWindow, `location.hash = ${JSON.stringify(route)}`);
            await waitFor(
                fixtureWindow,
                `location.hash === ${JSON.stringify(`#${route}`)} && Boolean(document.querySelector(${JSON.stringify(selector)}))`,
                `original ${name} route`
            );
            await checkpoint(fixtureWindow, `fixture-${name}`);
        }
        fixtureWindow.setSize(390, 844);
        await evaluate(fixtureWindow, 'location.hash = "/social/friend-list"');
        await waitFor(
            fixtureWindow,
            'location.hash === "#/social/friend-list" && Boolean(document.querySelector(".x-container table"))',
            'mobile friends route'
        );
        await checkpoint(fixtureWindow, 'fixture-mobile-friends');
        await evaluate(fixtureWindow, `document.querySelector('[data-testid="web-mobile-navigation"]').click()`);
        await waitFor(
            fixtureWindow,
            `document.querySelector('[data-sidebar="sidebar"][data-mobile="true"]')?.getAttribute('data-state') === 'open'`,
            'mobile navigation sheet opens'
        );
        await checkpoint(fixtureWindow, 'fixture-mobile-navigation');
        await evaluate(
            fixtureWindow,
            `Array.from(document.querySelectorAll('[data-sidebar="sidebar"][data-mobile="true"] [data-sidebar="menu-button"]')).find(button => button.innerText.trim() === 'Feed').click()`
        );
        await waitFor(
            fixtureWindow,
            `location.hash === '#/feed' && !document.querySelector('[data-sidebar="sidebar"][data-mobile="true"][data-state="open"]')`,
            'mobile Feed selection closes navigation sheet'
        );
        await checkpoint(fixtureWindow, 'fixture-mobile-feed');
        assert.deepEqual(failures, [], 'Console errors or unexpected fixture API calls');
        await fs.writeFile(
            path.join(output, 'report.json'),
            JSON.stringify(
                {
                    mode: 'anonymous-live-and-authenticated-fixtures',
                    staticSource: process.env.VRCX_SMOKE_STATIC_ROOT ? 'local-build' : 'deployment',
                    checkpoints,
                    fixtureRequests: [...requests].sort(),
                    networkFailures,
                    fixtureHttpLog,
                    failures
                },
                null,
                2
            )
        );
        console.log(
            JSON.stringify({
                ok: true,
                checkpoints: checkpoints.map(({ name }) => name),
                fixtureRequests: requests.size,
                output
            })
        );
        fixtureWindow.destroy();
        clearTimeout(overallTimeout);
        app.exit(0);
    })
    .catch(async (error) => {
        progress(`failed during ${currentStage}: ${error.message}`);
        for (const [index, win] of BrowserWindow.getAllWindows().entries()) {
            try {
                await fs.writeFile(
                    path.join(output, `failure-${index}.png`),
                    (await bounded(win.webContents.capturePage(), 'failure screenshot', 3_000)).toPNG()
                );
            } catch {
                /* Preserve the original failure when the renderer has exited. */
            }
        }
        await fs
            .writeFile(
                path.join(output, 'report.json'),
                JSON.stringify(
                    {
                        ok: false,
                        error: error.message,
                        failures,
                        checkpoints,
                        fixtureRequests: [...requests].sort(),
                        networkFailures,
                        fixtureHttpLog
                    },
                    null,
                    2
                )
            )
            .catch(() => {});
        console.error(error.stack || error);
        console.error(JSON.stringify({ failures, checkpoints }));
        clearTimeout(overallTimeout);
        app.exit(1);
    });
