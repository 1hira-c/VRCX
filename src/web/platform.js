/**
 * Build-time platform and capability selection for the browser build.
 *
 * This deliberately is not an Electron/AppApi compatibility shim. Shared
 * modules use these flags to avoid entering native-only leaves, while browser
 * data goes through the fixed server API adapters.
 */
export const isWebPlatform = import.meta.env.MODE === 'web';

export function createPlatformCapabilities(webPlatform) {
    return Object.freeze({
        serverSession: webPlatform,
        desktopIntegration: !webPlatform,
        localDatabase: !webPlatform,
        localGameIntegration: !webPlatform,
        nativeWindow: !webPlatform,
        vrchatMutations: !webPlatform,
        vrchatRead: true,
        importedHistory: true,
        entitySearch: true,
        entityDetails: true,
        memos: true,
        instanceActivityAnalytics: true,
        hotWorldAnalytics: true,
        cachedMutualGraph: true,
        gameLogSessions: true,
        userHistoryStats: true,
        activityDetails: !webPlatform,
        mutualConnections: !webPlatform,
        userNotes: !webPlatform,
        userRelatedResources: true,
        groupRelatedResources: true,
        marketplace: !webPlatform,
        store: !webPlatform
    });
}

export const platformCapabilities = createPlatformCapabilities(isWebPlatform);

const viewCapabilities = Object.freeze({
    charts: 'instanceActivityAnalytics',
    'charts-instance': 'instanceActivityAnalytics',
    'charts-hot-worlds': 'hotWorldAnalytics',
    'charts-mutual': 'cachedMutualGraph'
});

export function isPlatformViewAvailable(viewKey, capabilities = platformCapabilities) {
    const capability = viewCapabilities[viewKey];
    return !capability || capabilities[capability] === true;
}

export const webCapabilities = platformCapabilities;
export const isReadOnlyWeb = isWebPlatform && !platformCapabilities.vrchatMutations;
