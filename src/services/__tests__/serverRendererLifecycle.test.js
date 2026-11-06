import { EventEmitter } from 'node:events';
import { createRequire } from 'node:module';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const require = createRequire(import.meta.url);
const { attachServerRendererLifecycle } = require('../../../src-electron/serverRendererLifecycle.js');
const READY_TIMEOUT = 120_000;
const cleanups = [];

function createLifecycle(options = {}) {
    const window = new EventEmitter();
    window.webContents = new EventEmitter();
    let ready = false;
    const markUnavailable = vi.fn(() => {
        ready = false;
    });
    const onFailure = vi.fn();
    const lifecycle = attachServerRendererLifecycle(window, { markUnavailable, onFailure, ...options });
    cleanups.push(lifecycle.dispose);
    return {
        window,
        webContents: window.webContents,
        markUnavailable,
        onFailure,
        lifecycle,
        get ready() {
            return ready;
        },
        markReady() {
            if (lifecycle.markReady()) ready = true;
        }
    };
}

beforeEach(() => vi.useFakeTimers());
afterEach(() => {
    for (const dispose of cleanups.splice(0)) dispose();
    vi.useRealTimers();
});

describe('hidden server renderer lifecycle', () => {
    it('keeps readiness through original file-URL hash navigation and loading events', () => {
        const runtime = createLifecycle();
        runtime.markReady();

        runtime.webContents.emit('did-start-loading');
        runtime.webContents.emit('did-start-navigation', {}, 'file:///app/index.html#/feed', true, true);
        runtime.webContents.emit('did-navigate-in-page', {}, 'file:///app/index.html#/feed', true);
        runtime.webContents.emit('did-stop-loading');
        vi.advanceTimersByTime(READY_TIMEOUT * 2);

        expect(runtime.ready).toBe(true);
        expect(runtime.markUnavailable).not.toHaveBeenCalled();
        expect(runtime.onFailure).not.toHaveBeenCalled();
    });

    it('does not invalidate the main renderer when a subframe navigates', () => {
        const runtime = createLifecycle();
        runtime.markReady();
        runtime.webContents.emit('did-start-navigation', {}, 'https://example.invalid/', false, false);
        vi.advanceTimersByTime(READY_TIMEOUT * 2);

        expect(runtime.ready).toBe(true);
        expect(runtime.markUnavailable).not.toHaveBeenCalled();
        expect(runtime.onFailure).not.toHaveBeenCalled();
    });

    it('fails a renderer that never becomes ready after initial creation', () => {
        const runtime = createLifecycle();
        vi.advanceTimersByTime(READY_TIMEOUT - 1);
        expect(runtime.onFailure).not.toHaveBeenCalled();
        vi.advanceTimersByTime(1);

        expect(runtime.ready).toBe(false);
        expect(runtime.markUnavailable).toHaveBeenCalledOnce();
        expect(runtime.onFailure).toHaveBeenCalledExactlyOnceWith('renderer_ready_timeout');
        expect(runtime.lifecycle.markReady()).toBe(false);
    });

    it('cancels the initial watchdog after the renderer ready handshake', () => {
        const runtime = createLifecycle();
        vi.advanceTimersByTime(READY_TIMEOUT - 1);
        runtime.markReady();
        vi.advanceTimersByTime(READY_TIMEOUT * 2);

        expect(runtime.ready).toBe(true);
        expect(runtime.onFailure).not.toHaveBeenCalled();
        expect(vi.getTimerCount()).toBe(0);
    });

    it('invalidates readiness for a new main-frame document and starts a fresh watchdog', () => {
        const runtime = createLifecycle({ readyTimeoutMs: 1_000 });
        runtime.markReady();
        runtime.webContents.emit('did-start-navigation', {}, 'file:///app/index.html', false, true);

        expect(runtime.ready).toBe(false);
        expect(runtime.markUnavailable).toHaveBeenCalledOnce();
        vi.advanceTimersByTime(999);
        expect(runtime.onFailure).not.toHaveBeenCalled();
        vi.advanceTimersByTime(1);
        expect(runtime.onFailure).toHaveBeenCalledExactlyOnceWith('renderer_ready_timeout');
    });

    it('accepts a new document ready handshake and resets the timeout on another full navigation', () => {
        const runtime = createLifecycle({ readyTimeoutMs: 1_000 });
        runtime.markReady();
        runtime.webContents.emit('did-start-navigation', {}, 'file:///app/index.html', false, true);
        vi.advanceTimersByTime(800);
        runtime.webContents.emit('did-start-navigation', {}, 'file:///app/index.html', false, true);
        vi.advanceTimersByTime(800);
        expect(runtime.onFailure).not.toHaveBeenCalled();
        runtime.markReady();
        vi.advanceTimersByTime(2_000);

        expect(runtime.ready).toBe(true);
        expect(runtime.onFailure).not.toHaveBeenCalled();
    });

    it('reports renderer loss once using a fixed code instead of event details', () => {
        const runtime = createLifecycle();
        runtime.markReady();
        runtime.webContents.emit('render-process-gone', {}, { reason: 'arbitrary-sensitive-detail', exitCode: 123 });
        runtime.window.emit('unresponsive');
        vi.advanceTimersByTime(READY_TIMEOUT * 2);

        expect(runtime.ready).toBe(false);
        expect(runtime.onFailure).toHaveBeenCalledExactlyOnceWith('renderer_process_gone');
        expect(JSON.stringify(runtime.onFailure.mock.calls)).not.toContain('arbitrary-sensitive-detail');
    });

    it('fails an unresponsive hidden window without waiting for process exit', () => {
        const runtime = createLifecycle();
        runtime.markReady();
        runtime.window.emit('unresponsive', { reason: 'untrusted detail' });

        expect(runtime.ready).toBe(false);
        expect(runtime.onFailure).toHaveBeenCalledExactlyOnceWith('renderer_unresponsive');
        expect(vi.getTimerCount()).toBe(0);
    });

    it.each(['destroyed', 'closed'])('disposes the watchdog during normal %s without requesting restart', (event) => {
        const runtime = createLifecycle();
        (event === 'destroyed' ? runtime.webContents : runtime.window).emit(event);
        vi.advanceTimersByTime(READY_TIMEOUT * 2);

        expect(runtime.markUnavailable).toHaveBeenCalledOnce();
        expect(runtime.onFailure).not.toHaveBeenCalled();
        expect(runtime.lifecycle.markReady()).toBe(false);
        expect(vi.getTimerCount()).toBe(0);
    });

    it('removes only its own listeners and timer when explicitly disposed', () => {
        const runtime = createLifecycle();
        const unrelatedListener = vi.fn();
        runtime.webContents.on('did-start-navigation', unrelatedListener);
        runtime.lifecycle.dispose();
        runtime.lifecycle.dispose();
        runtime.webContents.emit('did-start-navigation', {}, 'file:///app/index.html', false, true);
        runtime.webContents.emit('render-process-gone');
        runtime.window.emit('unresponsive');
        vi.advanceTimersByTime(READY_TIMEOUT * 2);

        expect(unrelatedListener).toHaveBeenCalledOnce();
        expect(runtime.markUnavailable).not.toHaveBeenCalled();
        expect(runtime.onFailure).not.toHaveBeenCalled();
        expect(runtime.webContents.listenerCount('did-start-navigation')).toBe(1);
        expect(runtime.webContents.listenerCount('render-process-gone')).toBe(0);
        expect(runtime.window.listenerCount('unresponsive')).toBe(0);
        expect(vi.getTimerCount()).toBe(0);
    });
});
