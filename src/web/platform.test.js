import { describe, expect, it } from 'vitest';

import { createPlatformCapabilities, isPlatformViewAvailable } from './platform';

describe('Web analytics capabilities', () => {
    it('exposes the original read-only chart views without enabling live mutual fetches', () => {
        const capabilities = createPlatformCapabilities(true);

        expect(capabilities.instanceActivityAnalytics).toBe(true);
        expect(capabilities.hotWorldAnalytics).toBe(true);
        expect(capabilities.cachedMutualGraph).toBe(true);
        expect(capabilities.mutualConnections).toBe(false);
        expect(capabilities.vrchatMutations).toBe(false);

        expect(isPlatformViewAvailable('charts', capabilities)).toBe(true);
        expect(isPlatformViewAvailable('charts-instance', capabilities)).toBe(true);
        expect(isPlatformViewAvailable('charts-hot-worlds', capabilities)).toBe(true);
        expect(isPlatformViewAvailable('charts-mutual', capabilities)).toBe(true);
    });

    it('independently gates each chart route and dashboard panel key', () => {
        const capabilities = {
            ...createPlatformCapabilities(true),
            instanceActivityAnalytics: false,
            hotWorldAnalytics: false,
            cachedMutualGraph: false
        };

        expect(isPlatformViewAvailable('charts-instance', capabilities)).toBe(false);
        expect(isPlatformViewAvailable('charts-hot-worlds', capabilities)).toBe(false);
        expect(isPlatformViewAvailable('charts-mutual', capabilities)).toBe(false);
        expect(isPlatformViewAvailable('feed', capabilities)).toBe(true);
    });
});
