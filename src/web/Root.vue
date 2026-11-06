<template>
    <main v-if="!webSession.state.ready" class="web-auth-page">
        <div class="web-auth-loading">
            <span class="web-brand-mark">VRCX</span>
            <p>安全な接続を確認しています…</p>
        </div>
    </main>

    <WebAuthGate v-else-if="!webSession.state.auth.bootstrapped || !webSession.state.auth.authenticated" />

    <main v-else-if="workerUnavailableWithoutData" class="web-auth-page" data-testid="web-worker-unavailable">
        <div class="web-auth-loading">
            <span class="web-brand-mark">VRCX</span>
            <p>{{ t('web.vrchat.worker_unavailable_title') }}</p>
            <p class="max-w-md text-center">{{ t('web.vrchat.worker_unavailable_description') }}</p>
        </div>
    </main>

    <WebSnapshotSetup v-else-if="showSnapshotSetup" @continue="dismissSnapshotSetup" />

    <template v-else>
        <WebStateBootstrap />
        <App />
    </template>
</template>

<script setup>
    import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
    import { useRouter } from 'vue-router';
    import { useI18n } from 'vue-i18n';

    import App from '../App.vue';
    import { watchState } from '../services/watchState';
    import WebAuthGate from './components/WebAuthGate.vue';
    import WebSnapshotSetup from './components/WebSnapshotSetup.vue';
    import WebStateBootstrap from './components/WebStateBootstrap.vue';
    import { canBrowseWeb, hasExistingWebProjection, webSession } from './session';

    const router = useRouter();
    const { t } = useI18n();
    const vrchatAuthenticated = computed(
        () => webSession.state.auth.authenticated && webSession.state.vrchatAuth.status === 'authenticated'
    );
    const canBrowse = computed(canBrowseWeb);
    const workerUnavailableWithoutData = computed(
        () => webSession.state.workerConnection === 'unavailable' && !hasExistingWebProjection()
    );
    const snapshotSetupDismissed = ref(readSnapshotSetupDismissed());
    const showSnapshotSetup = computed(
        () =>
            webSession.state.ready &&
            webSession.state.auth.authenticated &&
            webSession.state.workerConnection === 'available' &&
            !snapshotSetupDismissed.value &&
            webSession.state.vrchatAuth.status !== 'authenticated' &&
            !hasExistingWebProjection()
    );

    function readSnapshotSetupDismissed() {
        try {
            return window.sessionStorage.getItem('vrcx.snapshotSetupDismissed') === '1';
        } catch {
            return false;
        }
    }

    function dismissSnapshotSetup() {
        snapshotSetupDismissed.value = true;
        try {
            window.sessionStorage.setItem('vrcx.snapshotSetupDismissed', '1');
        } catch {
            // Session storage can be unavailable in hardened browsers; the in-memory choice still applies.
        }
    }

    watch(
        [
            () => webSession.state.ready,
            () => webSession.state.auth.authenticated,
            () => webSession.state.vrchatAuth.status,
            () => webSession.state.workerConnection,
            canBrowse
        ],
        ([ready, serverAuthenticated, vrchatStatus, workerConnection, browseAllowed]) => {
            if (!ready) return;

            const authenticated = vrchatStatus === 'authenticated';
            if (!serverAuthenticated) return;
            if (workerConnection === 'unavailable') {
                if (browseAllowed && router.currentRoute.value.name === 'login') {
                    void router.replace({ name: 'feed' });
                }
                return;
            }

            if (!browseAllowed && router.currentRoute.value.name !== 'login') {
                const redirect = router.currentRoute.value.fullPath;
                void router.replace({
                    name: 'login',
                    query: redirect && redirect !== '/feed' ? { redirect } : undefined
                });
                return;
            }
            if (!browseAllowed || router.currentRoute.value.name !== 'login') return;
            if (!authenticated && router.currentRoute.value.query.reauth === '1') return;

            const redirect = router.currentRoute.value.query.redirect;
            void router.replace(typeof redirect === 'string' && redirect ? redirect : { name: 'feed' });
        },
        { immediate: true }
    );

    onMounted(() => {
        document.documentElement.classList.add('dark');
        // Original login state remains reserved for the desktop/worker VRChat
        // session. Browser route access is derived separately by canBrowseWeb().
        watchState.isLoggedIn = false;
        void webSession.initialize();
    });

    onBeforeUnmount(() => {
        webSession.closeEvents();
        watchState.isLoggedIn = false;
    });
</script>
