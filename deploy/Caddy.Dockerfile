FROM caddy:2.10.2-alpine
USER root
RUN setcap -r /usr/bin/caddy && \
    addgroup -S -g 10001 vrcx && \
    adduser -S -D -H -u 10001 -G vrcx vrcx && \
    mkdir -p /data /config && \
    chown -R vrcx:vrcx /data /config
USER vrcx
