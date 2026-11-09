# VRCX Web アドオン

オリジナルの VRCX レンダラーを非表示の worker（Xvfb + Electron）として常駐させ、その UI を
読み取り専用のブラウザアダプター経由で配信します。仕組みは `deploy/README.md` の Docker Compose
構成と同じで、Compose の Caddy と Docker secrets の代わりに、アドオンが TLS と秘密情報を
自前で用意します。

想定しているのは **信頼できる利用者 1 人** です。インターネットには公開しないでください。

## 動作要件

- Home Assistant OS（aarch64 / amd64）。主な対象は Raspberry Pi 5 です。
- メモリ: VRCX worker、.NET サーバー、Xvfb を合わせて、VRChat 未ログインの待機状態で約 250 MB です。
  ログイン後はフレンド数や履歴の量に応じて増えるため、Raspberry Pi 5 の 4 GB 以上のモデルを推奨します。
- 保存先: アドオンの `/data`（HA のバックアップ対象）に、VRCX のデータベースと
  Electron プロファイルを置きます。SD カードより SSD での運用を推奨します。

### Raspberry Pi 5 のページサイズについて

現行の HAOS は Raspberry Pi 5 のカーネルを 16 KB ページで動かしています（Raspberry Pi の
`bcm2712_defconfig` が `CONFIG_ARM64_16K_PAGES=y`）。このアドオンのイメージは、16 KB ページで
ビルドした Linux 6.18 を QEMU（arm64、TCG）で起動した環境で、.NET サーバー、Caddy、Xvfb、Electron の
worker が起動して HTTPS で応答することを確認済みです。Raspberry Pi 5 の実機ではまだ確認していないため、
起動しない場合はアドオンのログを確認してください。ページサイズは Terminal & SSH アドオンで
`getconf PAGESIZE` を実行すると確認できます（`16384` なら 16 KB）。

## インストール

1. **設定 → アドオン → アドオンストア → 右上のメニュー → リポジトリ** を開き、次の URL を追加します。

   ```
   https://github.com/1hira-c/VRCX#codex/vrcx-web-native
   ```

2. ストアに表示された **VRCX Web** をインストールします（初回はイメージのダウンロードに数分かかります）。
3. **設定** タブで `hostnames` に、ブラウザで開くときのホスト名・IP アドレスを **すべて** 入力します。
   例: `homeassistant.local`、`192.168.1.10`、Tailscale の MagicDNS 名。
4. アドオンを起動し、**ログ** タブを開きます。

## 初回セットアップ

1. 初回起動時、ログに次のような 1 回限りのセットアップトークンが表示されます。

   ```
   [vrcx-addon] No VRCX Web administrator exists yet. One-time setup token:
   [vrcx-addon]     xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx=
   ```

   これは「HA の管理者がこのセットアップをしている」ことを確認するためのもので、VRChat の認証情報や
   データベースの鍵ではありません。管理者を作成した後の起動では表示されなくなります。
   （Compose 版はトークンをログに出しませんが、HAOS ではホストのファイルを直接読めないため、
   HA 管理者だけが見られるアドオンログに出しています。）

2. 下記「HTTPS 証明書」の手順で CA を信頼させたうえで、`https://homeassistant.local:8443/` を開きます。
3. セットアップトークンを入力し、Web 管理者パスワード（12 文字以上）を設定します。
4. デスクトップ版 VRCX のデータを移行する場合は、ここでスナップショットをアップロードします
   （手順は `deploy/README.md` の「One-time desktop snapshot migration」と同じです）。
   移行しない場合は「移行せずに続行」を選ぶと、オリジナルの VRCX ログイン画面になります。

Web 管理者のログインと、VRCX 画面内の VRChat ログインは別物です。

## HTTPS 証明書

Web 管理者のセッション Cookie は `Secure` 属性付きのため、HTTPS が必須です。アドオンはポート
8443 で HTTPS のみを提供します（HTTP の待ち受けはありません）。

### 内蔵 CA（既定: `ssl: false`）

初回起動時に、アドオン専用のプライベート CA（有効期間 10 年）を `/data/tls` に作成し、
`hostnames` のすべての名前と IP を含むサーバー証明書（有効期間 397 日）を発行します。

- `export_ca: true` のとき、CA の **公開証明書** が `/share/vrcx/vrcx-local-ca.crt` にコピーされます。
  Samba share や File editor アドオンで取り出し、VRCX を開く端末にだけインストールしてください。
  インストール方法は `deploy/README.md` の「HTTPS trust」と同じです。
- ログに表示される `Built-in CA root SHA-256` と、端末に入れる証明書のフィンガープリントが一致することを
  確認してください。
- `hostnames` を変えた場合や、サーバー証明書の残り期間が 30 日を切った場合は、アドオンの再起動時に
  サーバー証明書が再発行されます（CA はそのままなので、端末側の作業は不要です）。
- ブラウザの証明書警告を「無視して進む」で回避しないでください。
- IP アドレスでアクセスする場合、その IP を `hostnames` に入れておく必要があります。

CA の秘密鍵は `/data/tls/ca.key` にあり、アドオンの外には出ません。

### /ssl の証明書（`ssl: true`）

Let's Encrypt アドオンや DuckDNS アドオンなどで `/ssl` に証明書がある場合は、`ssl: true` にして
`certfile` / `keyfile` を指定します。証明書の名前でアクセスしてください。証明書が更新されたら
アドオンを再起動してください。

## 外出先からのアクセス

ルーターのポート開放やリバースプロキシでインターネットに公開しないでください。外出先から使う場合は
Tailscale アドオンで HA ホストを tailnet に参加させ、Tailscale の IP または MagicDNS 名を
`hostnames` に追加してから `https://<その名前>:8443/` を開きます。

## バックアップ

- アドオンは **cold バックアップ**（バックアップ中は停止）です。SQLite と Electron プロファイルの
  整合性を保つためです。
- バックアップには、VRChat セッションを暗号化する鍵（`/data/secrets`）、暗号化済みのセッション、
  内蔵 CA の秘密鍵が含まれます。HA のバックアップは必ず暗号化して保管してください。
- キャッシュ（`runtime/xdg-cache` など）とインポートの一時ファイルはバックアップから除外しています。

## 管理者パスワードを忘れた場合

ログイン済みのブラウザが残っていない場合は、ホスト側で 1 回限りの回復コードを発行します。
保護モードをオフにした「Advanced SSH & Web Terminal」アドオンなど、`docker` コマンドが使える環境で:

```sh
docker exec -it "$(docker ps --format '{{.Names}}' | grep '_vrcx_web$')" vrcx-addon-run --issue-admin-recovery
```

回復コードで変わるのは Web 管理者パスワードだけです。VRCX の履歴や暗号化済み VRChat セッションは
削除されません。

## オプション

| オプション | 既定値 | 説明 |
| --- | --- | --- |
| `ssl` | `false` | `true` で `/ssl` の証明書を使う。`false` で内蔵 CA。 |
| `certfile` | `fullchain.pem` | `/ssl` 内の証明書チェーン。 |
| `keyfile` | `privkey.pem` | `/ssl` 内の秘密鍵。 |
| `hostnames` | `homeassistant.local` | 内蔵 CA の証明書に入れるホスト名・IPv4 アドレス。 |
| `export_ca` | `true` | 内蔵 CA の公開証明書を `/share/vrcx/` にコピーする。 |

## 構成メモ

- コンテナは root で起動し、`/data` の準備（秘密情報の生成、証明書、権限）だけを行ってから、
  .NET サーバーと Caddy を非 root ユーザー（`app`）で起動します。
- .NET サーバーは `127.0.0.1:8080`、worker の制御用エンドポイントは `127.0.0.1:18081` で待ち受け、
  外部に公開されるのは Caddy の 8443 だけです。
- HA のアドオンコンテナは `/dev/shm` が 64 MB 固定のため、Chromium には `--disable-dev-shm-usage` を
  渡しています。
- HSTS ヘッダーは付けません。HSTS はホスト名単位なので、`homeassistant.local:8123` の HA 本体まで
  HTTPS を強制してしまうためです。
- イメージは GitHub Actions（`.github/workflows/ha-addon.yaml`）が
  `ghcr.io/1hira-c/vrcx-ha-addon-{arch}:<version>` としてビルド・公開します。
