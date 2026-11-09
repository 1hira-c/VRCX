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
Only a committed new main-frame document clears readiness; canceled and failed
navigations preserve it. Startup has no ready deadline because the original database
migrations, VACUUM, and optimize can take a long time. A database initialization failure
reports the fixed `database_initialization_failed` reason without waiting for a hidden
desktop dialog or repeatedly restarting the migration. A crashed renderer terminates
the worker so the existing process supervisor can restart it. Once ready, an unresponsive
renderer has a 30-second grace period, canceled if it becomes responsive again.
The database failure is retained for that worker process. After repairing its cause,
restart the worker; browser reloads and VRChat reauthentication do not clear it.

Browser-side worker availability is tracked separately from VRChat authentication.
Only an explicit `503 vrchat_worker_unavailable` marks the worker unavailable and
preserves the last known session and data. Other login/OTP failures stay on the auth
form. Failed status reads retry after at least 30 seconds and respect `Retry-After`;
credentials are never retried automatically. Status and domain reads have separate
concurrency limits so polling and browsing cannot consume the login/OTP rate-limit
budget. VRChat request pacing remains in the original runtime. An expired server
cookie session uses the original logout flow, rather than the desktop saved-password
retry, so the browser can request reauthentication.

The server data directory must be supplied explicitly, for example:

```sh
xvfb-run -a electron . --server-mode --config=/var/lib/vrcx-server
```

The hidden BrowserWindow still requires a display service. The Docker image should
run Electron as a non-root user under Xvfb; it must not publish a debugging port.
The server runtime database is not the desktop VRCX database. Import is a one-time,
filtered operation into this dedicated directory.

History ownership is separate from VRChat authentication. A fixed
`vrcx_web_data_owner` record is derived from the validated snapshot account and
matching imported tables, then updated only by the original renderer's completed
login flow. Logout clears its usual session settings while retaining this
non-secret record. ASP.NET reads it through its read-only connection; it does not
write to the live runtime database. Existing installations can migrate the original
non-secret last-account setting into this record through the native SQLite writer.

Before an account-specific history read, the hidden renderer restores an empty
database context from that trusted record without logging in or creating user
tables. A native login context always takes precedence, including when a login
finishes while the metadata read is pending. Global game logs and memos remain
readable without an account; activity analysis uses the stored owner when available.

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

Notification-only changes use server-owned SQLite revision triggers for the original
notification tables. A committed insert, update, or delete produces a `data` event
for `notifications`; the outbox and observed revision advance in one transaction.
The event carries no notification content. The browser reloads the original
notification view through its existing resource-revision path. Replacing a browser
friend snapshot likewise uses the original batch removal path, which resets the
friend map, sorted sidebar index, and presence counts together.

Entity details use the original `queryRequest` and its cache policies, including
coalescing concurrent reads. Remote avatar lookups check the worker's persisted
`VRCX_avatarRemoteDatabase` setting before contacting a provider, and load the original
persistent VRCX identifier independently of desktop self-update. Browser-local
preferences cannot override that server setting.

Author avatar results cross the fixed read boundary once, as a complete list with
at most 5,000 entries. Name search retains the original 5,000-result range. Provider
failures never become a successful empty search; existing displayed results remain
visible on failure. For an author lookup with both successful and failed providers,
the result includes available entries and only the fixed
`avatar_provider_partial_failure` warning, preserving the original partial-result
notification without exposing provider URLs or exception text. Oversized results
fail explicitly rather than silently truncating.

Server shutdown drains cookie-save timer callbacks, serializes the final save and
key disposal, and prevents later writes with the destroyed key. Desktop cookie
persistence and HTTP completion behavior remain unchanged.
