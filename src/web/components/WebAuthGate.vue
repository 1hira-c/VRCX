<template>
    <main class="web-auth-page">
        <Card class="web-auth-card">
            <CardHeader>
                <div class="web-auth-kicker">
                    <ShieldCheck :size="18" aria-hidden="true" />
                    プライベートネットワーク内限定
                </div>
                <CardTitle class="text-2xl">VRCX Web</CardTitle>
                <CardDescription v-if="!state.auth.bootstrapped">
                    常駐サーバーの最初の管理者を設定します。
                </CardDescription>
                <CardDescription v-else> 読み取り専用のVRCX Webへログインします。 </CardDescription>
            </CardHeader>

            <CardContent>
                <form v-if="!state.auth.bootstrapped" class="web-auth-form" @submit.prevent="submitBootstrap">
                    <input
                        class="sr-only"
                        name="username"
                        value="admin"
                        autocomplete="username"
                        tabindex="-1"
                        readonly
                        aria-hidden="true" />
                    <Alert class="border-primary/30 bg-primary/5">
                        <AlertTitle>初回確認コード</AlertTitle>
                        <AlertDescription>
                            このコードは、Web管理者を最初に作る人がサーバーを操作できることを確認するためだけに使います。
                            Docker Compose ではホスト上で
                            <code>cat deploy/secrets/vrcx_setup_token</code>
                            を実行し、Home Assistant
                            アドオンではアドオンのログを開いて、表示された内容を入力してください。
                            VRChatの認証情報でもDB暗号化鍵でもなく、管理者作成後は再利用できません。
                        </AlertDescription>
                    </Alert>

                    <label class="web-form-field">
                        <span>初回確認コード</span>
                        <Input v-model="setupToken" type="password" autocomplete="one-time-code" required />
                    </label>
                    <label class="web-form-field">
                        <span>管理者パスワード</span>
                        <Input
                            v-model="password"
                            name="password"
                            type="password"
                            autocomplete="new-password"
                            minlength="12"
                            required />
                    </label>
                    <label class="web-form-field">
                        <span>管理者パスワード（確認）</span>
                        <Input
                            v-model="confirmPassword"
                            name="password-confirmation"
                            type="password"
                            autocomplete="new-password"
                            minlength="12"
                            required />
                    </label>
                    <label class="web-form-field">
                        <span>この端末の名前（任意）</span>
                        <Input
                            v-model="deviceName"
                            autocomplete="nickname"
                            maxlength="80"
                            placeholder="Desktop browser" />
                    </label>
                    <p v-if="localError || state.error" class="text-sm text-destructive">
                        {{ localError || state.error }}
                    </p>
                    <Button class="w-full" type="submit" :disabled="busy">
                        <LoaderCircle v-if="busy" class="animate-spin" :size="16" aria-hidden="true" />
                        管理者を設定して続ける
                    </Button>
                </form>

                <form v-else class="web-auth-form" @submit.prevent="submitLogin">
                    <input
                        class="sr-only"
                        name="username"
                        value="admin"
                        autocomplete="username"
                        tabindex="-1"
                        readonly
                        aria-hidden="true" />
                    <label class="web-form-field">
                        <span>管理者パスワード</span>
                        <Input
                            v-model="password"
                            name="password"
                            type="password"
                            autocomplete="current-password"
                            required />
                    </label>
                    <label class="web-form-field">
                        <span>この端末の名前（任意）</span>
                        <Input
                            v-model="deviceName"
                            autocomplete="nickname"
                            maxlength="80"
                            placeholder="Desktop browser" />
                    </label>
                    <p v-if="state.error" class="text-sm text-destructive">{{ state.error }}</p>
                    <Button class="w-full" type="submit" :disabled="busy">
                        <LoaderCircle v-if="busy" class="animate-spin" :size="16" aria-hidden="true" />
                        ログイン
                    </Button>
                </form>

                <div v-if="state.auth.bootstrapped" class="mt-6 border-t pt-5">
                    <button
                        type="button"
                        class="web-pairing-disclosure"
                        :aria-expanded="showPairing"
                        @click="showPairing = !showPairing">
                        <Smartphone :size="16" aria-hidden="true" />
                        ペアリングコードでこの端末を追加
                    </button>
                    <form v-if="showPairing" class="web-auth-form mt-4" @submit.prevent="submitPairing">
                        <label class="web-form-field">
                            <span>ペアリングコード</span>
                            <Input v-model="pairingCode" autocomplete="one-time-code" required />
                        </label>
                        <label class="web-form-field">
                            <span>この端末の名前</span>
                            <Input v-model="deviceName" autocomplete="nickname" maxlength="80" placeholder="Phone" />
                        </label>
                        <Button variant="secondary" type="submit" :disabled="busy">端末を追加</Button>
                    </form>

                    <button
                        type="button"
                        class="web-pairing-disclosure mt-4"
                        :aria-expanded="showRecovery"
                        @click="showRecovery = !showRecovery">
                        <KeyRound :size="16" aria-hidden="true" />
                        管理者パスワードを忘れた場合
                    </button>
                    <form v-if="showRecovery" class="web-auth-form mt-4" @submit.prevent="submitRecovery">
                        <input
                            class="sr-only"
                            name="username"
                            value="admin"
                            autocomplete="username"
                            tabindex="-1"
                            readonly
                            aria-hidden="true" />
                        <Alert class="border-primary/30 bg-primary/5">
                            <AlertTitle>ホストで一回限りの復旧コードを発行してください</AlertTitle>
                            <AlertDescription>
                                Docker ホストで
                                <code>dotnet VRCX.Web.Server.dll --issue-admin-recovery</code>
                                をサーバーコンテナ内から実行します（Home Assistant アドオンでは
                                <code>vrcx-addon-run --issue-admin-recovery</code
                                >）。復旧コードは10分で失効し、一度使うと無効になります。
                            </AlertDescription>
                        </Alert>
                        <label class="web-form-field">
                            <span>復旧コード</span>
                            <Input v-model="recoveryCode" type="password" autocomplete="one-time-code" required />
                        </label>
                        <label class="web-form-field">
                            <span>新しい管理者パスワード</span>
                            <Input
                                v-model="password"
                                name="password"
                                type="password"
                                autocomplete="new-password"
                                minlength="12"
                                required />
                        </label>
                        <label class="web-form-field">
                            <span>新しい管理者パスワード（確認）</span>
                            <Input
                                v-model="confirmPassword"
                                name="password-confirmation"
                                type="password"
                                autocomplete="new-password"
                                minlength="12"
                                required />
                        </label>
                        <label class="web-form-field">
                            <span>この端末の名前</span>
                            <Input
                                v-model="deviceName"
                                autocomplete="nickname"
                                maxlength="80"
                                placeholder="Recovered browser" />
                        </label>
                        <p v-if="localError || state.error" class="text-sm text-destructive">
                            {{ localError || state.error }}
                        </p>
                        <Button variant="secondary" class="w-full" type="submit" :disabled="busy">
                            <LoaderCircle v-if="busy" class="animate-spin" :size="16" aria-hidden="true" />
                            パスワードを再設定して続ける
                        </Button>
                    </form>
                </div>
            </CardContent>
        </Card>
    </main>
</template>

<script setup>
    import { computed, onMounted, ref } from 'vue';
    import { KeyRound, LoaderCircle, ShieldCheck, Smartphone } from 'lucide-vue-next';

    import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert';
    import { Button } from '@/components/ui/button';
    import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';
    import { Input } from '@/components/ui/input';

    import { webSession } from '../session';

    const state = webSession.state;
    const setupToken = ref('');
    const password = ref('');
    const confirmPassword = ref('');
    const pairingCode = ref('');
    const recoveryCode = ref('');
    const deviceName = ref('');
    const showPairing = ref(false);
    const showRecovery = ref(false);
    const localError = ref('');
    const busy = computed(() => state.pendingRequests > 0);

    onMounted(() => {
        const hashQuery = window.location.hash.indexOf('?');
        const hashParams = new URLSearchParams(hashQuery >= 0 ? window.location.hash.slice(hashQuery + 1) : '');
        // Pairing URLs keep the one-time code in the fragment so it is never
        // sent in an HTTP request line or reverse-proxy access log.
        const code = hashParams.get('code');
        if (!code) return;
        pairingCode.value = code;
        showPairing.value = true;
        window.history.replaceState({}, document.title, window.location.pathname);
    });

    async function submitBootstrap() {
        localError.value = '';
        if (password.value !== confirmPassword.value) {
            localError.value = 'パスワードが一致しません。';
            return;
        }

        try {
            await webSession.bootstrap(password.value, setupToken.value, deviceName.value);
            setupToken.value = '';
            password.value = '';
            confirmPassword.value = '';
        } catch {
            // The shared session state contains a safe, localized error.
        }
    }

    async function submitLogin() {
        try {
            await webSession.login(password.value, deviceName.value);
            password.value = '';
        } catch {
            // The shared session state contains a safe, localized error.
        }
    }

    async function submitPairing() {
        try {
            await webSession.redeemPairing(pairingCode.value, deviceName.value);
            pairingCode.value = '';
        } catch {
            // The shared session state contains a safe, localized error.
        }
    }

    async function submitRecovery() {
        localError.value = '';
        if (password.value !== confirmPassword.value) {
            localError.value = 'パスワードが一致しません。';
            return;
        }

        try {
            await webSession.recoverAdmin(recoveryCode.value, password.value, deviceName.value);
            recoveryCode.value = '';
            password.value = '';
            confirmPassword.value = '';
            showRecovery.value = false;
        } catch {
            // The shared session state contains a safe, localized error.
        }
    }
</script>
