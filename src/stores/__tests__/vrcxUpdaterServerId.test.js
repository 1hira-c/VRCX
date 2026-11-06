import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

const mocks = vi.hoisted(() => ({
    getString: vi.fn(),
    setString: vi.fn(),
    runtime: { selfUpdate: false },
    platform: { isWebPlatform: false }
}));

vi.mock('../../services/config', () => ({
    default: { getString: (...args) => mocks.getString(...args), setString: (...args) => mocks.setString(...args) }
}));
vi.mock('../../services/runtimeCapabilities', () => ({ runtimeCapabilities: mocks.runtime }));
vi.mock('../../web/platform.js', () => mocks.platform);
vi.mock('../../shared/utils', () => ({ changeLogRemoveLinks: vi.fn() }));
vi.mock('vue-i18n', () => ({ useI18n: () => ({ t: (key) => key }) }));

import { useVRCXUpdaterStore } from '../vrcxUpdater';

describe('server VRCX installation identity', () => {
    beforeEach(() => {
        vi.clearAllMocks();
        setActivePinia(createPinia());
        mocks.platform.isWebPlatform = false;
        mocks.getString.mockResolvedValue('saved-id');
        mocks.setString.mockResolvedValue(undefined);
        vi.stubGlobal('AppApi', { GetVersion: vi.fn().mockResolvedValue('2026.1.0') });
    });

    it('loads the existing installation ID while self updates remain disabled', async () => {
        const store = useVRCXUpdaterStore();
        await store.loadVrcxId();

        expect(store.vrcxId).toBe('saved-id');
        expect(store.noUpdater).toBe(true);
        expect(store.autoUpdateVRCX).toBe('Off');
        expect(mocks.getString).toHaveBeenCalledWith('VRCX_id', '');
        expect(mocks.setString).not.toHaveBeenCalled();
    });

    it('generates and persists one stable ID across concurrent startup and lookup calls', async () => {
        mocks.getString.mockResolvedValue('');
        const store = useVRCXUpdaterStore();
        const [firstId, secondId] = await Promise.all([store.loadVrcxId(), store.loadVrcxId()]);

        expect(firstId).toMatch(/^[a-f0-9-]{36}$/);
        expect(firstId).toBe(secondId);
        expect(store.vrcxId).toBe(firstId);
        expect(mocks.setString).toHaveBeenCalledExactlyOnceWith('VRCX_id', firstId);
        expect(await store.loadVrcxId()).toBe(firstId);
        expect(mocks.getString).toHaveBeenCalledTimes(1);
    });

    it('never creates a second browser installation ID', async () => {
        mocks.platform.isWebPlatform = true;
        const store = useVRCXUpdaterStore();
        expect(await store.loadVrcxId()).toBe('');
        expect(mocks.getString).not.toHaveBeenCalled();
        expect(mocks.setString).not.toHaveBeenCalled();
    });
});
