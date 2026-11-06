describe('runtimeCapabilities', () => {
    afterEach(() => {
        Reflect.deleteProperty(window, 'runtimeCapabilities');
        vi.resetModules();
    });

    it('keeps desktop behavior when preload does not expose capabilities', async () => {
        const { runtimeCapabilities } = await import('../runtimeCapabilities');

        expect(runtimeCapabilities.serverMode).toBe(false);
        expect(runtimeCapabilities.desktopIntegration).toBe(true);
        expect(runtimeCapabilities.vrOverlay).toBe(true);
    });

    it('uses the immutable server capabilities exposed by preload', async () => {
        Object.defineProperty(window, 'runtimeCapabilities', {
            configurable: true,
            value: {
                serverMode: true,
                desktopIntegration: false,
                gameProcessMonitoring: false,
                logWatcher: false,
                vrOverlay: false,
                discord: false,
                selfUpdate: false,
                vrchatMutationAutomation: false,
                customRendererExtensions: false
            }
        });

        const { runtimeCapabilities } = await import('../runtimeCapabilities');

        expect(runtimeCapabilities.serverMode).toBe(true);
        expect(runtimeCapabilities.desktopIntegration).toBe(false);
        expect(runtimeCapabilities.vrchatMutationAutomation).toBe(false);
        expect(Object.isFrozen(runtimeCapabilities)).toBe(true);
    });
});
