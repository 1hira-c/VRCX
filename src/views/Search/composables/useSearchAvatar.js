import { ref } from 'vue';
import { storeToRefs } from 'pinia';
import { toast } from 'vue-sonner';

import { useAdvancedSettingsStore, useSearchStore } from '../../../stores';
import { lookupAvatars } from '../../../coordinators/avatarCoordinator';
import { applyAvatar } from '../../../coordinators/avatarCoordinator';
import { searchWebEntities } from '../../../web/entities';
import { isWebPlatform } from '../../../web/platform';

/**
 * Avatar search composable for Search view.
 * Searches remote avatar databases only (local avatar browsing is handled by My Avatars page).
 */
export function useSearchAvatar() {
    const { avatarRemoteDatabase } = storeToRefs(useAdvancedSettingsStore());
    const { searchText } = storeToRefs(useSearchStore());

    const searchAvatarPageNum = ref(0);
    const searchAvatarResults = ref([]);
    const searchAvatarPage = ref([]);
    const isSearchAvatarLoading = ref(false);

    async function searchAvatar() {
        isSearchAvatarLoading.value = true;
        try {
            const avatars = new Map();
            const query = searchText.value;

            // Web requests use the worker's persisted setting; browser-local
            // preferences cannot enable or disable its external providers.
            if (query && query.length >= 3 && (isWebPlatform || avatarRemoteDatabase.value)) {
                const data = isWebPlatform
                    ? (await searchWebEntities('avatar', query, { offset: 0, limit: 5_000 })).items
                    : await lookupAvatars('search', query);
                if (data && typeof data === 'object') {
                    data.forEach((avatar) => {
                        const ref = isWebPlatform ? applyAvatar(avatar) : avatar;
                        avatars.set(ref.id, ref);
                    });
                }
            }

            const avatarsArray = Array.from(avatars.values());
            searchAvatarPageNum.value = 0;
            searchAvatarResults.value = avatarsArray;
            searchAvatarPage.value = avatarsArray.slice(0, 10);
        } catch (error) {
            if (!isWebPlatform) throw error;
            toast.error(error.message);
        } finally {
            isSearchAvatarLoading.value = false;
        }
    }

    /**
     * @param n
     */
    function moreSearchAvatar(n) {
        let offset;
        if (n === -1) {
            searchAvatarPageNum.value--;
            offset = searchAvatarPageNum.value * 10;
        }
        if (n === 1) {
            searchAvatarPageNum.value++;
            offset = searchAvatarPageNum.value * 10;
        }
        searchAvatarPage.value = searchAvatarResults.value.slice(offset, offset + 10);
    }

    function clearAvatarSearch() {
        searchAvatarResults.value = [];
        searchAvatarPage.value = [];
        searchAvatarPageNum.value = 0;
    }

    return {
        searchAvatarPageNum,
        searchAvatarResults,
        searchAvatarPage,
        isSearchAvatarLoading,
        searchAvatar,
        moreSearchAvatar,
        clearAvatarSearch
    };
}
