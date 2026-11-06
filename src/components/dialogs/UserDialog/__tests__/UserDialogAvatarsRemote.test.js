import { beforeEach, describe, expect, it, vi } from 'vitest';
import { shallowMount } from '@vue/test-utils';
import { reactive } from 'vue';

const mocks = vi.hoisted(() => ({
    lookupAvatars: vi.fn(),
    toastError: vi.fn(),
    platform: { isWebPlatform: true },
    userStore: null,
    advancedStore: null,
    cachedAvatars: new Map()
}));

vi.mock('pinia', async () => ({ storeToRefs: (await import('vue')).toRefs }));
vi.mock('vue-i18n', () => ({ useI18n: () => ({ t: (key) => key }) }));
vi.mock('vue-sonner', () => ({ toast: { error: (...args) => mocks.toastError(...args) } }));
vi.mock('../../../../web/platform', () => mocks.platform);
vi.mock('../../../../stores', () => ({
    useUserStore: () => mocks.userStore,
    useAdvancedSettingsStore: () => mocks.advancedStore,
    useAvatarStore: () => ({ cachedAvatars: mocks.cachedAvatars })
}));
vi.mock('../../../../coordinators/avatarCoordinator', () => ({
    showAvatarDialog: vi.fn(),
    lookupAvatars: (...args) => mocks.lookupAvatars(...args)
}));
vi.mock('../../../../coordinators/userCoordinator', () => ({ refreshUserDialogAvatars: vi.fn() }));

import UserDialogAvatarsTab from '../UserDialogAvatarsTab.vue';

describe('Web user remote avatar loading', () => {
    beforeEach(() => {
        vi.clearAllMocks();
        mocks.platform.isWebPlatform = true;
        mocks.advancedStore = reactive({ avatarRemoteDatabase: false });
        mocks.userStore = reactive({
            userDialog: {
                id: 'usr_author',
                ref: { id: 'usr_author' },
                avatars: [{ id: 'avtr_previous', name: 'Previous', authorId: 'usr_author' }],
                licensedAvatars: [],
                isAvatarsLoading: false,
                avatarReleaseStatus: 'all'
            },
            currentUser: { id: 'usr_me' },
            sortUserDialogAvatars: vi.fn(),
            sortUserDialogLicensedAvatars: vi.fn()
        });
    });

    it('uses the worker setting while preserving cached avatars on provider failure', async () => {
        mocks.lookupAvatars.mockRejectedValue(new Error('Remote avatar lookup is disabled'));
        const wrapper = shallowMount(UserDialogAvatarsTab);
        await wrapper.vm.setUserDialogAvatarsRemote('usr_author');

        expect(mocks.lookupAvatars).toHaveBeenCalledExactlyOnceWith('authorId', 'usr_author');
        expect(mocks.userStore.userDialog.avatars).toHaveLength(1);
        expect(mocks.userStore.userDialog.avatars[0].id).toBe('avtr_previous');
        expect(mocks.userStore.userDialog.isAvatarsLoading).toBe(false);
        expect(mocks.toastError).toHaveBeenCalledWith('Remote avatar lookup is disabled');
    });

    it('merges the complete list once while rejecting avatars of a different author', async () => {
        mocks.lookupAvatars.mockResolvedValue(
            new Map([
                ['avtr_next', { id: 'avtr_next', authorId: 'usr_author' }],
                ['avtr_other', { id: 'avtr_other', authorId: 'usr_other' }]
            ])
        );
        const consoleError = vi.spyOn(console, 'error').mockImplementation(() => {});
        const wrapper = shallowMount(UserDialogAvatarsTab);
        await wrapper.vm.setUserDialogAvatarsRemote('usr_author');

        expect(mocks.userStore.userDialog.avatars.map((avatar) => avatar.id)).toEqual(['avtr_previous', 'avtr_next']);
        expect(mocks.userStore.userDialog.isAvatarsLoading).toBe(false);
        consoleError.mockRestore();
    });
});
