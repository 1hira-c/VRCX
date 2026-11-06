import { createPinia, setActivePinia } from 'pinia';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const mocks = vi.hoisted(() => ({
    database: {
        upgradeDatabaseVersion: vi.fn(),
        vacuum: vi.fn(),
        optimize: vi.fn(),
        setMaxTableSize: vi.fn(),
        setSearchTableSize: vi.fn()
    },
    config: { getInt: vi.fn(), getString: vi.fn(), setInt: vi.fn() },
    alert: vi.fn(),
    showDevTools: vi.fn(),
    report: vi.fn(),
    capabilities: { serverMode: true, customRendererExtensions: false }
}));
vi.mock('vue-i18n', () => ({ useI18n: () => ({ t: (key) => key }) }));
vi.mock('../../services/config', () => ({ default: mocks.config }));
vi.mock('../../services/database', () => ({ database: mocks.database }));
vi.mock('../../services/runtimeCapabilities', () => ({ runtimeCapabilities: mocks.capabilities }));
vi.mock('../../web/platform.js', () => ({ isWebPlatform: false }));
vi.mock('../../services/appConfig', () => ({ AppDebug: {} }));
vi.mock('../../services/watchState', () => ({ watchState: { isLoggedIn: false } }));
vi.mock('../../shared/utils', () => ({ debounce: vi.fn(), parseLocation: vi.fn() }));
vi.mock('../../shared/utils/base/ui', () => ({ refreshCustomScript: vi.fn() }));
vi.mock('../../api', () => ({ avatarRequest: {}, queryRequest: {} }));
vi.mock('../settings/advanced', () => ({ useAdvancedSettingsStore: () => ({}) }));
vi.mock('../avatarProvider', () => ({ useAvatarProviderStore: () => ({}) }));
vi.mock('../favorite', () => ({ useFavoriteStore: () => ({}) }));
vi.mock('../gameLog', () => ({ useGameLogStore: () => ({}) }));
vi.mock('../game', () => ({ useGameStore: () => ({}) }));
vi.mock('../location', () => ({ useLocationStore: () => ({}) }));
vi.mock('../modal', () => ({ useModalStore: () => ({ alert: mocks.alert }) }));
vi.mock('../notification', () => ({ useNotificationStore: () => ({}) }));
vi.mock('../photon', () => ({ usePhotonStore: () => ({}) }));
vi.mock('../search', () => ({ useSearchStore: () => ({}) }));
vi.mock('../updateLoop', () => ({ useUpdateLoopStore: () => ({}) }));
vi.mock('../user', () => ({ useUserStore: () => ({}) }));
vi.mock('../../coordinators/favoriteCoordinator', () => ({
    addLocalWorldFavorite: vi.fn(),
    addLocalAvatarFavorite: vi.fn()
}));
vi.mock('../../coordinators/groupCoordinator', () => ({ showGroupDialog: vi.fn() }));
vi.mock('../../coordinators/worldCoordinator', () => ({ showWorldDialog: vi.fn() }));
vi.mock('../../coordinators/avatarCoordinator', () => ({
    showAvatarDialog: vi.fn(),
    selectAvatarWithConfirmation: vi.fn(),
    selectAvatarWithoutConfirmation: vi.fn()
}));
vi.mock('../../coordinators/userCoordinator', () => ({ showUserDialog: vi.fn(), addCustomTag: vi.fn() }));
vi.mock('../../coordinators/vrcxCoordinator', () => ({ clearVRCXCache: vi.fn() }));
vi.mock('../../coordinators/searchIndexCoordinator', () => ({ resetSearchIndexOnLogin: vi.fn() }));

import { useVrcxStore } from '../vrcx';

beforeEach(() => {
    vi.clearAllMocks();
    setActivePinia(createPinia());
    mocks.capabilities.serverMode = true;
    mocks.config.getInt.mockImplementation((_key, value) => Promise.resolve(value));
    mocks.config.getString.mockResolvedValue('');
    mocks.config.setInt.mockResolvedValue();
    for (const fn of [mocks.database.upgradeDatabaseVersion, mocks.database.vacuum, mocks.database.optimize])
        fn.mockResolvedValue();
    mocks.alert.mockImplementation(() => new Promise(() => {}));
    vi.stubGlobal('electron', { reportServerStartupFailure: mocks.report });
    vi.stubGlobal('VRCXStorage', { Get: vi.fn().mockResolvedValue('true'), Set: vi.fn() });
    vi.stubGlobal('AppApi', { ShowDevTools: mocks.showDevTools });
});
afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
});

describe('original database startup in the hidden server renderer', () => {
    it.each(['upgradeDatabaseVersion', 'vacuum', 'optimize'])(
        'finishes failed %s initialization without waiting for an invisible modal',
        async (method) => {
            mocks.database[method].mockRejectedValueOnce(new Error('private SQL and database path'));
            const store = useVrcxStore();
            await expect(store.waitForDatabaseInit()).resolves.toBe(false);
            expect(mocks.report).toHaveBeenCalledExactlyOnceWith();
            expect(mocks.alert).not.toHaveBeenCalled();
            expect(mocks.config.setInt).not.toHaveBeenCalled();
        }
    );

    it('reports failures before the schema upgrade without leaving initialization pending', async () => {
        mocks.config.getInt.mockRejectedValueOnce(new Error('private database-open failure'));
        const store = useVrcxStore();
        await expect(store.waitForDatabaseInit()).resolves.toBe(false);
        expect(mocks.report).toHaveBeenCalledExactlyOnceWith();
        expect(mocks.alert).not.toHaveBeenCalled();
    });

    it('waits for the original migration, vacuum and optimization before making the database ready', async () => {
        let finishUpgrade;
        mocks.database.upgradeDatabaseVersion.mockImplementationOnce(
            () =>
                new Promise((resolve) => {
                    finishUpgrade = resolve;
                })
        );
        const store = useVrcxStore();
        await Promise.resolve();
        await Promise.resolve();
        expect(store.databaseReadyForAutoLogin).toBe(false);
        expect(mocks.database.vacuum).not.toHaveBeenCalled();
        finishUpgrade();
        await expect(store.waitForDatabaseInit()).resolves.toBe(true);
        expect(mocks.database.vacuum).toHaveBeenCalledOnce();
        expect(mocks.database.optimize).toHaveBeenCalledOnce();
        expect(mocks.config.setInt).toHaveBeenCalledWith('VRCX_databaseVersion', 18);
        expect(mocks.report).not.toHaveBeenCalled();
    });

    it('keeps the original desktop upgrade failure dialog and diagnostics', async () => {
        mocks.capabilities.serverMode = false;
        mocks.database.upgradeDatabaseVersion.mockRejectedValueOnce(new Error('desktop upgrade failed'));
        mocks.alert.mockResolvedValueOnce();
        vi.spyOn(console, 'error').mockImplementation(() => {});
        const store = useVrcxStore();
        await expect(store.waitForDatabaseInit()).resolves.toBe(false);
        expect(mocks.alert).toHaveBeenCalledOnce();
        expect(mocks.showDevTools).toHaveBeenCalledOnce();
        expect(mocks.report).not.toHaveBeenCalled();
    });
});
