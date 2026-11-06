import { ref } from 'vue';
import { storeToRefs } from 'pinia';

import { useSearchStore } from '../../../stores';
import { replaceBioSymbols } from '../../../shared/utils';
import { groupRequest } from '../../../api';
import { applyGroup } from '../../../coordinators/groupCoordinator';
import { searchWebEntities } from '../../../web/entities';
import { isWebPlatform } from '../../../web/platform';

function createDefaultSearchGroupParams() {
    return {
        n: 60,
        offset: 0,
        query: ''
    };
}

/**
 * Group search composable for Search view.
 * Manages group search state and pagination.
 */
export function useSearchGroup() {
    const { searchText } = storeToRefs(useSearchStore());

    const searchGroupParams = ref(createDefaultSearchGroupParams());
    const searchGroupResults = ref([]);
    const isSearchGroupLoading = ref(false);

    async function searchGroup() {
        searchGroupParams.value = {
            ...createDefaultSearchGroupParams(),
            query: replaceBioSymbols(searchText.value)
        };
        await moreSearchGroup();
    }

    /**
     * @param go
     */
    async function moreSearchGroup(go) {
        const params = searchGroupParams.value;
        if (go) {
            params.offset += params.n * go;
            if (params.offset < 0) {
                params.offset = 0;
            }
        }
        isSearchGroupLoading.value = true;
        try {
            const json = isWebPlatform
                ? (
                      await searchWebEntities('group', params.query, {
                          offset: params.offset,
                          limit: params.n
                      })
                  ).items
                : (await groupRequest.groupSearch(params)).json;
            const map = new Map();
            for (const item of json) {
                const ref = isWebPlatform ? applyGroup(item) : item;
                map.set(ref.id, ref);
            }
            searchGroupResults.value = Array.from(map.values());
        } finally {
            isSearchGroupLoading.value = false;
        }
    }

    function clearGroupSearch() {
        searchGroupParams.value = createDefaultSearchGroupParams();
        searchGroupResults.value = [];
    }

    return {
        searchGroupParams,
        searchGroupResults,
        isSearchGroupLoading,
        searchGroup,
        moreSearchGroup,
        clearGroupSearch
    };
}
