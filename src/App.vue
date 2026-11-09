<template>
    <TooltipProvider>
        <MacOSTitleBar></MacOSTitleBar>

        <div
            id="x-app"
            class="flex w-screen h-screen overflow-hidden cursor-default [&>.x-container]:pt-[15px]"
            :class="{ 'pt-7': isMacOS }">
            <RouterView></RouterView>
            <Toaster position="top-center" :theme="theme"></Toaster>

            <AlertDialogModal></AlertDialogModal>
            <OpenExternalLinkDialog></OpenExternalLinkDialog>
            <PromptDialogModal></PromptDialogModal>
            <OtpDialogModal></OtpDialogModal>
            <DatabaseUpgradeDialog v-if="platformCapabilities.localDatabase"></DatabaseUpgradeDialog>

            <VRCXUpdateDialog v-if="platformCapabilities.desktopIntegration"></VRCXUpdateDialog>
        </div>
        <div id="x-dialog-portal" class="x-dialog-portal"></div>
    </TooltipProvider>
</template>

<script setup>
    import { computed, onBeforeMount, onMounted } from 'vue';

    import { addGameLogEvent, getGameLogTable } from './coordinators/gameLogCoordinator';
    import {
        runCheckVRChatDebugLoggingFlow,
        runUpdateIsGameRunningFlow,
        runUpdateIsHmdAfkFlow
    } from './coordinators/gameCoordinator';
    import { Toaster } from './components/ui/sonner';
    import { TooltipProvider } from './components/ui/tooltip';
    import { createGlobalStores, useAppearanceSettingsStore } from './stores';
    import { initNoty } from './plugins/noty';
    import { runtimeCapabilities } from './services/runtimeCapabilities';
    import { startServerAuthControl } from './services/serverAuthControl';
    import { startServerProjectionPublisher } from './services/serverProjectionPublisher';
    import { platformCapabilities } from './web/platform';

    import AlertDialogModal from './components/ui/alert-dialog/AlertDialogModal.vue';
    import DatabaseUpgradeDialog from './components/dialogs/DatabaseUpgradeDialog.vue';
    import MacOSTitleBar from './components/MacOSTitleBar.vue';
    import OpenExternalLinkDialog from './components/dialogs/OpenExternalLinkDialog.vue';
    import OtpDialogModal from './components/ui/dialog/OtpDialogModal.vue';
    import PromptDialogModal from './components/ui/dialog/PromptDialogModal.vue';
    import VRCXUpdateDialog from './components/dialogs/VRCXUpdateDialog.vue';

    import '@/styles/globals.css';

    console.log(`isLinux: ${LINUX}`);

    const isMacOS = computed(() => navigator.platform.includes('Mac'));

    const theme = computed(() => {
        return store.appearanceSettings.isDarkMode ? 'dark' : 'light';
    });

    initNoty();

    const store = platformCapabilities.desktopIntegration
        ? createGlobalStores()
        : { appearanceSettings: useAppearanceSettingsStore() };

    const coordinatorModules = import.meta.glob('./coordinators/*.js', { eager: true });
    const coordinators = Object.fromEntries(
        Object.entries(coordinatorModules).map(([path, module]) => [
            path.replace('./coordinators/', '').replace('Coordinator.js', ''),
            module
        ])
    );

    if (typeof window !== 'undefined' && platformCapabilities.desktopIntegration) {
        window.$pinia = store;
        window.$coordinators = coordinators;
        window.$bridge = {
            updateIsGameRunning: runUpdateIsGameRunningFlow,
            updateIsHmdAfk: runUpdateIsHmdAfkFlow,
            addGameLogEvent
        };
    }

    onBeforeMount(() => {
        if (platformCapabilities.desktopIntegration) {
            store.updateLoop.updateLoop();
        }
    });

    onMounted(async () => {
        if (!platformCapabilities.desktopIntegration) {
            return;
        }
        if (await store.vrcx.waitForDatabaseInit()) {
            if (runtimeCapabilities.serverMode) {
                startServerAuthControl(store);
                try {
                    await startServerProjectionPublisher(/** @type {ReturnType<typeof createGlobalStores>} */ (store));
                } catch (error) {
                    console.error('Failed to start server projection publisher', error);
                }
            }
            getGameLogTable();
            await store.auth.migrateStoredUsers();
            store.auth.autoLoginAfterMounted();
            if (runtimeCapabilities.desktopIntegration) {
                store.vrcx.checkAutoBackupRestoreVrcRegistry();
            }
        }

        if (runtimeCapabilities.desktopIntegration) {
            runCheckVRChatDebugLoggingFlow();
        }
    });
</script>
