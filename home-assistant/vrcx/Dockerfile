# syntax=docker/dockerfile:1.7
#
# Home Assistant add-on image. It wraps the regular VRCX Web server image (the repository root
# Dockerfile) with a TLS front end and an entrypoint that follows Home Assistant add-on
# conventions (/data/options.json, root start, persistent /data).
#
#   docker build --platform linux/arm64 -t vrcx-web-native:local .
#   docker build --platform linux/arm64 \
#     --build-arg VRCX_SERVER_IMAGE=vrcx-web-native:local \
#     -t ghcr.io/1hira-c/vrcx-ha-addon-aarch64:0.1.0 home-assistant/vrcx

ARG VRCX_SERVER_IMAGE=vrcx-web-native:local
ARG CADDY_IMAGE=caddy:2.10.2-alpine

FROM ${CADDY_IMAGE} AS caddy
USER root
# The upstream binary carries cap_net_bind_service. It is not needed on port 8443 and would make
# exec fail under no_new_privs, so drop the file capability.
RUN setcap -r /usr/bin/caddy

FROM ${VRCX_SERVER_IMAGE}
ARG ADDON_VERSION=dev
ARG ADDON_ARCH=aarch64
USER root
RUN apt-get update && \
    apt-get install -y --no-install-recommends jq openssl && \
    rm -rf /var/lib/apt/lists/*
COPY --from=caddy /usr/bin/caddy /usr/local/bin/caddy
COPY rootfs/ /
RUN chmod 0755 /usr/local/bin/vrcx-addon-run

# Kestrel listens on loopback only; Caddy terminates TLS on 8443 and is the only published port.
ENV ASPNETCORE_URLS=http://127.0.0.1:8080
EXPOSE 8443

LABEL io.hass.type="addon" \
      io.hass.name="VRCX Web" \
      io.hass.version="${ADDON_VERSION}" \
      io.hass.arch="${ADDON_ARCH}" \
      org.opencontainers.image.source="https://github.com/1hira-c/VRCX"

ENTRYPOINT ["/usr/local/bin/vrcx-addon-run"]
