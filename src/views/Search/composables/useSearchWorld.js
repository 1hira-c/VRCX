import { ref } from 'vue';
import { storeToRefs } from 'pinia';

import { replaceBioSymbols } from '../../../shared/utils';
import { useAuthStore, useSearchStore, useWorldStore } from '../../../stores';
import { worldRequest } from '../../../api';
import { applyWorld } from '../../../coordinators/worldCoordinator';
import { searchWebEntities } from '../../../web/entities';
import { isWebPlatform } from '../../../web/platform';

/** @returns {import('vrchat').SearchWorlds['query']} */
function createDefaultSearchWorldParams() {
    return {
        n: 60,
        offset: 0
    };
}

/**
 * World search composable for Search view.
 * Manages world search state, category selection, and pagination.
 */
export function useSearchWorld() {
    const { cachedWorlds } = useWorldStore();
    const { searchText } = storeToRefs(useSearchStore());
    const { cachedConfig } = storeToRefs(useAuthStore());

    const searchWorldOption = ref('');
    const searchWorldLabs = ref(false);
    const searchWorldParams = ref(createDefaultSearchWorldParams());
    const searchWorldCategoryIndex = ref(null);
    const searchWorldResults = ref([]);
    const isSearchWorldLoading = ref(false);

    /**
     * @param ref
     */
    function searchWorld(ref) {
        searchWorldOption.value = '';
        searchWorldCategoryIndex.value = ref?.index ?? null;
        const params = createDefaultSearchWorldParams();
        switch (ref.sortHeading) {
            case 'featured':
                params.sort = 'order';
                params.featured = true;
                break;
            case 'trending':
                params.sort = 'popularity';
                params.featured = false;
                break;
            case 'updated':
                params.sort = 'updated';
                break;
            case 'created':
                params.sort = 'created';
                break;
            case 'publication':
                params.sort = 'publicationDate';
                break;
            case 'shuffle':
                params.sort = 'shuffle';
                break;
            case 'active':
                searchWorldOption.value = 'active';
                break;
            case 'recent':
                searchWorldOption.value = 'recent';
                break;
            case 'favorite':
                searchWorldOption.value = 'favorites';
                break;
            case 'labs':
                params.sort = 'labsPublicationDate';
                break;
            case 'heat':
                params.sort = 'heat';
                params.featured = false;
                break;
            default:
                params.sort = 'relevance';
                params.search = replaceBioSymbols(searchText.value);
                break;
        }
        params.order = ref.sortOrder || 'descending';
        if (ref.sortOwnership === 'mine') {
            params.user = 'me';
            params.releaseStatus = 'all';
        }
        if (ref.tag) {
            params.tag = ref.tag;
        }
        if (!searchWorldLabs.value) {
            if (params.tag) {
                params.tag += ',system_approved';
            } else {
                params.tag = 'system_approved';
            }
        }
        // TODO: option.platform
        searchWorldParams.value = params;
        moreSearchWorld();
    }

    /**
     * @param index
     */
    function handleSearchWorldCategorySelect(index) {
        searchWorldCategoryIndex.value = index;
        const row = cachedConfig.value?.dynamicWorldRows?.find((r) => r.index === index);
        searchWorld(row || {});
    }

    /**
     * @param go
     */
    async function moreSearchWorld(go) {
        const params = searchWorldParams.value;
        if (go) {
            params.offset += params.n * go;
            if (params.offset < 0) {
                params.offset = 0;
            }
        }
        isSearchWorldLoading.value = true;
        try {
            const json = isWebPlatform
                ? (
                      await searchWebEntities(
                          'world',
                          params.search || '',
                          {
                              offset: params.offset,
                              limit: params.n
                          },
                          {
                              worldOption: searchWorldOption.value || undefined,
                              sort: params.sort,
                              order: params.order,
                              featured: params.featured,
                              tag: params.tag,
                              user: params.user,
                              releaseStatus: params.releaseStatus
                          }
                      )
                  ).items
                : (await worldRequest.getWorlds(params, searchWorldOption.value)).json;
            if (isWebPlatform) {
                json.forEach((world) => applyWorld(world));
            }
            const map = new Map();
            for (const item of json) {
                const ref = cachedWorlds.get(item.id);
                if (typeof ref !== 'undefined') {
                    map.set(ref.id, ref);
                }
            }
            searchWorldResults.value = Array.from(map.values());
        } finally {
            isSearchWorldLoading.value = false;
        }
    }

    function clearWorldSearch() {
        searchWorldParams.value = createDefaultSearchWorldParams();
        searchWorldResults.value = [];
    }

    return {
        searchWorldOption,
        searchWorldLabs,
        searchWorldParams,
        searchWorldCategoryIndex,
        searchWorldResults,
        isSearchWorldLoading,
        searchWorld,
        moreSearchWorld,
        handleSearchWorldCategorySelect,
        clearWorldSearch
    };
}
