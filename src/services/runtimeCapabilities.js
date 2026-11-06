const desktopCapabilities = Object.freeze({
    serverMode: false,
    desktopIntegration: true,
    gameProcessMonitoring: true,
    logWatcher: true,
    vrOverlay: true,
    discord: true,
    selfUpdate: true,
    vrchatMutationAutomation: true,
    customRendererExtensions: true
});

const exposedCapabilities = typeof window === 'undefined' ? null : window.runtimeCapabilities;

/**
 * Runtime effects which are available to the renderer. Server mode keeps the
 * original renderer/domain runtime, but removes integrations that require a
 * user's desktop, VR runtime, or local VRChat installation.
 */
export const runtimeCapabilities = Object.freeze({
    ...desktopCapabilities,
    ...(exposedCapabilities ?? {})
});
