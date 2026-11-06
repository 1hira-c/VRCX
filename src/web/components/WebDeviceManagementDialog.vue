<template>
    <Dialog :open="open" @update:open="(value) => emit('update:open', value)">
        <DialogContent class="sm:max-w-2xl">
            <DialogHeader>
                <DialogTitle>{{ t('web.devices.title') }}</DialogTitle>
                <DialogDescription>{{ t('web.devices.description') }}</DialogDescription>
            </DialogHeader>

            <section class="grid gap-3 rounded-lg border p-4">
                <div class="flex flex-wrap items-center justify-between gap-2">
                    <div>
                        <h3 class="text-sm font-medium">{{ t('web.devices.add_title') }}</h3>
                        <p class="text-xs text-muted-foreground">{{ t('web.devices.add_description') }}</p>
                    </div>
                    <Button variant="secondary" size="sm" :disabled="busy" @click="handleCreatePairing">
                        <QrCode class="size-4" />
                        {{ t('web.devices.issue_pairing') }}
                    </Button>
                </div>

                <div v-if="pairing" class="grid gap-4 rounded-md bg-muted/40 p-3 sm:grid-cols-[184px_1fr]">
                    <img
                        :src="pairing.qrImage"
                        width="184"
                        height="184"
                        :alt="t('web.devices.pairing_qr_alt')"
                        class="rounded bg-white" />
                    <div class="flex min-w-0 flex-col justify-center gap-2">
                        <span class="text-xs text-muted-foreground">{{ t('web.devices.pairing_code') }}</span>
                        <code
                            class="select-all break-all rounded border bg-background px-3 py-2 text-base font-semibold">
                            {{ pairing.code }}
                        </code>
                        <span class="text-xs text-muted-foreground">{{
                            t('web.devices.expires_at', { time: formatDeviceTime(pairing.expiresAt) })
                        }}</span>
                        <p class="text-xs text-muted-foreground">{{ t('web.devices.pairing_warning') }}</p>
                    </div>
                </div>
            </section>

            <Separator />

            <section class="grid gap-3">
                <div class="flex items-center justify-between gap-2">
                    <div>
                        <h3 class="text-sm font-medium">{{ t('web.devices.connected_title') }}</h3>
                        <p class="text-xs text-muted-foreground">{{ t('web.devices.connected_description') }}</p>
                    </div>
                    <Button
                        variant="ghost"
                        size="icon-sm"
                        :disabled="busy"
                        :aria-label="t('web.devices.refresh')"
                        :title="t('web.devices.refresh')"
                        @click="refreshDevices">
                        <RefreshCw class="size-4" :class="{ 'animate-spin': loadingDevices }" />
                    </Button>
                </div>

                <div
                    v-if="!devices.length && !loadingDevices"
                    class="rounded-md border border-dashed p-4 text-sm text-muted-foreground">
                    {{ t('web.devices.empty') }}
                </div>
                <div v-else class="max-h-64 space-y-2 overflow-y-auto pr-1">
                    <div
                        v-for="device in devices"
                        :key="device.id"
                        class="flex items-center gap-3 rounded-md border px-3 py-2">
                        <Smartphone class="size-4 shrink-0 text-muted-foreground" />
                        <div class="min-w-0 flex-1">
                            <div class="flex flex-wrap items-center gap-2">
                                <span class="truncate text-sm font-medium">{{
                                    device.deviceName || t('web.devices.unnamed')
                                }}</span>
                                <Badge v-if="isCurrentDevice(device)" variant="secondary">{{
                                    t('web.devices.current')
                                }}</Badge>
                                <Badge v-if="device.revokedAt" variant="outline">{{ t('web.devices.revoked') }}</Badge>
                            </div>
                            <p class="mt-0.5 text-xs text-muted-foreground">
                                {{
                                    t('web.devices.last_seen_at', {
                                        time: formatDeviceTime(device.lastSeenAt || device.issuedAt)
                                    })
                                }}
                                <span v-if="device.expiresAt">
                                    ·
                                    {{
                                        t('web.devices.expires_at', { time: formatDeviceTime(device.expiresAt) })
                                    }}</span
                                >
                            </p>
                        </div>
                        <Button
                            variant="ghost"
                            size="sm"
                            class="text-destructive hover:text-destructive"
                            :disabled="busy || !!device.revokedAt"
                            @click="handleRevoke(device)">
                            {{ t('web.devices.revoke') }}
                        </Button>
                    </div>
                </div>
            </section>

            <p v-if="localError || state.error" class="text-sm text-destructive">
                {{ localError || state.error }}
            </p>

            <DialogFooter class="border-t pt-4 sm:justify-between">
                <div class="mr-auto max-w-md text-left">
                    <p class="text-sm font-medium">{{ t('web.devices.admin_session_title') }}</p>
                    <p class="text-xs text-muted-foreground">{{ t('web.devices.admin_session_description') }}</p>
                </div>
                <Button variant="destructive" :disabled="busy" @click="handleServerLogout">
                    {{ t('web.devices.admin_logout') }}
                </Button>
            </DialogFooter>
        </DialogContent>
    </Dialog>
</template>

<script setup>
    import { computed, ref, watch } from 'vue';
    import { QrCode, RefreshCw, Smartphone } from 'lucide-vue-next';
    import QRCode from 'qrcode';
    import { useI18n } from 'vue-i18n';

    import { Badge } from '@/components/ui/badge';
    import { Button } from '@/components/ui/button';
    import {
        Dialog,
        DialogContent,
        DialogDescription,
        DialogFooter,
        DialogHeader,
        DialogTitle
    } from '@/components/ui/dialog';
    import { Separator } from '@/components/ui/separator';
    import { useModalStore } from '@/stores';

    import { errorMessage, formatTime } from '../format';
    import { webSession } from '../session';

    const props = defineProps({
        open: {
            type: Boolean,
            default: false
        }
    });
    const emit = defineEmits(['update:open']);

    const { t } = useI18n();
    const modalStore = useModalStore();
    const state = webSession.state;
    const pairing = ref(null);
    const localError = ref('');
    const loadingDevices = ref(false);
    const busy = computed(() => state.pendingRequests > 0 || loadingDevices.value);
    const devices = computed(() =>
        Array.isArray(state.data.devices) ? state.data.devices.filter((device) => device?.id) : []
    );

    watch(
        () => props.open,
        (isOpen) => {
            if (!isOpen) {
                pairing.value = null;
                localError.value = '';
                return;
            }
            void refreshDevices();
        }
    );

    function isCurrentDevice(device) {
        return Boolean(device?.id && device.id === state.auth.device?.id);
    }

    function formatDeviceTime(value) {
        return formatTime(value, t('web.devices.no_record'));
    }

    async function refreshDevices() {
        localError.value = '';
        loadingDevices.value = true;
        try {
            await webSession.loadDevices();
        } catch (error) {
            localError.value = errorMessage(error);
        } finally {
            loadingDevices.value = false;
        }
    }

    async function handleCreatePairing() {
        localError.value = '';
        try {
            const result = await webSession.createPairing();
            const code = typeof result?.code === 'string' ? result.code : '';
            const pairingUrl = new URL(String(result?.url || ''), window.location.origin);
            const hashQuery = pairingUrl.hash.indexOf('?');
            const hashParams = new URLSearchParams(hashQuery >= 0 ? pairingUrl.hash.slice(hashQuery + 1) : '');
            if (
                pairingUrl.origin !== window.location.origin ||
                pairingUrl.search ||
                !code ||
                hashParams.get('code') !== code
            ) {
                throw new Error(t('web.devices.invalid_pairing_url'));
            }
            pairing.value = {
                code,
                expiresAt: result.expiresAt || null,
                qrImage: await QRCode.toDataURL(pairingUrl.href, { width: 184, margin: 1 })
            };
        } catch (error) {
            localError.value = errorMessage(error);
        }
    }

    async function handleRevoke(device) {
        const current = isCurrentDevice(device);
        const { ok } = await modalStore.confirm({
            title: t(current ? 'web.devices.revoke_current_title' : 'web.devices.revoke_title'),
            description: current
                ? t('web.devices.revoke_current_description')
                : t('web.devices.revoke_description', { name: device.deviceName || t('web.devices.unnamed') }),
            destructive: true
        });
        if (!ok) return;

        localError.value = '';
        try {
            await webSession.revokeDevice(device.id);
            if (!state.auth.authenticated) {
                emit('update:open', false);
            }
        } catch (error) {
            localError.value = errorMessage(error);
        }
    }

    async function handleServerLogout() {
        const { ok } = await modalStore.confirm({
            title: t('web.devices.admin_logout_title'),
            description: t('web.devices.admin_logout_description'),
            destructive: true
        });
        if (!ok) return;

        localError.value = '';
        try {
            await webSession.logout();
            emit('update:open', false);
        } catch (error) {
            localError.value = errorMessage(error);
        }
    }
</script>
