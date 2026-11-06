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
            <p>{{ t(workerStatusTitle) }}</p>
            <p class="max-w-md text-center">{{ t(workerStatusDescription) }}</p>
            <p
                v-if="webSession.state.workerConnection === 'check_failed' && webSession.state.error"
                class="max-w-md text-center">
                {{ webSession.state.error }}
            </p>
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
    import { resolveWebSessionRoute } from './sessionRouting';

    const router = useRouter();
    const { t } = useI18n();
    const canBrowse = computed(canBrowseWeb);
    const workerUnavailableWithoutData = computed(
        () => ['unavailable', 'check_failed'].includes(webSession.state.workerConnection) && !hasExistingWebProjection()
    );
    const workerStatusTitle = computed(() =>
        webSession.state.workerFailureReason === 'database_initialization_failed'
            ? 'web.vrchat.database_initialization_failed_title'
            : webSession.state.workerConnection === 'check_failed'
              ? 'web.vrchat.worker_check_failed_title'
              : 'web.vrchat.worker_unavailable_title'
    );
    const workerStatusDescription = computed(() =>
        webSession.state.workerFailureReason === 'database_initialization_failed'
            ? 'web.vrchat.database_initialization_failed_description'
            : webSession.state.workerConnection === 'check_failed'
              ? 'web.vrchat.worker_check_failed_description'
              : 'web.vrchat.worker_unavailable_description'
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
            const destination = resolveWebSessionRoute({
                ready,
                serverAuthenticated,
                vrchatStatus,
                workerConnection,
                browseAllowed,
                route: router.currentRoute.value
            });
            if (destination) void router.replace(destination);
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
