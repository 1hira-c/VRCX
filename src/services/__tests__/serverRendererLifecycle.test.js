import { EventEmitter } from 'node:events';
import { createRequire } from 'node:module';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const require = createRequire(import.meta.url);
const { attachServerRendererLifecycle } = require('../../../src-electron/serverRendererLifecycle.js');
const READY_TIMEOUT = 120_000;
const UNRESPONSIVE_GRACE = 30_000;
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

    it('allows slow initial migrations to complete without a readiness deadline', () => {
        const runtime = createLifecycle();
        runtime.webContents.emit('did-navigate', {}, 'file:///app/index.html', -1, '');
        runtime.window.emit('unresponsive');
        vi.advanceTimersByTime(30 * 60_000);

        expect(runtime.onFailure).not.toHaveBeenCalled();
        expect(vi.getTimerCount()).toBe(0);
        runtime.markReady();
        expect(runtime.ready).toBe(true);
    });

    it('does not schedule restarts merely because initialization takes a long time', () => {
        const runtime = createLifecycle();
        vi.advanceTimersByTime(READY_TIMEOUT - 1);
        runtime.markReady();
        vi.advanceTimersByTime(READY_TIMEOUT * 2);

        expect(runtime.ready).toBe(true);
        expect(runtime.onFailure).not.toHaveBeenCalled();
        expect(vi.getTimerCount()).toBe(0);
    });

    it('invalidates readiness only after the new main-frame document commits', () => {
        const runtime = createLifecycle();
        runtime.markReady();
        runtime.webContents.emit('did-start-navigation', { isSameDocument: false, isMainFrame: true });
        expect(runtime.ready).toBe(true);
        runtime.webContents.emit('did-navigate', {}, 'file:///app/index.html', -1, '');

        expect(runtime.ready).toBe(false);
        expect(runtime.markUnavailable).toHaveBeenCalledOnce();
        runtime.window.emit('unresponsive');
        vi.advanceTimersByTime(30 * 60_000);
        expect(runtime.onFailure).not.toHaveBeenCalled();
        expect(vi.getTimerCount()).toBe(0);
        runtime.markReady();
        expect(runtime.ready).toBe(true);
    });

    it.each(['cancelled', 'failed'])('preserves the old document when navigation is %s before commit', (outcome) => {
        const runtime = createLifecycle();
        runtime.markReady();
        runtime.webContents.emit('did-start-navigation', { isSameDocument: false, isMainFrame: true });
        if (outcome === 'cancelled') {
            runtime.webContents.emit('will-navigate', { preventDefault: vi.fn() });
        } else {
            runtime.webContents.emit('did-fail-load', {}, -105, 'ERR_NAME_NOT_RESOLVED');
        }
        runtime.webContents.emit('did-stop-loading');
        vi.advanceTimersByTime(30 * 60_000);

        expect(runtime.ready).toBe(true);
        expect(runtime.markUnavailable).not.toHaveBeenCalled();
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

    it('lets a short hang recover without restarting the worker', () => {
        const runtime = createLifecycle();
        runtime.markReady();
        runtime.window.emit('unresponsive');
        vi.advanceTimersByTime(UNRESPONSIVE_GRACE - 1);
        expect(runtime.ready).toBe(true);
        expect(runtime.onFailure).not.toHaveBeenCalled();
        runtime.window.emit('responsive');
        vi.advanceTimersByTime(UNRESPONSIVE_GRACE * 2);

        expect(runtime.ready).toBe(true);
        expect(runtime.onFailure).not.toHaveBeenCalled();
        expect(vi.getTimerCount()).toBe(0);
    });

    it('fails a continuously unresponsive ready window after one bounded grace period', () => {
        const runtime = createLifecycle();
        runtime.markReady();
        runtime.window.emit('unresponsive', { reason: 'untrusted detail' });
        vi.advanceTimersByTime(UNRESPONSIVE_GRACE - 1);
        expect(runtime.onFailure).not.toHaveBeenCalled();
        runtime.window.emit('unresponsive');
        vi.advanceTimersByTime(1);

        expect(runtime.ready).toBe(false);
        expect(runtime.onFailure).toHaveBeenCalledExactlyOnceWith('renderer_unresponsive');
        expect(vi.getTimerCount()).toBe(0);
    });

    it('cancels the previous document hang timer when a new document commits', () => {
        const runtime = createLifecycle();
        runtime.markReady();
        runtime.window.emit('unresponsive');
        runtime.webContents.emit('did-navigate', {}, 'file:///app/index.html', -1, '');
        vi.advanceTimersByTime(UNRESPONSIVE_GRACE * 2);

        expect(runtime.onFailure).not.toHaveBeenCalled();
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
        expect(runtime.window.listenerCount('responsive')).toBe(0);
        expect(runtime.webContents.listenerCount('did-navigate')).toBe(0);
        expect(vi.getTimerCount()).toBe(0);
    });
});
