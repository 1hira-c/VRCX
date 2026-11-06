# syntax=docker/dockerfile:1.7

FROM node:24-bookworm-slim AS frontend-build
WORKDIR /workspace
COPY package.json package-lock.json ./
RUN --mount=type=secret,id=proxy_ca,required=false \
    set -eu; \
    if [ -s /run/secrets/proxy_ca ]; then export NODE_EXTRA_CA_CERTS=/run/secrets/proxy_ca; fi; \
    npm ci --strict-ssl=true
COPY Version ./Version
COPY images ./images
COPY src ./src
COPY src-electron ./src-electron
COPY build-scripts ./build-scripts
COPY Dotnet ./Dotnet
RUN npm run prod && \
    mkdir -p /out/desktop /out/web /out/worker-node_modules && \
    cp -a build/html/. /out/desktop/ && \
    npm run build:web && \
    cp -a build/html/. /out/web/ && \
    cp -a node_modules/electron/dist /out/electron && \
    cp -a node_modules/hazardous node_modules/callsite node_modules/node-api-dotnet /out/worker-node_modules/

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dotnet-build
ARG TARGETARCH
WORKDIR /workspace
COPY Version ./Version
COPY images ./images
COPY Dotnet ./Dotnet
COPY --from=frontend-build /out/web/. Dotnet/VRCX.Web.Server/wwwroot/
RUN --mount=type=secret,id=proxy_ca,required=false \
    set -eu; \
    build_ca=/etc/ssl/certs/ca-certificates.crt; \
    if [ -s /run/secrets/proxy_ca ]; then \
      cat /etc/ssl/certs/ca-certificates.crt /run/secrets/proxy_ca > /tmp/vrcx-build-ca.pem; \
      build_ca=/tmp/vrcx-build-ca.pem; \
    fi; \
    export SSL_CERT_FILE="$build_ca"; \
    case "$TARGETARCH" in \
      amd64) platform=x64; rid=linux-x64 ;; \
      arm64) platform=ARM64; rid=linux-arm64 ;; \
      *) echo "Unsupported target architecture: $TARGETARCH" >&2; exit 1 ;; \
    esac && \
    dotnet restore Dotnet/VRCX-Electron.csproj -p:Platform="$platform" -r "$rid" && \
    dotnet build Dotnet/VRCX-Electron.csproj --configuration Release --no-restore -p:Platform="$platform" -r "$rid" && \
    dotnet restore Dotnet/VRCX.Web.Server/VRCX.Web.Server.csproj -p:Platform="$platform" -r "$rid" && \
    dotnet publish Dotnet/VRCX.Web.Server/VRCX.Web.Server.csproj \
      --configuration Release --no-restore --output /out/server -p:Platform="$platform" -r "$rid" --no-self-contained && \
    rm -f /tmp/vrcx-build-ca.pem

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN --mount=type=secret,id=proxy_ca,required=false \
    set -eu; \
    build_ca=/etc/ssl/certs/ca-certificates.crt; \
    if [ -s /run/secrets/proxy_ca ]; then \
      cat /etc/ssl/certs/ca-certificates.crt /run/secrets/proxy_ca > /tmp/vrcx-build-ca.pem; \
      build_ca=/tmp/vrcx-build-ca.pem; \
    fi && \
    apt-get -o Acquire::https::CaInfo="$build_ca" update && \
    apt-get -o Acquire::https::CaInfo="$build_ca" install -y --no-install-recommends \
      ca-certificates curl fonts-liberation util-linux xauth xvfb \
      libasound2t64 libatk-bridge2.0-0t64 libatk1.0-0t64 libcairo2 libcups2t64 libdbus-1-3 \
      libdrm2 libgbm1 libglib2.0-0 libgtk-3-0t64 libnspr4 libnss3 libpango-1.0-0 \
      libx11-6 libx11-xcb1 libxcb1 libxcomposite1 libxdamage1 libxext6 libxfixes3 \
      libxkbcommon0 libxrandr2 libxshmfence1 libxss1 && \
    rm -rf /var/lib/apt/lists/* && \
    rm -f /tmp/vrcx-build-ca.pem

WORKDIR /app
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0 \
    HOME=/data/runtime/home \
    XDG_CONFIG_HOME=/data/runtime/xdg-config \
    XDG_CACHE_HOME=/data/runtime/xdg-cache \
    VRCX_WEB_SECRET_KEY_FILE=/run/secrets/vrcx_secret_key

COPY --from=dotnet-build /out/server/. ./
COPY package.json Version /app/worker/
COPY images /app/worker/images
COPY src-electron /app/worker/src-electron
COPY --from=frontend-build /out/desktop/. /app/worker/build/html/
COPY --from=frontend-build /out/electron/. /app/worker/electron/
COPY --from=frontend-build /out/worker-node_modules/. /app/worker/node_modules/
COPY --from=dotnet-build /workspace/build/Electron/ /app/worker/build/Electron/

RUN mkdir -p /data/runtime/home /data/runtime/xdg-config /data/runtime/xdg-cache && \
    chown -R ${APP_UID}:${APP_UID} /data

USER ${APP_UID}
EXPOSE 8080
ENTRYPOINT ["dotnet", "VRCX.Web.Server.dll"]
