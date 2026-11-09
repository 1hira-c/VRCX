# VRCX Web — Tailscale self-hosting

This stack runs the original VRCX renderer as a hidden 24/7 worker and serves the original VRCX UI
through a read-only browser adapter. It is intended for one trusted user on a Linux host or NAS.
Caddy is published only on the host's Tailscale IPv4 address; the ASP.NET control plane and the
worker control socket remain inside the container.

The desktop VRCX database is never mounted or shared. The server owns `/data/runtime/VRCX.sqlite3`.
The separate `/data/vrcx-server.db` contains only the Web administrator password verifier and
browser-device sessions.

For Home Assistant OS (for example on a Raspberry Pi 5), use the add-on in
[`home-assistant/vrcx`](../home-assistant/vrcx/DOCS.md) instead of this Compose stack. It runs the same
server image in one container with its own TLS front end and generated secrets.

## Required environment

Docker Engine, Compose v2, OpenSSL, `jq`, and a connected Tailscale node are required. Keep the
tailnet grant (or an existing ACL policy) limited to trusted devices and do not use Funnel or a
router port-forward.

For a new installation, create two different secrets once:

```sh
mkdir -p deploy/secrets
chmod 0700 deploy/secrets
umask 077
openssl rand -base64 32 > deploy/secrets/vrcx_secret_key
openssl rand -base64 32 > deploy/secrets/vrcx_setup_token
chmod 0640 deploy/secrets/vrcx_secret_key deploy/secrets/vrcx_setup_token
```

`vrcx_secret_key` encrypts the VRChat cookie snapshot stored in the original VRCX database.
`vrcx_setup_token` is the one-time first-setup confirmation code shown by
`cat deploy/secrets/vrcx_setup_token`. It only proves that the person creating the Web administrator
can access the Docker host. It is not a VRChat credential or a database key. The two files are not
interchangeable, and neither value is written to logs.

Set the non-secret deployment values:

```sh
export VRCX_SECRET_GID="$(stat -c '%g' deploy/secrets/vrcx_secret_key)"
export VRCX_BIND_ADDRESS="$(tailscale ip -4 | head -n1)"
export VRCX_HOST="$(tailscale status --json | jq -r '.Self.DNSName' | sed 's/\.$//')"
test -n "$VRCX_BIND_ADDRESS" && tailscale ip -4 | grep -Fqx -- "$VRCX_BIND_ADDRESS"
case "$VRCX_HOST" in
  ''|'null'|*[!A-Za-z0-9.-]*) echo 'VRCX_HOST is not a valid DNS name' >&2; exit 1 ;;
esac
```

The validation must succeed. `VRCX_BIND_ADDRESS` is required and must be this host's Tailscale IPv4
address, never `0.0.0.0`, a LAN address, or a public interface. Compose publishes HTTPS only; there
is no plaintext HTTP listener or redirect.

In a managed build environment, set `CODEX_PROXY_CERT` to its public proxy CA file before building:

```sh
export CODEX_PROXY_CERT=/path/to/public-build-proxy-ca.crt
docker compose -p vrcx-web build
```

On a normal NAS or Linux host without that build proxy, leave the variable unset; the build uses the
standard CA trust already present in its base images. This is not an application secret and is never
mounted at runtime.

## Safe first run and upgrade

The Compose file fixes its project name to `vrcx-web`; keep it unchanged. This reuses the existing
`vrcx-web_vrcx-data`, `vrcx-web_caddy-data`, and `vrcx-web_caddy-config` volumes. The administrator
password, browser sessions, and Caddy private CA therefore survive the upgrade. Back up the data
volume and both secret files before replacing a working deployment.

`docker compose up` for this project is a cutover operation: it recreates services with the same
Compose project and service names. Do not run it while a working legacy `vrcx-web` deployment must
remain untouched. Configuration expansion and image builds do not replace running services:

```sh
docker compose -p vrcx-web config --quiet
docker compose -p vrcx-web build
```

Only after the backup, compatibility checks, and cutover have been explicitly approved, replace the
running services:

```sh
docker compose -p vrcx-web up -d
docker compose -p vrcx-web ps
```

The server container is non-root, has a read-only root filesystem, and exposes no host port. Only
Caddy binds to `VRCX_BIND_ADDRESS`. Do not add a `ports:` entry to `vrcx-server`.
The server healthcheck requires both the ASP.NET health endpoint and the hidden original-VRCX worker
control endpoint to be ready. It does not imply that VRChat authentication is currently valid.

On a fresh volume, the HTTPS page asks for the local setup token once and then sets the
administrator password. Before the original VRCX login is shown, a fresh server also offers the
one-time desktop snapshot import described below. On an existing volume it goes directly to the
normal administrator login. The Web administrator login is separate from the VRChat login shown by
the original VRCX UI.

## HTTPS trust

Caddy keeps its CA in the named volume. Export the public root certificate and trust it only on
devices that should open VRCX:

```sh
docker compose -p vrcx-web cp caddy:/data/caddy/pki/authorities/local/root.crt ./deploy/vrcx-local-ca.crt
```

Only export `root.crt`. Never copy or expose the CA private key under
`/data/caddy/pki/authorities/local/private/`.

Install the exported root only on devices that should trust this private service. Examples:

- Debian/Ubuntu: copy it to `/usr/local/share/ca-certificates/vrcx-local-ca.crt`, then run
  `sudo update-ca-certificates`.
- Windows Chrome/Edge: import it for the current user into **Trusted Root Certification Authorities**
  (for example, with `certmgr.msc`).
- macOS Safari/Chrome: add it to the login keychain with Keychain Access and set it to **Always Trust**.
- iPhone/iPad: install the certificate profile, then enable it under **Settings → General → About →
  Certificate Trust Settings**. Android's user-CA installation path varies by device policy.

Verify the certificate fingerprint out of band before installing it on another device. Removing the
root from a device removes that device's trust in this Caddy CA.

Open `https://$VRCX_HOST/`. If `VRCX_HTTPS_PORT` is not `443`, open
`https://$VRCX_HOST:$VRCX_HTTPS_PORT/` instead. Do not bypass certificate warnings with a browser
exception.

## Password recovery

If the administrator password is lost and no authenticated browser remains, issue a short-lived,
one-use recovery code on the host:

```sh
container_id="$(docker ps -q --filter label=com.docker.compose.project=vrcx-web --filter label=com.docker.compose.service=vrcx-server)"
docker exec -it "$container_id" dotnet VRCX.Web.Server.dll --issue-admin-recovery
```

Redeeming it changes only the Web administrator password and revokes all browser sessions and
pairing codes. It does not delete VRCX history or the encrypted VRChat session.

## One-time desktop snapshot migration

Do not copy a live desktop database into `/data/runtime` and do not share it with desktop VRCX.
Completely exit desktop VRCX first, then take the SQLite main database and any matching `-wal` and
`-shm` files as one snapshot. After signing in as the Web administrator, select those files in the
HTTPS first-run screen. Browser-provided paths and filenames are ignored; the server stages them
under generated names in `/data/import-staging`.

The import is serialized with other import attempts. After upload finishes, the server pauses the
hidden original-VRCX worker and waits for its complete process group to exit before opening the
runtime database. It validates the snapshot and copies only allowlisted original VRCX tables and
columns in one transaction. Staged main/WAL/SHM files are deleted and the worker is resumed after
both success and failure.

Import is allowed only while the server runtime has no VRCX domain rows. Retrying the exact same
snapshot is an idempotent no-op, but a different snapshot is rejected after the first import or
after live synchronization has started. Choosing **continue without migration** opens the original
VRCX login; once that login has populated the runtime database, return migration is intentionally
closed.

Cookies, `savedCredentials`, passwords, proxy settings, API keys/endpoints, server-auth tables, and
other session or secret material are never imported. History, notification context, memos,
favorites, analytics data, and the small allowlist of non-secret display preferences retain their
original table shapes.

## Isolated verification and browser checks

To keep an existing deployment running during evaluation, use a different Compose project name,
HTTPS port, image tags, and secret files. `--project-name` overrides the default `vrcx-web` name.
The optional `VRCX_SERVER_IMAGE`, `VRCX_CADDY_IMAGE`, `VRCX_SECRET_KEY_FILE`, and
`VRCX_SETUP_TOKEN_FILE` variables select those isolated resources. Keep secret files on persistent
host storage so a host restart does not invalidate their bind mounts. A different project also
creates its own database volumes and Caddy CA; export and trust that project's public root.

The additional Web runtime CI tests the fixed reads, administrator authentication, cookie
encryption, migration, worker lifecycle, and browser adapters. Both native amd64 and arm64 jobs
build the image, start Compose, verify HTTPS with its real CA, reject anonymous APIs, and confirm
the server has no published management port.

`tests/browser-web-smoke.cjs` uses Chromium without Electron bridges. Its anonymous window reads
the real setup/login screen. Other windows use explicit read-only API fixtures to exercise the
original login, Feed, friends, notifications, game log, analytics, search, theme switching, SSE
staleness/recovery, and mobile navigation. Those fixture checks do not authenticate to VRChat or
prove that a real account's credentials work. Screenshots and a JSON report distinguish fixture
data from the live anonymous check.

For an already running default project, a local browser smoke can be run with:

```sh
install -d -m 0777 build/browser-smoke
docker run --rm --init --shm-size=256m \
  --network vrcx-web_vrcx \
  --volume "$PWD/tests/browser-web-smoke.cjs:/smoke/browser-web-smoke.cjs:ro" \
  --volume "$PWD/build/browser-smoke:/artifacts" \
  --env VRCX_SMOKE_URL=http://vrcx-server:8080/ \
  --env VRCX_SMOKE_OUTPUT=/artifacts \
  --entrypoint /usr/bin/xvfb-run \
  "${VRCX_SERVER_IMAGE:-vrcx-web-native:local}" \
  -a /app/worker/electron/electron --no-sandbox /smoke/browser-web-smoke.cjs
```

This temporary test container stays on the private Compose network and publishes no ports. The
separate HTTPS check must still use the Caddy URL and its public CA with certificate verification
enabled. For a browser smoke against HTTPS directly, supply its public CA with
`VRCX_SMOKE_CA_FILE`; the harness verifies the real certificate before trusting that exact leaf.
