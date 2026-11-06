import { beforeEach, describe, expect, it, vi } from 'vitest';

const mocks = vi.hoisted(() => ({
    runtime: { serverMode: true },
    platform: { isWebPlatform: false, platformCapabilities: {} },
    provider: {
        avatarRemoteDatabaseProvider: 'https://provider.example/search',
        avatarRemoteDatabaseProviderList: ['https://provider.example/search', 'https://other.example/search']
    },
    updater: { vrcxId: '', loadVrcxId: vi.fn() },
    getBool: vi.fn(),
    execute: vi.fn(),
    getUserRemoteAvatars: vi.fn(),
    toastError: vi.fn(),
    toastWarning: vi.fn()
}));

vi.mock('../../shared/utils', () => ({ extractFileId: (url) => url?.split('/').at(-1) }));
vi.mock('../../api', () => ({ avatarRequest: {}, miscRequest: {}, queryRequest: {} }));
vi.mock('../../plugins/i18n', () => ({ i18n: { global: { t: (key) => key } } }));
vi.mock('../../services/appConfig', () => ({ logWebRequest: vi.fn() }));
vi.mock('../../services/database', () => ({ database: {} }));
vi.mock('../../queries', () => ({ patchAvatarFromEvent: vi.fn() }));
vi.mock('../../services/request', () => ({ processBulk: vi.fn() }));
vi.mock('../favoriteCoordinator', () => ({ applyFavorite: vi.fn() }));
vi.mock('../userCoordinator', () => ({ refreshUserDialogAvatars: vi.fn(), showUserDialog: vi.fn() }));
vi.mock('../searchIndexCoordinator', () => ({ syncAvatarSearchIndex: vi.fn(), removeAvatarSearchIndex: vi.fn() }));
vi.mock('../../stores/settings/advanced', () => ({ useAdvancedSettingsStore: vi.fn() }));
vi.mock('../../stores/avatarProvider', () => ({ useAvatarProviderStore: () => mocks.provider }));
vi.mock('../../stores/avatar', () => ({ useAvatarStore: vi.fn() }));
vi.mock('../../stores/favorite', () => ({ useFavoriteStore: vi.fn() }));
vi.mock('../../stores/modal', () => ({ useModalStore: vi.fn() }));
vi.mock('../../stores/ui', () => ({ useUiStore: vi.fn() }));
vi.mock('../../stores/user', () => ({ useUserStore: vi.fn() }));
vi.mock('../../stores/vrcxUpdater', () => ({ useVRCXUpdaterStore: () => mocks.updater }));
vi.mock('../../services/config', () => ({ default: { getBool: (...args) => mocks.getBool(...args) } }));
vi.mock('../../services/runtimeCapabilities', () => ({ runtimeCapabilities: mocks.runtime }));
vi.mock('../../services/webapi', () => ({ default: { execute: (...args) => mocks.execute(...args) } }));
vi.mock('../../web/entities', () => ({ getWebEntity: vi.fn() }));
vi.mock('../../web/platform', () => mocks.platform);
vi.mock('../../web/api', () => ({
    webApi: { getUserRemoteAvatars: (...args) => mocks.getUserRemoteAvatars(...args) }
}));
vi.mock('vue-sonner', () => ({
    toast: {
        error: (...args) => mocks.toastError(...args),
        warning: (...args) => mocks.toastWarning(...args)
    }
}));

import { lookupAvatarByImageFileId, lookupAvatars } from '../avatarCoordinator';

describe('original avatar provider lookup in the server worker', () => {
    beforeEach(() => {
        vi.clearAllMocks();
        mocks.runtime.serverMode = true;
        mocks.platform.isWebPlatform = false;
        mocks.getBool.mockResolvedValue(true);
        mocks.updater.vrcxId = '';
        mocks.updater.loadVrcxId.mockImplementation(async () => {
            mocks.updater.vrcxId = 'persisted-installation-id';
        });
        mocks.execute.mockResolvedValue({ status: 200, data: '[]' });
    });

    it.each(['search', 'authorId'])(
        'blocks %s before contacting any provider when the stored setting is off',
        async (type) => {
            mocks.getBool.mockResolvedValue(false);

            await expect(lookupAvatars(type, 'usr_author')).rejects.toMatchObject({
                code: 'avatar_remote_database_disabled'
            });

            expect(mocks.getBool).toHaveBeenCalledWith('VRCX_avatarRemoteDatabase', true);
            expect(mocks.execute).not.toHaveBeenCalled();
            expect(mocks.updater.loadVrcxId).not.toHaveBeenCalled();
        }
    );

    it('waits for the original persisted VRCX-ID before sending a provider request', async () => {
        let finishLoadingId;
        mocks.updater.loadVrcxId.mockImplementation(
            () =>
                new Promise((resolve) => {
                    finishLoadingId = () => {
                        mocks.updater.vrcxId = 'loaded-id';
                        resolve();
                    };
                })
        );
        const pending = lookupAvatars('search', 'name');
        await vi.waitFor(() => expect(finishLoadingId).toBeTypeOf('function'));
        expect(mocks.execute).not.toHaveBeenCalled();
        finishLoadingId();
        await pending;

        expect(mocks.execute).toHaveBeenCalledWith(
            expect.objectContaining({ headers: { Referer: 'https://vrcx.app', 'VRCX-ID': 'loaded-id' } })
        );
    });

    it('uses the original one-request-per-provider author lookup', async () => {
        mocks.execute.mockResolvedValue({ status: 200, data: '[{"id":"avtr_one","authorId":"usr_author"}]' });
        const result = await lookupAvatars('authorId', 'usr_author');

        expect(mocks.execute).toHaveBeenCalledTimes(2);
        expect(result.size).toBe(1);
        for (const [request] of mocks.execute.mock.calls) {
            expect(request.url).toContain('?authorId=usr_author');
            expect(request.headers['VRCX-ID']).toBe('persisted-installation-id');
        }
    });

    it.each(['search', 'authorId'])('reports a %s provider failure instead of an empty success', async (type) => {
        mocks.execute.mockRejectedValue(new TypeError('provider unavailable'));
        await expect(lookupAvatars(type, 'value')).rejects.toThrow('provider unavailable');
        expect(mocks.toastError).not.toHaveBeenCalled();
    });

    it('retains another provider author result when one provider is unavailable', async () => {
        mocks.execute.mockRejectedValueOnce(new TypeError('provider unavailable')).mockResolvedValueOnce({
            status: 200,
            data: '[{"id":"avtr_other","authorId":"usr_author"}]'
        });

        const onPartialFailure = vi.fn();
        const result = await lookupAvatars('authorId', 'usr_author', { onPartialFailure });

        expect(mocks.execute).toHaveBeenCalledTimes(2);
        expect(Array.from(result.keys())).toEqual(['avtr_other']);
        expect(onPartialFailure).toHaveBeenCalledExactlyOnceWith('avatar_provider_partial_failure');
    });

    it('reports failed author lookups when the other provider returns no avatars', async () => {
        mocks.execute.mockRejectedValueOnce(new TypeError('provider unavailable')).mockResolvedValueOnce({
            status: 200,
            data: '[]'
        });

        await expect(lookupAvatars('authorId', 'usr_author')).rejects.toThrow('provider unavailable');
        expect(mocks.execute).toHaveBeenCalledTimes(2);
    });

    it('continues author image fallbacks after a provider failure', async () => {
        mocks.execute
            .mockRejectedValueOnce(new Error('unsupported file lookup'))
            .mockRejectedValueOnce(new Error('unsupported file lookup'))
            .mockRejectedValueOnce(new Error('provider unavailable'))
            .mockResolvedValueOnce({
                status: 200,
                data: '[{"id":"avtr_found","authorId":"usr_author","imageUrl":"https://cdn/file_image"}]'
            });

        expect(await lookupAvatarByImageFileId('usr_author', 'file_image')).toBe('avtr_found');
        expect(mocks.execute).toHaveBeenCalledTimes(4);
    });

    it('transports the author list once in Web mode even for a full former page', async () => {
        mocks.runtime.serverMode = false;
        mocks.platform.isWebPlatform = true;
        mocks.getUserRemoteAvatars.mockResolvedValue({
            items: Array.from({ length: 200 }, (_, i) => ({ id: `avtr_${i}`, authorId: 'usr_author' })),
            warningCode: 'avatar_provider_partial_failure'
        });

        const result = await lookupAvatars('authorId', 'usr_author');

        expect(result.size).toBe(200);
        expect(mocks.getUserRemoteAvatars).toHaveBeenCalledExactlyOnceWith('usr_author');
        expect(mocks.toastWarning).toHaveBeenCalledExactlyOnceWith('web.vrchat.avatar_provider_partial_failure');
        expect(mocks.execute).not.toHaveBeenCalled();
    });
});
