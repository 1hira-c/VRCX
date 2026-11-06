# Server runtime boundary

Server mode is a parity host for the existing VRCX runtime. It keeps the
same Vue/Pinia stores, request layer, update loop, Pipeline reducer, coordinators,
and SQLite repositories running in a hidden Electron renderer. It does not make
the Electron or .NET bridge remotely accessible.

In server mode, the Electron main process:

- creates the normal renderer with `show: false` and background timer throttling disabled;
- initializes the existing VRCX storage, SQLite, and HTTP/Cookie transport;
- does not initialize Discord, VR/OpenVR, the VRChat log watcher, system monitoring,
  tray/desktop installation, self-update, or desktop notifications;
- exposes runtime capabilities to the renderer so desktop-only side effects are
  skipped while the original remote synchronization and persistence paths remain active.

Renderer readiness follows the main document, not Electron's loading spinner. With a
`file://` entry point, the original router's hash transitions also emit
`did-start-loading`; these transitions must preserve the registered control handlers.
Only a new main-frame document clears readiness. A missing ready handshake, crashed
renderer, or unresponsive hidden window terminates the worker so the existing process
supervisor can restart it. Browser-side worker availability is tracked separately from
VRChat authentication; an internal 503 preserves the last known session and data.

The server data directory must be supplied explicitly, for example:

```sh
xvfb-run -a electron . --server-mode --config=/var/lib/vrcx-server
```

The hidden BrowserWindow still requires a display service. The Docker image should
run Electron as a non-root user under Xvfb; it must not publish a debugging port.
The server runtime database is not the desktop VRCX database. Import is a one-time,
filtered operation into this dedicated directory.

## Control and projection boundary

The browser-facing ASP.NET service must not call the generic Electron/.NET bridge
and must not expose arbitrary HTTP, headers, cookies, SQL, or renderer IPC.
Communication with the hidden runtime uses two narrow interfaces:

1. A private loopback-only HTTP control listener on `127.0.0.1:18081`. Every route
   and payload is fixed and validated for login, OTP, reauthentication, logout,
   search, entity reads, and explicitly named original SQLite reads. Login and OTP
   commands invoke the existing VRCX auth store; results contain only
   status/challenge data and never the VRChat cookie. The listener is not published
   from the container.
2. The server-owned original VRCX SQLite database plus a transactional
   projection/outbox written by a thin renderer adapter. ASP.NET opens domain data
   read-only (`query_only`) and exposes only fixed `/api/v1` endpoints and SSE
   events. Views whose original VRCX repository method carries more semantics than
   a projection call that exact method through the typed loopback interface.

Projection code belongs at this boundary. The original Pipeline payloads and VRCX
domain objects remain intact inside the trusted runtime; allowlisting must not
discard context before reducers, history, feed, and notification persistence have run.
