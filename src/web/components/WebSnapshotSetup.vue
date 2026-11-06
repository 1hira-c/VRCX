<template>
    <main class="web-auth-page">
        <Card class="web-auth-card web-import-card">
            <CardHeader>
                <div class="web-auth-kicker">
                    <Database :size="18" aria-hidden="true" />
                    初回データ移行
                </div>
                <CardTitle class="text-2xl">既存のVRCX履歴を引き継ぐ</CardTitle>
                <CardDescription>
                    停止したデスクトップVRCXから取得したSQLiteスナップショットを、一度だけサーバー専用DBへ取り込みます。
                </CardDescription>
            </CardHeader>

            <CardContent class="web-auth-form">
                <Alert v-if="result" class="border-primary/30 bg-primary/5">
                    <AlertTitle>インポートが完了しました</AlertTitle>
                    <AlertDescription>
                        {{ result.importedTableCount }} テーブル、{{ result.importedRowCount }} 行を確認しました。
                        <template v-if="result.alreadyImported">同じスナップショットは既に取り込み済みです。</template>
                    </AlertDescription>
                </Alert>

                <template v-else>
                    <Alert class="border-primary/30 bg-primary/5">
                        <AlertTitle>アップロード前にデスクトップVRCXを完全に終了してください</AlertTitle>
                        <AlertDescription>
                            メインDBと同じ時点の <code>-wal</code> /
                            <code>-shm</code> が存在する場合は一緒に選択してください。
                            Cookie、保存済みログイン情報、プロキシ、APIキーは移行されません。
                        </AlertDescription>
                    </Alert>

                    <label class="web-form-field">
                        <span>VRCX SQLiteデータベース（必須）</span>
                        <input
                            class="web-native-file"
                            type="file"
                            accept=".sqlite,.sqlite3,.db,application/vnd.sqlite3,application/octet-stream"
                            required
                            @change="database = selectedFile($event)" />
                    </label>
                    <label class="web-form-field">
                        <span>同じスナップショットのWAL（存在する場合）</span>
                        <input class="web-native-file" type="file" @change="wal = selectedFile($event)" />
                    </label>
                    <label class="web-form-field">
                        <span>同じスナップショットのSHM（存在する場合）</span>
                        <input class="web-native-file" type="file" @change="shm = selectedFile($event)" />
                    </label>

                    <p v-if="error" class="text-sm text-destructive">{{ error }}</p>
                    <div class="web-import-actions">
                        <Button type="button" :disabled="busy || !database" @click="submit">
                            <LoaderCircle v-if="busy" class="animate-spin" :size="16" aria-hidden="true" />
                            スナップショットを取り込む
                        </Button>
                        <Button type="button" variant="ghost" :disabled="busy" @click="$emit('continue')">
                            今は移行せずVRCXへ進む
                        </Button>
                    </div>
                </template>

                <Button v-if="result" class="w-full" type="button" @click="$emit('continue')"> VRCXへ進む </Button>
            </CardContent>
        </Card>
    </main>
</template>

<script setup>
    import { ref } from 'vue';
    import { Database, LoaderCircle } from 'lucide-vue-next';

    import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert';
    import { Button } from '@/components/ui/button';
    import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';

    import { webApi } from '../api';
    import { webSession } from '../session';

    defineEmits(['continue']);

    const database = ref(null);
    const wal = ref(null);
    const shm = ref(null);
    const busy = ref(false);
    const error = ref('');
    const result = ref(null);

    function selectedFile(event) {
        return event.target?.files?.[0] || null;
    }

    async function submit() {
        if (!database.value || busy.value) return;
        busy.value = true;
        error.value = '';
        try {
            result.value = await webApi.importSnapshot(database.value, wal.value, shm.value);
            await webSession.refreshAll({ silent: true, keepError: true });
        } catch (exception) {
            error.value = importErrorMessage(exception);
        } finally {
            busy.value = false;
        }
    }

    function importErrorMessage(exception) {
        switch (exception?.body?.error) {
            case 'snapshot_upload_too_large':
                return 'スナップショットがサーバーのアップロード上限を超えています。';
            case 'snapshot_setup_closed':
                return 'サーバー側のVRCXデータが既に使われ始めているため、初回インポートはできません。';
            case 'snapshot_rejected':
                return '対応するVRCXスナップショットとして検証できませんでした。DBとWALの組み合わせを確認してください。';
            case 'invalid_snapshot_upload':
                return 'メインDB、WAL、SHMをそれぞれ正しい欄で選択してください。';
            default:
                return 'スナップショットを取り込めませんでした。しばらく待ってからもう一度試してください。';
        }
    }
</script>
