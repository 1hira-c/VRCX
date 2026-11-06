import { describe, expect, it, vi, beforeEach } from 'vitest';
const mocks = vi.hoisted(() => ({
    avatarRemoteDatabase: require('vue').ref(true),
    searchText: require('vue').ref(''),
    lookupAvatars: vi.fn(),
    searchWebEntities: vi.fn(),
    toastError: vi.fn(),
    platform: { isWebPlatform: false }
}));

vi.mock('pinia', () => ({
    storeToRefs: (store) => store
}));

vi.mock('../../../../stores', () => ({
    useAdvancedSettingsStore: () => ({
        avatarRemoteDatabase: mocks.avatarRemoteDatabase
    }),
    useSearchStore: () => ({
        searchText: mocks.searchText
    })
}));

vi.mock('../../../../coordinators/avatarCoordinator', () => ({
    lookupAvatars: (...args) => mocks.lookupAvatars(...args),
    applyAvatar: (avatar) => avatar
}));
vi.mock('../../../../web/entities', () => ({ searchWebEntities: (...args) => mocks.searchWebEntities(...args) }));
vi.mock('../../../../web/platform', () => mocks.platform);
vi.mock('vue-sonner', () => ({ toast: { error: (...args) => mocks.toastError(...args) } }));

import { useSearchAvatar } from '../useSearchAvatar';

describe('useSearchAvatar', () => {
    beforeEach(() => {
        mocks.avatarRemoteDatabase.value = true;
        mocks.searchText.value = '';
        mocks.lookupAvatars.mockReset();
        mocks.searchWebEntities.mockReset();
        mocks.toastError.mockReset();
        mocks.platform.isWebPlatform = false;
    });

    it('queries remote avatars and builds first page', async () => {
        mocks.searchText.value = 'alice';
        mocks.lookupAvatars.mockResolvedValue([
            { id: 'avtr_1', name: 'A' },
            { id: 'avtr_1', name: 'A-dup' },
            { id: 'avtr_2', name: 'B' }
        ]);

        const api = useSearchAvatar();
        await api.searchAvatar();

        expect(mocks.lookupAvatars).toHaveBeenCalledWith('search', 'alice');
        expect(api.searchAvatarResults.value.map((x) => x.id)).toEqual(['avtr_1', 'avtr_2']);
        expect(api.searchAvatarPage.value.map((x) => x.id)).toEqual(['avtr_1', 'avtr_2']);
        expect(api.searchAvatarPageNum.value).toBe(0);
    });

    it('skips remote query when text is too short', async () => {
        mocks.searchText.value = 'ab';
        const api = useSearchAvatar();

        await api.searchAvatar();

        expect(mocks.lookupAvatars).not.toHaveBeenCalled();
        expect(api.searchAvatarResults.value).toEqual([]);
    });

    it('honors the original desktop remote database setting', async () => {
        mocks.avatarRemoteDatabase.value = false;
        mocks.searchText.value = 'name';
        await useSearchAvatar().searchAvatar();
        expect(mocks.lookupAvatars).not.toHaveBeenCalled();
    });

    it('loads the bounded complete Web result through the worker setting instead of browser preferences', async () => {
        mocks.platform.isWebPlatform = true;
        mocks.avatarRemoteDatabase.value = false;
        mocks.searchText.value = 'name';
        mocks.searchWebEntities.mockResolvedValue({ items: [{ id: 'avtr_one' }] });
        const api = useSearchAvatar();
        await api.searchAvatar();

        expect(mocks.searchWebEntities).toHaveBeenCalledExactlyOnceWith('avatar', 'name', { offset: 0, limit: 5_000 });
        expect(api.searchAvatarResults.value).toEqual([{ id: 'avtr_one' }]);
        expect(mocks.lookupAvatars).not.toHaveBeenCalled();
    });

    it('keeps prior Web results and releases loading when the worker reports a provider failure', async () => {
        mocks.platform.isWebPlatform = true;
        mocks.searchText.value = 'name';
        mocks.searchWebEntities.mockRejectedValue(new Error('Remote avatar database is unavailable'));
        const api = useSearchAvatar();
        const previous = [{ id: 'avtr_previous' }];
        api.searchAvatarResults.value = previous;
        api.searchAvatarPage.value = previous;
        api.searchAvatarPageNum.value = 2;

        await api.searchAvatar();

        expect(api.searchAvatarResults.value).toEqual(previous);
        expect(api.searchAvatarPage.value).toEqual(previous);
        expect(api.searchAvatarPageNum.value).toBe(2);
        expect(api.isSearchAvatarLoading.value).toBe(false);
        expect(mocks.toastError).toHaveBeenCalledWith('Remote avatar database is unavailable');
    });

    it('paginates results by 10 items', () => {
        const api = useSearchAvatar();
        api.searchAvatarResults.value = Array.from({ length: 25 }, (_, i) => ({
            id: `avtr_${i}`
        }));
        api.searchAvatarPage.value = api.searchAvatarResults.value.slice(0, 10);

        api.moreSearchAvatar(1);
        expect(api.searchAvatarPageNum.value).toBe(1);
        expect(api.searchAvatarPage.value.map((x) => x.id)).toEqual(
            Array.from({ length: 10 }, (_, i) => `avtr_${i + 10}`)
        );

        api.moreSearchAvatar(-1);
        expect(api.searchAvatarPageNum.value).toBe(0);
        expect(api.searchAvatarPage.value.map((x) => x.id)).toEqual(Array.from({ length: 10 }, (_, i) => `avtr_${i}`));
    });
});
