<template>
    <div
        class="shrink-0 h-[22px] flex items-center bg-sidebar border-t border-border text-xs select-none overflow-hidden"
        style="font-family: var(--font-mono-cjk)"
        @contextmenu.prevent>
        <ContextMenu>
            <ContextMenuTrigger as-child>
                <div class="flex items-center w-full h-full px-2">
                    <!-- Left section -->
                    <div
                        class="flex items-center flex-1 min-w-0 overflow-hidden [&>*:first-child]:pl-0.5"
                        style="
                            mask-image: linear-gradient(to right, black calc(100% - 20px), transparent 100%);
                            -webkit-mask-image: linear-gradient(to right, black calc(100% - 20px), transparent 100%);
                        ">
                        <TooltipWrapper
                            v-if="platformCapabilities.serverSession"
                            :content="webVrchatTooltip"
                            side="top">
                            <button
                                type="button"
                                :aria-disabled="webSession.state.workerConnection !== 'available'"
                                class="flex items-center gap-1 px-2 h-[22px] whitespace-nowrap border-r border-border cursor-pointer hover:bg-accent"
                                @click="handleWebVrchatClick">
                                <span class="inline-block size-2 rounded-full shrink-0" :class="webVrchatStatusDot" />
                                <span class="text-foreground text-[11px]">VRChat</span>
                                <span class="hidden sm:inline text-[10px] text-muted-foreground">{{
                                    webVrchatStatusLabel
                                }}</span>
                            </button>
                        </TooltipWrapper>

                        <TooltipWrapper v-if="platformCapabilities.serverSession" :content="webSyncTooltip" side="top">
                            <div
                                data-testid="web-sync-status"
                                :data-stale="String(webSyncView.stale)"
                                :data-sync-state="webSyncView.state"
                                class="flex items-center gap-1 px-2 h-[22px] whitespace-nowrap border-r border-border text-[10px]"
                                :aria-label="webSyncTooltip">
                                <span
                                    class="inline-block size-2 rounded-full shrink-0"
                                    :class="statusDot(webSyncView.tone)" />
                                <span class="hidden sm:inline">{{ t(webSyncView.labelKey) }}</span>
                                <span class="sm:hidden">{{ t(webSyncView.compactLabelKey) }}</span>
                                <span class="text-muted-foreground">{{ t('web.sync.last_success_short') }}</span>
                                <time
                                    v-if="webSyncView.lastSuccessAt"
                                    :datetime="webSyncView.lastSuccessAt"
                                    data-testid="web-sync-last-success">
                                    {{ webSyncLastTime }}
                                </time>
                                <span v-else>{{ t('web.sync.never') }}</span>
                                <span
                                    class="ml-1 inline-block size-1.5 rounded-full shrink-0"
                                    :class="statusDot(webPipelineView.tone)" />
                                <span :data-connection-state="webPipelineView.state" data-testid="web-pipeline-status">
                                    <span class="hidden sm:inline">Pipeline {{ t(webPipelineView.labelKey) }}</span>
                                    <span class="sm:hidden">P</span>
                                </span>
                                <span
                                    class="ml-1 inline-block size-1.5 rounded-full shrink-0"
                                    :class="statusDot(webEventsView.tone)" />
                                <span :data-connection-state="webEventsView.state" data-testid="web-events-status">
                                    <span class="hidden sm:inline"
                                        >{{ t('web.sync.events') }} {{ t(webEventsView.labelKey) }}</span
                                    >
                                    <span class="sm:hidden">UI</span>
                                </span>
                            </div>
                        </TooltipWrapper>

                        <TooltipWrapper
                            v-if="platformCapabilities.desktopIntegration && !isLinux && visibility.proxy"
                            :content="
                                vrcxStore.proxyServer
                                    ? `${t('status_bar.proxy')}: ${vrcxStore.proxyServer}`
                                    : t('status_bar.proxy')
                            "
                            side="top">
                            <div
                                class="flex items-center gap-1 px-2 h-[22px] whitespace-nowrap border-r border-border cursor-pointer hover:bg-accent"
                                @click="handleProxyClick">
                                <span
                                    class="inline-block size-2 rounded-full shrink-0"
                                    :class="vrcxStore.proxyServer ? 'bg-status-online' : 'bg-status-offline-alt'" />
                                <span class="text-foreground text-[11px]">{{
                                    vrcxStore.proxyServer || t('status_bar.proxy')
                                }}</span>
                            </div>
                        </TooltipWrapper>

                        <TooltipWrapper
                            v-if="platformCapabilities.localGameIntegration && !isMacOS && visibility.steamvr"
                            :content="
                                gameStore.isSteamVRRunning
                                    ? t('status_bar.steamvr_running')
                                    : t('status_bar.steamvr_stopped')
                            "
                            side="top">
                            <div class="flex items-center gap-1 px-2 h-[22px] whitespace-nowrap border-r border-border">
                                <span
                                    class="inline-block size-2 rounded-full shrink-0"
                                    :class="
                                        gameStore.isSteamVRRunning ? 'bg-status-online' : 'bg-status-offline-alt'
                                    " />
                                <span class="text-foreground text-[11px]">{{ t('status_bar.steamvr') }}</span>
                            </div>
                        </TooltipWrapper>

                        <HoverCard
                            v-if="platformCapabilities.localGameIntegration && !isMacOS && visibility.vrchat"
                            v-model:open="gameHoverOpen"
                            :open-delay="50"
                            :close-delay="50">
                            <HoverCardTrigger as-child>
                                <div
                                    class="flex items-center gap-1 px-2 h-[22px] whitespace-nowrap border-r border-border">
                                    <span
                                        class="inline-block size-2 rounded-full shrink-0"
                                        :class="
                                            gameStore.isGameRunning ? 'bg-status-online' : 'bg-status-offline-alt'
                                        " />
                                    <span class="text-foreground text-[11px]">{{ t('status_bar.game') }}</span>
                                    <span v-if="gameStore.isGameRunning" class="text-[10px] text-foreground">{{
                                        gameSessionText
                                    }}</span>
                                </div>
                            </HoverCardTrigger>
                            <HoverCardContent
                                v-if="gameStore.isGameRunning && userStore.currentUser.$online_for"
                                class="w-auto min-w-[160px] px-3 py-2"
                                side="top"
                                align="start"
                                :side-offset="4">
                                <div class="flex flex-col gap-1">
                                    <div class="flex items-center justify-between gap-3">
                                        <span class="text-[11px] text-muted-foreground">{{
                                            t('status_bar.game_started_at')
                                        }}</span>
                                        <span class="text-[11px] text-foreground">{{ gameStartedAtText }}</span>
                                    </div>
                                    <div class="flex items-center justify-between gap-3">
                                        <span class="text-[11px] text-muted-foreground">{{
                                            t('status_bar.game_session_duration')
                                        }}</span>
                                        <span class="text-[11px] text-foreground">{{ gameSessionDetailText }}</span>
                                    </div>
                                </div>
                            </HoverCardContent>
                            <HoverCardContent
                                v-else-if="!gameStore.isGameRunning && gameStore.lastSessionDurationMs > 0"
                                class="w-auto min-w-[160px] px-3 py-2"
                                side="top"
                                align="start"
                                :side-offset="4">
                                <div class="flex flex-col gap-1">
                                    <div class="flex items-center justify-between gap-3">
                                        <span class="text-[11px] text-muted-foreground">{{
                                            t('status_bar.game_last_session')
                                        }}</span>
                                        <span class="text-[11px] text-foreground">{{ lastSessionText }}</span>
                                    </div>
                                    <div class="flex items-center justify-between gap-3">
                                        <span class="text-[11px] text-muted-foreground">{{
                                            t('status_bar.game_last_offline')
                                        }}</span>
                                        <span class="text-[11px] text-foreground">{{ lastOfflineTimeText }}</span>
                                    </div>
                                </div>
                            </HoverCardContent>
                        </HoverCard>

                        <HoverCard
                            v-if="!platformCapabilities.serverSession && visibility.servers"
                            v-model:open="serversHoverOpen">
                            <HoverCardTrigger as-child>
                                <TooltipWrapper
                                    v-if="!vrcStatusStore.hasIssue"
                                    :content="t('status_bar.servers_ok')"
                                    side="top">
                                    <div
                                        class="flex items-center gap-1 px-2 h-[22px] whitespace-nowrap border-r border-border cursor-pointer hover:bg-accent"
                                        @click="vrcStatusStore.openStatusPage()">
                                        <span class="inline-block size-2 rounded-full shrink-0 bg-status-online" />
                                        <span class="text-foreground text-[11px]">{{ t('status_bar.servers') }}</span>
                                    </div>
                                </TooltipWrapper>
                                <div
                                    v-else
                                    class="flex items-center gap-1 px-2 h-[22px] whitespace-nowrap border-r border-border cursor-pointer hover:bg-accent"
                                    @click="vrcStatusStore.openStatusPage()">
                                    <span
                                        class="inline-block size-2 rounded-full shrink-0"
                                        :class="vrcStatusStore.isMajor ? 'bg-destructive' : 'bg-status-askme'" />
                                    <span class="text-foreground text-[11px]">{{ t('status_bar.servers') }}</span>
                                </div>
                            </HoverCardTrigger>
                            <HoverCardContent
                                v-if="vrcStatusStore.hasIssue"
                                class="w-[280px] px-3 py-2.5"
                                side="top"
                                align="start"
                                :side-offset="4">
                                <div class="flex items-center gap-1.5 mb-1.5">
                                    <span
                                        class="inline-block size-2 rounded-full shrink-0"
                                        :class="vrcStatusStore.isMajor ? 'bg-destructive' : 'bg-status-askme'" />
                                    <span class="font-semibold text-xs text-foreground">{{
                                        t('status_bar.servers_issue')
                                    }}</span>
                                </div>
                                <p class="text-[11px] text-muted-foreground m-0 leading-[1.4]">
                                    {{ vrcStatusStore.statusText }}
                                </p>
                            </HoverCardContent>
                        </HoverCard>

                        <TooltipWrapper
                            v-if="!platformCapabilities.serverSession && visibility.ws"
                            :content="wsTooltip"
                            side="top">
                            <div class="flex items-center gap-1 px-2 h-[22px] whitespace-nowrap border-r border-border">
                                <span
                                    class="inline-block size-2 rounded-full shrink-0"
                                    :class="wsState.connected ? 'bg-status-online' : 'bg-status-offline-alt'" />
                                <span class="text-foreground text-[11px]">WebSocket</span>
                                <canvas ref="wsCanvasRef" class="shrink-0 rounded-sm" />
                                <span class="text-[10px] text-foreground">{{
                                    t('status_bar.ws_avg_per_minute', { count: msgsPerMinuteAvg })
                                }}</span>
                            </div>
                        </TooltipWrapper>

                        <div
                            v-if="platformCapabilities.localGameIntegration && visibility.nowPlaying && nowPlaying.url"
                            class="flex items-center gap-1 px-2 h-[22px] whitespace-nowrap border-r border-border min-w-0 max-w-[400px]">
                            <i v-if="!isYouTubeNowPlaying" class="ri-play-fill text-[10px] shrink-0" />
                            <i v-if="isYouTubeNowPlaying" class="ri-youtube-fill text-[#FF0000] shrink-0 text-[12px]" />
                            <TooltipWrapper side="top" content-class="p-2">
                                <template #content>
                                    <div class="flex flex-col gap-1.5 max-w-[320px]">
                                        <img
                                            v-if="nowPlaying.thumbnailUrl"
                                            :src="nowPlaying.thumbnailUrl"
                                            class="w-full max-h-[180px] object-contain rounded"
                                            loading="lazy" />
                                        <span v-if="nowPlaying.name" class="whitespace-pre-wrap">{{
                                            nowPlaying.name
                                        }}</span>
                                        <span v-if="!nowPlaying.urlHidden" class="break-all text-muted-foreground">{{
                                            nowPlaying.url
                                        }}</span>
                                    </div>
                                </template>
                                <span
                                    class="text-[11px] text-foreground truncate cursor-pointer"
                                    @click="handleNowPlayingClick"
                                    >{{ nowPlaying.name || nowPlaying.url }}</span
                                >
                            </TooltipWrapper>
                            <template v-if="nowPlaying.playing">
                                <div
                                    class="shrink-0 h-[4px] rounded-full bg-muted overflow-hidden ml-1"
                                    style="width: 40px"
                                    :title="`${nowPlayingElapsedText} / ${nowPlayingLengthText}`">
                                    <div
                                        class="h-full rounded-full bg-foreground/60 transition-[width] duration-1000 ease-linear"
                                        :style="{ width: `${nowPlaying.percentage}%` }" />
                                </div>
                                <span class="text-[10px] tabular-nums shrink-0">
                                    {{ nowPlayingElapsedText }} / {{ nowPlayingLengthText }}
                                </span>
                            </template>
                        </div>
                    </div>

                    <!-- Right section -->
                    <div
                        class="flex items-center shrink-0 ml-auto [&>*:last-child]:border-r-0 [&>*:last-child]:pr-0.5"
                        :class="{ 'max-sm:hidden': platformCapabilities.serverSession }">
                        <template v-if="visibility.clocks">
                            <Popover
                                v-for="(clock, idx) in visibleClocks"
                                :key="idx"
                                v-model:open="clockPopoverOpen[idx]">
                                <PopoverTrigger as-child>
                                    <div
                                        class="flex items-center gap-1 px-2 h-[22px] whitespace-nowrap border-r border-border cursor-pointer hover:bg-accent">
                                        <span class="text-[10px] text-foreground">{{ formatClock(clock) }}</span>
                                    </div>
                                </PopoverTrigger>
                                <PopoverContent class="w-[280px]" side="top" align="center">
                                    <div class="flex flex-col gap-2 p-1">
                                        <label class="text-xs font-medium">{{ t('status_bar.timezone') }}</label>
                                        <Select
                                            :model-value="String(clock.offset)"
                                            @update:modelValue="(offset) => updateClockTimezone(idx, offset)">
                                            <SelectTrigger size="sm">
                                                <SelectValue :placeholder="t('status_bar.timezone')" />
                                            </SelectTrigger>
                                            <SelectContent class="max-h-60">
                                                <SelectGroup>
                                                    <SelectItem
                                                        v-for="opt in timezoneOptions"
                                                        :key="opt.value"
                                                        :value="String(opt.value)">
                                                        <div class="flex w-full items-center justify-end font-mono">
                                                            {{ opt.label }}
                                                        </div>
                                                    </SelectItem>
                                                </SelectGroup>
                                            </SelectContent>
                                        </Select>
                                    </div>
                                </PopoverContent>
                            </Popover>
                        </template>

                        <TooltipWrapper
                            v-if="platformCapabilities.nativeWindow && visibility.zoom"
                            :content="t('status_bar.zoom_tooltip')"
                            side="top"
                            :disabled="zoomEditing">
                            <div
                                class="flex items-center gap-1 px-2 h-[22px] whitespace-nowrap border-r border-border cursor-pointer hover:bg-accent"
                                @click="toggleZoomEdit">
                                <template v-if="zoomEditing">
                                    <span class="text-[10px] text-foreground">{{ t('status_bar.zoom') }}</span>
                                    <NumberField
                                        v-model="zoomLevel"
                                        :step="1"
                                        :format-options="{ maximumFractionDigits: 0 }"
                                        class="w-20"
                                        @click.stop>
                                        <NumberFieldContent>
                                            <NumberFieldDecrement />
                                            <NumberFieldInput
                                                ref="zoomInputRef"
                                                class="h-[18px] text-[11px] px-0.5 text-center"
                                                @blur="setZoomLevel" />
                                            <NumberFieldIncrement />
                                        </NumberFieldContent>
                                    </NumberField>
                                </template>
                                <template v-else>
                                    <span class="text-[10px] text-foreground">{{ t('status_bar.zoom') }}</span>
                                    <span class="text-[10px] text-foreground">{{ zoomLevel }}%</span>
                                </template>
                            </div>
                        </TooltipWrapper>

                        <TooltipWrapper v-if="visibility.uptime" :content="t('status_bar.app_uptime')" side="top">
                            <div class="flex items-center gap-1 px-2 h-[22px] whitespace-nowrap border-r border-border">
                                <span class="text-[10px] text-foreground">{{ t('status_bar.app_uptime_short') }}</span>
                                <span class="text-[10px] text-foreground">{{ appUptimeText }}</span>
                            </div>
                        </TooltipWrapper>
                    </div>
                </div>
            </ContextMenuTrigger>

            <ContextMenuContent>
                <ContextMenuCheckboxItem
                    v-if="platformCapabilities.localGameIntegration && !isMacOS"
                    :model-value="visibility.vrchat"
                    @select.prevent
                    @update:model-value="toggleVisibility('vrchat')">
                    {{ t('status_bar.game') }}
                </ContextMenuCheckboxItem>
                <ContextMenuCheckboxItem
                    v-if="!platformCapabilities.serverSession"
                    :model-value="visibility.servers"
                    @select.prevent
                    @update:model-value="toggleVisibility('servers')">
                    {{ t('status_bar.servers') }}
                </ContextMenuCheckboxItem>
                <ContextMenuCheckboxItem
                    v-if="platformCapabilities.localGameIntegration && !isMacOS"
                    :model-value="visibility.steamvr"
                    @select.prevent
                    @update:model-value="toggleVisibility('steamvr')">
                    {{ t('status_bar.steamvr') }}
                </ContextMenuCheckboxItem>
                <ContextMenuCheckboxItem
                    v-if="platformCapabilities.desktopIntegration"
                    :model-value="visibility.proxy"
                    @select.prevent
                    @update:model-value="toggleVisibility('proxy')">
                    {{ t('status_bar.proxy') }}
                </ContextMenuCheckboxItem>
                <ContextMenuCheckboxItem
                    v-if="!platformCapabilities.serverSession"
                    :model-value="visibility.ws"
                    @select.prevent
                    @update:model-value="toggleVisibility('ws')">
                    WebSocket
                </ContextMenuCheckboxItem>
                <ContextMenuCheckboxItem
                    v-if="platformCapabilities.localGameIntegration"
                    :model-value="visibility.nowPlaying"
                    @select.prevent
                    @update:model-value="toggleVisibility('nowPlaying')">
                    {{ t('status_bar.now_playing') }}
                </ContextMenuCheckboxItem>
                <ContextMenuCheckboxItem
                    :model-value="visibility.uptime"
                    @select.prevent
                    @update:model-value="toggleVisibility('uptime')">
                    {{ t('status_bar.app_uptime_short') }}
                </ContextMenuCheckboxItem>
                <ContextMenuCheckboxItem
                    v-if="platformCapabilities.nativeWindow && !isMacOS"
                    :model-value="visibility.zoom"
                    @select.prevent
                    @update:model-value="toggleVisibility('zoom')">
                    {{ t('status_bar.zoom') }}
                </ContextMenuCheckboxItem>
                <ContextMenuSeparator />
                <ContextMenuSub>
                    <ContextMenuSubTrigger>{{ t('status_bar.clocks') }}</ContextMenuSubTrigger>
                    <ContextMenuSubContent>
                        <ContextMenuCheckboxItem
                            :model-value="clockCount === 0"
                            @select.prevent
                            @update:model-value="setClockCount('0')">
                            {{ t('status_bar.clocks_none') }}
                        </ContextMenuCheckboxItem>
                        <ContextMenuCheckboxItem
                            :model-value="clockCount === 1"
                            @select.prevent
                            @update:model-value="setClockCount('1')">
                            1 {{ t('status_bar.clocks_label') }}
                        </ContextMenuCheckboxItem>
                        <ContextMenuCheckboxItem
                            :model-value="clockCount === 2"
                            @select.prevent
                            @update:model-value="setClockCount('2')">
                            2 {{ t('status_bar.clocks_label') }}
                        </ContextMenuCheckboxItem>
                        <ContextMenuCheckboxItem
                            :model-value="clockCount === 3"
                            @select.prevent
                            @update:model-value="setClockCount('3')">
                            3 {{ t('status_bar.clocks_label') }}
                        </ContextMenuCheckboxItem>
                    </ContextMenuSubContent>
                </ContextMenuSub>
            </ContextMenuContent>
        </ContextMenu>
    </div>
</template>

<script setup>
    import {
        ContextMenu,
        ContextMenuCheckboxItem,
        ContextMenuContent,
        ContextMenuSeparator,
        ContextMenuSub,
        ContextMenuSubContent,
        ContextMenuSubTrigger,
        ContextMenuTrigger
    } from '@/components/ui/context-menu';
    import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue';
    import { storeToRefs } from 'pinia';
    import { Select, SelectContent, SelectGroup, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
    import {
        NumberField,
        NumberFieldContent,
        NumberFieldDecrement,
        NumberFieldIncrement,
        NumberFieldInput
    } from '@/components/ui/number-field';
    import {
        useGameLogStore,
        useGameStore,
        useGeneralSettingsStore,
        useModalStore,
        useUserStore,
        useVrcStatusStore,
        useVrcxStore
    } from '@/stores';
    import { HoverCard, HoverCardContent, HoverCardTrigger } from '@/components/ui/hover-card';
    import { formatSeconds, timeToText } from '@/shared/utils';
    import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover';
    import { useIntervalFn, useNow } from '@vueuse/core';
    import { TooltipWrapper } from '@/components/ui/tooltip';
    import { useI18n } from 'vue-i18n';
    import { useRouter } from 'vue-router';
    import { openExternalLink } from '@/shared/utils/appActions';
    import { wsState } from '@/services/websocket';
    import { platformCapabilities } from '@/web/platform';
    import { webSession } from '@/web/session';
    import { connectionStatusPresentation, syncStatusPresentation } from '@/web/syncStatus';

    import dayjs from 'dayjs';
    import timezone from 'dayjs/plugin/timezone';
    import utc from 'dayjs/plugin/utc';

    import {
        defaultVisibility,
        formatAppUptime,
        formatUtcHour,
        normalizeClock,
        normalizeUtcHour,
        parseClockOffset
    } from './statusBarUtils';

    import configRepository from '../services/config';

    dayjs.extend(utc);
    dayjs.extend(timezone);

    const { t } = useI18n();
    const router = useRouter();
    const modalStore = useModalStore();

    const webVrchatStatusLabel = computed(() => {
        if (webSession.state.workerFailureReason === 'database_initialization_failed')
            return t('web.vrchat.status.database_initialization_failed');
        if (webSession.state.workerConnection === 'unavailable') return t('web.vrchat.status.worker_unavailable');
        if (webSession.state.workerConnection === 'check_failed') return t('web.vrchat.status.check_failed');
        if (webSession.state.workerConnection === 'checking') return t('web.vrchat.status.unknown');
        const statusKey =
            {
                authenticated: 'authenticated',
                authenticating: 'authenticating',
                two_factor_required: 'two_factor_required',
                requires_authentication: 'requires_authentication'
            }[webSession.state.vrchatAuth.status] || 'unknown';
        return t(`web.vrchat.status.${statusKey}`);
    });
    const webVrchatStatusDot = computed(() => {
        if (webSession.state.workerFailureReason === 'database_initialization_failed') return 'bg-destructive';
        if (webSession.state.workerConnection !== 'available') return 'bg-status-askme';
        if (webSession.state.vrchatAuth.status === 'authenticated') return 'bg-status-online';
        if (webSession.state.vrchatAuth.status === 'authenticating') return 'bg-status-askme';
        return 'bg-status-offline-alt';
    });
    const webVrchatTooltip = computed(() =>
        webSession.state.workerFailureReason === 'database_initialization_failed'
            ? t('web.vrchat.database_initialization_failed_description')
            : webSession.state.workerConnection === 'check_failed'
              ? t('web.vrchat.worker_check_failed_description')
              : webSession.state.workerConnection === 'unavailable'
                ? t('web.vrchat.worker_unavailable_description')
                : t('web.vrchat.status_tooltip', { status: webVrchatStatusLabel.value })
    );
    const webSyncView = computed(() =>
        syncStatusPresentation(webSession.state.status, webSession.state.workerConnection)
    );
    const webPipelineView = computed(() =>
        connectionStatusPresentation(
            webSession.state.workerConnection === 'unavailable' ? undefined : webSession.state.status?.pipeline?.state
        )
    );
    const webEventsView = computed(() => connectionStatusPresentation(webSession.state.eventsConnection));
    const webSyncLastTime = computed(() => dayjs(webSyncView.value.lastSuccessAt).format('HH:mm'));
    const webSyncTooltip = computed(() =>
        [
            t(webSyncView.value.labelKey),
            t('web.sync.last_success', {
                time: webSyncView.value.lastSuccessAt
                    ? dayjs(webSyncView.value.lastSuccessAt).format('YYYY-MM-DD HH:mm:ss Z')
                    : t('web.sync.never')
            }),
            `Pipeline: ${t(webPipelineView.value.labelKey)}`,
            `${t('web.sync.events')}: ${t(webEventsView.value.labelKey)}`
        ].join('\n')
    );

    function statusDot(tone) {
        return {
            success: 'bg-status-online',
            warning: 'bg-status-askme',
            error: 'bg-destructive',
            neutral: 'bg-status-offline-alt'
        }[tone];
    }

    async function handleWebVrchatClick() {
        if (webSession.state.workerConnection !== 'available') return;
        const reauthRoute = {
            name: 'login',
            query: { reauth: '1', redirect: router.currentRoute.value.fullPath }
        };
        if (webSession.state.vrchatAuth.status !== 'authenticated') {
            await router.push(reauthRoute);
            return;
        }

        const { ok } = await modalStore.confirm({
            title: t('web.vrchat.reauth_title'),
            description: t('web.vrchat.reauth_description')
        });
        if (!ok) return;

        try {
            await webSession.logoutVrchat();
            await router.replace(reauthRoute);
        } catch {
            // The shared Web session state exposes a safe error to the login screen.
        }
    }

    const isMacOS = computed(() => navigator.platform.includes('Mac'));
    const isLinux = computed(() => LINUX);

    const gameStore = useGameStore();
    const gameLogStore = useGameLogStore();
    const userStore = useUserStore();
    const vrcxStore = useVrcxStore();
    const vrcStatusStore = useVrcStatusStore();
    const generalSettingsStore = useGeneralSettingsStore();

    const { nowPlaying } = storeToRefs(gameLogStore);

    const nowPlayingElapsedText = computed(() => {
        if (!nowPlaying.value.playing) return '';
        return formatSeconds(Math.floor(nowPlaying.value.elapsed));
    });

    const nowPlayingLengthText = computed(() => {
        if (!nowPlaying.value.length) return '';
        return formatSeconds(nowPlaying.value.length);
    });

    function handleNowPlayingClick() {
        const url = nowPlaying.value.url;
        if (url && !nowPlaying.value.urlHidden) {
            openExternalLink(url);
        }
    }

    const isYouTubeNowPlaying = computed(() => /youtu\.?be/i.test(nowPlaying.value.url));

    // --- Game session timer ---
    const gameHoverOpen = ref(false);

    const gameSessionText = computed(() => {
        if (!gameStore.isGameRunning || !userStore.currentUser.$online_for) return '';
        const elapsed = now.value.getTime() - userStore.currentUser.$online_for;
        return elapsed > 0 ? timeToText(elapsed) : '';
    });

    const gameStartedAtText = computed(() => {
        if (!userStore.currentUser.$online_for) return '-';
        return dayjs(userStore.currentUser.$online_for).format('MM/DD HH:mm');
    });

    const gameSessionDetailText = computed(() => {
        if (!gameStore.isGameRunning || !userStore.currentUser.$online_for) return '-';
        const elapsed = now.value.getTime() - userStore.currentUser.$online_for;
        return elapsed > 0 ? timeToText(elapsed, true) : '-';
    });

    const lastSessionText = computed(() => {
        if (gameStore.lastSessionDurationMs <= 0) return '-';
        return timeToText(gameStore.lastSessionDurationMs);
    });

    const lastOfflineTimeText = computed(() => {
        if (gameStore.lastOfflineAt <= 0) return '-';
        return dayjs(gameStore.lastOfflineAt).format('MM/DD HH:mm');
    });

    // --- Servers status HoverCard ---
    const serversHoverOpen = ref(false);
    let serversHoverTimer = null;

    watch(
        () => vrcStatusStore.hasIssue,
        (hasIssue) => {
            if (hasIssue && visibility.servers) {
                serversHoverOpen.value = true;
                clearTimeout(serversHoverTimer);
                serversHoverTimer = setTimeout(() => {
                    serversHoverOpen.value = false;
                }, 5000);
            } else {
                serversHoverOpen.value = false;
                clearTimeout(serversHoverTimer);
            }
        }
    );

    const VISIBILITY_KEY = 'VRCX_statusBarVisibility';

    const visibility = reactive({ ...defaultVisibility });

    /**
     * @param key
     */
    function toggleVisibility(key) {
        visibility[key] = !visibility[key];
        configRepository.setString(VISIBILITY_KEY, JSON.stringify(visibility));
    }

    // --- WebSocket message rate + sparkline ---

    const GRAPH_POINTS = 60;
    const WS_CANVAS_WIDTH = 48;
    const WS_CANVAS_HEIGHT = 12;
    const msgHistory = ref(new Array(GRAPH_POINTS).fill(0));
    const msgsLastMinute = ref(0);
    let lastMsgCount = wsState.messageCount;

    const wsCanvasRef = ref(null);
    const now = useNow({ scheduler: (cb) => useIntervalFn(cb, 1000) });

    useIntervalFn(() => {
        const delta = wsState.messageCount - lastMsgCount;
        lastMsgCount = wsState.messageCount;

        const arr = msgHistory.value;
        arr.shift();
        arr.push(delta);
        msgHistory.value = arr;

        // Sum of messages in the last 60 seconds
        msgsLastMinute.value = arr.reduce((a, b) => a + b, 0);

        drawSparkline();
    }, 1000);

    const msgsPerMinuteAvg = computed(() => Math.round(msgsLastMinute.value));

    function drawSparkline() {
        const canvas = wsCanvasRef.value;
        if (!canvas) return;
        const ctx = canvas.getContext('2d');
        if (!ctx) return;

        const dpr = window.devicePixelRatio || 1;
        canvas.width = Math.floor(WS_CANVAS_WIDTH * dpr);
        canvas.height = Math.floor(WS_CANVAS_HEIGHT * dpr);
        canvas.style.width = `${WS_CANVAS_WIDTH}px`;
        canvas.style.height = `${WS_CANVAS_HEIGHT}px`;
        ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
        const w = WS_CANVAS_WIDTH;
        const h = WS_CANVAS_HEIGHT;
        const data = msgHistory.value;

        const fg = resolveCssColor('--foreground', '#cfd3dc');
        ctx.clearRect(0, 0, w, h);

        const max = Math.max(...data, 1);
        const step = w / (data.length - 1);

        // Only draw the sparkline stroke (no background, grid, or fill area)
        ctx.globalAlpha = 0.75;
        ctx.strokeStyle = fg;
        ctx.lineWidth = 1;
        ctx.beginPath();
        for (let i = 0; i < data.length; i++) {
            const x = i * step;
            const y = h - (data[i] / max) * (h - 2);
            if (i === 0) {
                ctx.moveTo(x, y);
            } else {
                ctx.lineTo(x, y);
            }
        }
        ctx.stroke();
        ctx.globalAlpha = 1;
    }

    /**
     * @param variableName
     * @param fallback
     */
    function resolveCssColor(variableName, fallback) {
        const value = getComputedStyle(document.documentElement).getPropertyValue(variableName).trim();
        if (!value) return fallback;
        if (value.startsWith('#') || value.startsWith('rgb') || value.startsWith('hsl') || value.startsWith('oklch')) {
            return value;
        }
        return `hsl(${value})`;
    }

    const wsTooltip = computed(() => {
        const state = wsState.connected ? t('status_bar.ws_connected') : t('status_bar.ws_disconnected');
        return `WebSocket: ${state}`;
    });

    const appUptimeText = computed(() => {
        const elapsedSeconds = Math.floor((now.value.getTime() - vrcxStore.appStartAt) / 1000);
        return formatAppUptime(elapsedSeconds);
    });

    const CLOCKS_KEY = 'VRCX_statusBarClocks';
    const CLOCK_COUNT_KEY = 'VRCX_statusBarClockCount';
    const localOffset = normalizeUtcHour(dayjs().utcOffset() / 60);
    const defaultClocks = [{ offset: localOffset }, { offset: 0 }, { offset: localOffset < 0 ? 9 : -5 }];

    const clocks = ref(defaultClocks.map((c) => ({ ...c })));
    const clockCount = ref(2);
    const clockPopoverOpen = reactive([false, false, false]);

    const visibleClocks = computed(() => clocks.value.slice(0, clockCount.value));

    function saveClocks() {
        configRepository.setString(CLOCKS_KEY, JSON.stringify(clocks.value));
    }

    /**
     * @param val
     */
    function setClockCount(val) {
        clockCount.value = Number(val);
        configRepository.setString(CLOCK_COUNT_KEY, String(clockCount.value));
        if (clockCount.value > 0) {
            visibility.clocks = true;
            configRepository.setString(VISIBILITY_KEY, JSON.stringify(visibility));
        }
    }

    /**
     * @param clock
     * @returns {string}
     */
    function formatClock(clock) {
        try {
            const current = dayjs(now.value).utcOffset(normalizeUtcHour(clock.offset) * 60);
            const time = current.format('HH:mm');
            return `${time} ${formatUtcHour(clock.offset)}`;
        } catch {
            return '??:?? UTC+0';
        }
    }

    /**
     * @param idx
     * @param offsetValue
     */
    function updateClockTimezone(idx, offsetValue) {
        clocks.value[idx].offset = parseClockOffset(offsetValue);
        saveClocks();
        clockPopoverOpen[idx] = false;
    }

    const timezoneOptions = computed(() => {
        return Array.from({ length: 27 }, (_, i) => {
            const value = i - 12;
            return { value, label: formatUtcHour(value) };
        });
    });

    onMounted(async () => {
        const [savedVis, savedClocks, savedClockCount] = await Promise.all([
            configRepository.getString(VISIBILITY_KEY, null),
            configRepository.getString(CLOCKS_KEY, null),
            configRepository.getString(CLOCK_COUNT_KEY, null)
        ]);
        if (savedVis) {
            try {
                Object.assign(visibility, JSON.parse(savedVis));
            } catch {
                // ignore
            }
        }
        if (savedClocks) {
            try {
                const parsed = JSON.parse(savedClocks);
                if (Array.isArray(parsed) && parsed.length === 3) {
                    clocks.value = parsed.map(normalizeClock);
                }
            } catch {
                // ignore
            }
        }
        if (savedClockCount !== null) {
            const n = Number(savedClockCount);
            if (n >= 0 && n <= 3) clockCount.value = n;
        }

        drawSparkline();
    });

    const zoomLevel = ref(100);
    const zoomEditing = ref(false);
    const zoomInputRef = ref(null);
    let cleanupWheel = null;

    onBeforeUnmount(() => {
        clearTimeout(serversHoverTimer);
        if (cleanupWheel) {
            cleanupWheel();
        }
    });

    if (platformCapabilities.nativeWindow) {
        initGetZoomLevel();
    }

    watch(
        () => visibility.ws,
        (enabled) => {
            if (enabled) {
                nextTick(() => {
                    drawSparkline();
                });
            }
        }
    );

    async function initGetZoomLevel() {
        const handleWheel = (event) => {
            if (event.ctrlKey) {
                getZoomLevel();
            }
        };
        window.addEventListener('wheel', handleWheel);
        cleanupWheel = () => {
            window.removeEventListener('wheel', handleWheel);
        };
        getZoomLevel();
    }

    async function getZoomLevel() {
        zoomLevel.value = Math.round(((await AppApi.GetZoom()) + 10) * 10);
    }

    function setZoomLevel() {
        zoomEditing.value = false;
        AppApi.SetZoom(zoomLevel.value / 10 - 10);
    }

    async function toggleZoomEdit() {
        if (zoomEditing.value) {
            zoomEditing.value = false;
            return;
        }
        await getZoomLevel();
        zoomEditing.value = true;
        await nextTick();
        zoomInputRef.value?.$el?.focus?.();
    }

    function handleProxyClick() {
        generalSettingsStore.promptProxySettings();
    }
</script>
