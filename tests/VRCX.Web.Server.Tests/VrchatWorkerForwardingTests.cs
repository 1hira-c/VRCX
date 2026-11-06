using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Xunit;

namespace VRCX.Web.Server.Tests;

public sealed partial class ReadApiEndpointTests
{
    [Fact]
    public async Task WorkerAuthEndpointsForwardOnlyTheFixedTypedContract()
    {
        var repositoryRoot = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "vrcx-web-server-worker-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var setupTokenBytes = RandomNumberGenerator.GetBytes(32);
        var setupToken = Convert.ToBase64String(setupTokenBytes);
        CryptographicOperations.ZeroMemory(setupTokenBytes);
        var setupTokenPath = Path.Combine(temporaryDirectory, "setup-token");
        await File.WriteAllTextAsync(setupTokenPath, setupToken);
        var readModelDatabasePath = Path.Combine(temporaryDirectory, "VRCX.sqlite3");
        await CreateFallbackReadModelAsync(readModelDatabasePath);

        var workerBaseAddress = new Uri($"http://127.0.0.1:{ReserveTcpPort()}/");
        using var worker = new HttpListener();
        worker.Prefixes.Add(workerBaseAddress.ToString());
        worker.Start();

        var serverBaseAddress = new Uri($"http://127.0.0.1:{ReserveTcpPort()}");
        var output = new ConcurrentQueue<string>();
        using var process = StartServer(
            repositoryRoot,
            temporaryDirectory,
            setupTokenPath,
            serverBaseAddress,
            output,
            workerBaseAddress,
            readModelDatabasePath: readModelDatabasePath);
        try
        {
            using var client = new HttpClient
            {
                BaseAddress = serverBaseAddress,
                Timeout = TimeSpan.FromSeconds(5)
            };
            await WaitForServerAsync(client, process, output);

            using var bootstrap = await client.PostAsJsonAsync("/api/v1/auth/bootstrap", new
            {
                password = "administrator password",
                setupToken,
                deviceName = "Worker forwarding test"
            });
            Assert.Equal(HttpStatusCode.Created, bootstrap.StatusCode);
            var sessionCookie = bootstrap.Headers.GetValues("Set-Cookie").Single().Split(';', 2)[0];

            var statusExchange = ReplyFromWorkerAsync(
                worker,
                "GET",
                "/control/v1/status",
                (request, body) =>
                {
                    Assert.Null(body);
                    AssertNoForwardedBrowserHeaders(request);
                },
                new
                {
                    ok = true,
                    status = "authenticated",
                    currentUserId = "usr_forward_test",
                    challenge = (object?)null,
                    errorCode = (string?)null
                });
            using (var response = await SendAuthenticatedAsync(
                client,
                HttpMethod.Get,
                "/api/v1/vrchat/auth/status",
                sessionCookie))
            {
                await statusExchange;
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.True(payload.RootElement.GetProperty("ok").GetBoolean());
                Assert.Equal("authenticated", payload.RootElement.GetProperty("status").GetString());
                Assert.Equal(
                    "usr_forward_test",
                    payload.RootElement.GetProperty("currentUser").GetProperty("id").GetString());
                Assert.False(payload.RootElement.TryGetProperty("currentUserId", out _));
                Assert.False(payload.RootElement.TryGetProperty("token", out _));
                Assert.False(payload.RootElement.TryGetProperty("cookie", out _));
            }

            var loginExchange = ReplyFromWorkerAsync(
                worker,
                "POST",
                "/control/v1/login",
                (request, body) =>
                {
                    AssertNoForwardedBrowserHeaders(request);
                    Assert.NotNull(body);
                    Assert.Equal(2, body.RootElement.EnumerateObject().Count());
                    Assert.Equal("forward-user", body.RootElement.GetProperty("username").GetString());
                    Assert.Equal("forward-password", body.RootElement.GetProperty("password").GetString());
                },
                new
                {
                    ok = true,
                    status = "two_factor_required",
                    currentUserId = (string?)null,
                    challenge = new { kinds = new[] { "totp", "emailOtp" } },
                    errorCode = (string?)null
                });
            using (var request = AuthenticatedRequest(
                HttpMethod.Post,
                "/api/v1/vrchat/auth/login",
                sessionCookie))
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer browser-only");
                request.Headers.TryAddWithoutValidation("X-Worker-Endpoint", "http://example.invalid");
                request.Content = JsonContent.Create(new
                {
                    username = "forward-user",
                    password = "forward-password"
                });
                using var response = await client.SendAsync(request);
                await loginExchange;
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.Equal("two_factor_required", payload.RootElement.GetProperty("status").GetString());
                var kinds = payload.RootElement.GetProperty("challenge").GetProperty("kinds");
                Assert.Equal(2, kinds.GetArrayLength());
            }

            var twoFactorExchange = ReplyFromWorkerAsync(
                worker,
                "POST",
                "/control/v1/two-factor",
                (request, body) =>
                {
                    AssertNoForwardedBrowserHeaders(request);
                    Assert.NotNull(body);
                    Assert.Equal(2, body.RootElement.EnumerateObject().Count());
                    Assert.Equal("totp", body.RootElement.GetProperty("kind").GetString());
                    Assert.Equal("123456", body.RootElement.GetProperty("code").GetString());
                },
                new
                {
                    ok = false,
                    status = "two_factor_required",
                    currentUserId = (string?)null,
                    challenge = new { kinds = new[] { "totp" } },
                    errorCode = "http_401"
                });
            using (var response = await SendAuthenticatedJsonAsync(
                client,
                "/api/v1/vrchat/auth/two-factor",
                sessionCookie,
                new { kind = "totp", code = "123456" }))
            {
                await twoFactorExchange;
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.False(payload.RootElement.GetProperty("ok").GetBoolean());
                Assert.Equal("http_401", payload.RootElement.GetProperty("errorCode").GetString());
            }

            var resendExchange = ReplyFromWorkerAsync(
                worker,
                "POST",
                "/control/v1/email/resend",
                (request, body) =>
                {
                    Assert.Null(body);
                    AssertNoForwardedBrowserHeaders(request);
                },
                new
                {
                    ok = true,
                    status = "authenticating",
                    currentUserId = (string?)null,
                    challenge = (object?)null,
                    errorCode = (string?)null
                });
            using (var response = await SendAuthenticatedAsync(
                client,
                HttpMethod.Post,
                "/api/v1/vrchat/auth/email-otp/resend",
                sessionCookie))
            {
                await resendExchange;
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            var logoutExchange = ReplyFromWorkerAsync(
                worker,
                "POST",
                "/control/v1/logout",
                (request, body) =>
                {
                    Assert.Null(body);
                    AssertNoForwardedBrowserHeaders(request);
                },
                new
                {
                    ok = true,
                    status = "requires_authentication",
                    currentUserId = (string?)null,
                    challenge = (object?)null,
                    errorCode = (string?)null
                });
            using (var response = await SendAuthenticatedAsync(
                client,
                HttpMethod.Post,
                "/api/v1/vrchat/auth/logout",
                sessionCookie))
            {
                await logoutExchange;
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            var liveSearchExchange = ReplyFromWorkerAsync(
                worker,
                "POST",
                "/control/v1/search",
                (request, body) =>
                {
                    AssertNoForwardedBrowserHeaders(request);
                    Assert.NotNull(body);
                    Assert.Equal(5, body.RootElement.EnumerateObject().Count());
                    Assert.Equal("world", body.RootElement.GetProperty("type").GetString());
                    Assert.Equal("Live World", body.RootElement.GetProperty("query").GetString());
                    Assert.Equal(0, body.RootElement.GetProperty("offset").GetInt32());
                    Assert.Equal(20, body.RootElement.GetProperty("limit").GetInt32());
                    var options = body.RootElement.GetProperty("options");
                    Assert.Equal(5, options.EnumerateObject().Count());
                    Assert.Equal("recent", options.GetProperty("worldOption").GetString());
                    Assert.Equal("popularity", options.GetProperty("sort").GetString());
                    Assert.Equal("descending", options.GetProperty("order").GetString());
                    Assert.False(options.GetProperty("featured").GetBoolean());
                    Assert.Equal("system_approved", options.GetProperty("tag").GetString());
                },
                new
                {
                    ok = true,
                    items = new[]
                    {
                        new
                        {
                            type = "world",
                            id = "wrld_live",
                            name = "Live World",
                            ownerId = "usr_live_author",
                            description = "Returned by the original VRCX worker",
                            updatedAt = "2026-11-06T12:00:00Z",
                            payload = new
                            {
                                id = "wrld_live",
                                name = "Live World",
                                authorId = "usr_live_author",
                                tags = new[] { "system_approved" }
                            }
                        }
                    },
                    total = 1,
                    offset = 0,
                    limit = 20,
                    errorCode = (string?)null
                });
            using (var request = AuthenticatedRequest(
                HttpMethod.Get,
                "/api/v1/search?type=world&q=Live%20World&offset=0&limit=20&worldOption=recent&sort=popularity&order=descending&featured=false&tag=system_approved",
                sessionCookie))
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer browser-only");
                request.Headers.TryAddWithoutValidation("X-Worker-Endpoint", "http://example.invalid");
                using var response = await client.SendAsync(request);
                await liveSearchExchange;
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var item = Assert.Single(payload.RootElement.GetProperty("items").EnumerateArray());
                Assert.Equal("wrld_live", item.GetProperty("id").GetString());
                Assert.Equal("Live World", item.GetProperty("name").GetString());
                Assert.Equal("usr_live_author", item.GetProperty("ownerId").GetString());
            }

            var categorySearchExchange = ReplyFromWorkerAsync(
                worker,
                "POST",
                "/control/v1/search",
                (request, body) =>
                {
                    AssertNoForwardedBrowserHeaders(request);
                    Assert.NotNull(body);
                    Assert.Equal(string.Empty, body.RootElement.GetProperty("query").GetString());
                    var options = body.RootElement.GetProperty("options");
                    Assert.Equal(2, options.EnumerateObject().Count());
                    Assert.Equal("favorites", options.GetProperty("worldOption").GetString());
                    Assert.Equal("descending", options.GetProperty("order").GetString());
                },
                new
                {
                    ok = true,
                    items = Array.Empty<object>(),
                    total = 0,
                    offset = 0,
                    limit = 60,
                    errorCode = (string?)null
                });
            using (var response = await SendAuthenticatedAsync(
                client,
                HttpMethod.Get,
                "/api/v1/search?type=world&q=&limit=60&worldOption=favorites&order=descending",
                sessionCookie))
            {
                await categorySearchExchange;
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.Empty(payload.RootElement.GetProperty("items").EnumerateArray());
            }

            using (var response = await SendAuthenticatedAsync(
                client,
                HttpMethod.Get,
                "/api/v1/search?type=world&q=&limit=60&sort=arbitrary",
                sessionCookie))
            {
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            }

            var liveEntityExchange = ReplyFromWorkerAsync(
                worker,
                "POST",
                "/control/v1/entity",
                (request, body) =>
                {
                    AssertNoForwardedBrowserHeaders(request);
                    Assert.NotNull(body);
                    Assert.Equal(2, body.RootElement.EnumerateObject().Count());
                    Assert.Equal("world", body.RootElement.GetProperty("type").GetString());
                    Assert.Equal("wrld_live", body.RootElement.GetProperty("id").GetString());
                },
                new
                {
                    ok = true,
                    item = new
                    {
                        type = "world",
                        id = "wrld_live",
                        name = "Live World Detail",
                        ownerId = "usr_live_author",
                        description = "Live detail",
                        updatedAt = "2026-11-06T12:00:00Z",
                        payload = new { id = "wrld_live", name = "Live World Detail" }
                    },
                    errorCode = (string?)null
                });
            using (var response = await SendAuthenticatedAsync(
                client,
                HttpMethod.Get,
                "/api/v1/worlds/wrld_live",
                sessionCookie))
            {
                await liveEntityExchange;
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.Equal("Live World Detail", payload.RootElement.GetProperty("name").GetString());
            }

            var authenticationFallbackExchange = ReplyFromWorkerAsync(
                worker,
                "POST",
                "/control/v1/search",
                (request, body) =>
                {
                    AssertNoForwardedBrowserHeaders(request);
                    Assert.NotNull(body);
                },
                new
                {
                    ok = false,
                    items = Array.Empty<object>(),
                    total = 0,
                    offset = 0,
                    limit = 20,
                    errorCode = "requires_authentication"
                });
            using (var response = await SendAuthenticatedAsync(
                client,
                HttpMethod.Get,
                "/api/v1/search?type=group&q=Cached&limit=20",
                sessionCookie))
            {
                await authenticationFallbackExchange;
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var item = Assert.Single(payload.RootElement.GetProperty("items").EnumerateArray());
                Assert.Equal("grp_cached", item.GetProperty("id").GetString());
                Assert.Equal("Cached Group", item.GetProperty("name").GetString());
            }

            var unsafeEntityResponse = ReplyFromWorkerAsync(
                worker,
                "POST",
                "/control/v1/entity",
                (request, body) =>
                {
                    AssertNoForwardedBrowserHeaders(request);
                    Assert.NotNull(body);
                },
                new
                {
                    ok = true,
                    item = new
                    {
                        type = "group",
                        id = "grp_cached",
                        name = "Must Not Escape",
                        ownerId = "usr_owner",
                        description = "Unknown root fields invalidate the response",
                        updatedAt = "2026-11-06T12:00:00Z",
                        payload = new { id = "grp_cached", name = "Must Not Escape" }
                    },
                    errorCode = (string?)null,
                    token = "must-not-cross-the-boundary"
                });
            using (var response = await SendAuthenticatedAsync(
                client,
                HttpMethod.Get,
                "/api/v1/groups/grp_cached",
                sessionCookie))
            {
                await unsafeEntityResponse;
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var body = await response.Content.ReadAsStringAsync();
                using var payload = JsonDocument.Parse(body);
                Assert.Equal("Cached Group", payload.RootElement.GetProperty("name").GetString());
                Assert.DoesNotContain("must-not-cross", body, StringComparison.Ordinal);
                Assert.DoesNotContain("Must Not Escape", body, StringComparison.Ordinal);
            }

            var invalidDocumentResponse = ReplyFromWorkerAsync(
                worker,
                "POST",
                "/control/v1/search",
                (request, body) =>
                {
                    AssertNoForwardedBrowserHeaders(request);
                    Assert.NotNull(body);
                },
                new
                {
                    ok = true,
                    items = new[]
                    {
                        new
                        {
                            type = "group",
                            id = "usr_wrong_prefix",
                            name = "Invalid live group",
                            ownerId = "usr_owner",
                            description = (string?)null,
                            updatedAt = (string?)null,
                            payload = new { id = "usr_wrong_prefix" }
                        }
                    },
                    total = 1,
                    offset = 0,
                    limit = 20,
                    errorCode = (string?)null
                });
            using (var response = await SendAuthenticatedAsync(
                client,
                HttpMethod.Get,
                "/api/v1/search?type=group&q=Cached&limit=20",
                sessionCookie))
            {
                await invalidDocumentResponse;
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.Equal(
                    "grp_cached",
                    Assert.Single(payload.RootElement.GetProperty("items").EnumerateArray()).GetProperty("id").GetString());
            }

            var invalidPayloadResponse = ReplyFromWorkerAsync(
                worker,
                "POST",
                "/control/v1/search",
                (request, body) =>
                {
                    AssertNoForwardedBrowserHeaders(request);
                    Assert.NotNull(body);
                },
                new
                {
                    ok = true,
                    items = new object[]
                    {
                        new
                        {
                            type = "group",
                            id = "grp_cached",
                            name = "Invalid payload group",
                            ownerId = "usr_owner",
                            description = (string?)null,
                            updatedAt = (string?)null,
                            payload = new[] { "objects-only-at-this-boundary" }
                        }
                    },
                    total = 1,
                    offset = 0,
                    limit = 20,
                    errorCode = (string?)null
                });
            using (var response = await SendAuthenticatedAsync(
                client,
                HttpMethod.Get,
                "/api/v1/search?type=group&q=Cached&limit=20",
                sessionCookie))
            {
                await invalidPayloadResponse;
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.Equal(
                    "Cached Group",
                    Assert.Single(payload.RootElement.GetProperty("items").EnumerateArray()).GetProperty("name").GetString());
            }

            var unsafeWorkerResponse = ReplyFromWorkerAsync(
                worker,
                "GET",
                "/control/v1/status",
                (_, body) => Assert.Null(body),
                new
                {
                    ok = true,
                    status = "authenticated",
                    currentUserId = "usr_forward_test",
                    challenge = (object?)null,
                    errorCode = (string?)null,
                    token = "must-not-cross-the-boundary"
                });
            using (var response = await SendAuthenticatedAsync(
                client,
                HttpMethod.Get,
                "/api/v1/vrchat/auth/status",
                sessionCookie))
            {
                await unsafeWorkerResponse;
                Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
                var body = await response.Content.ReadAsStringAsync();
                using var payload = JsonDocument.Parse(body);
                Assert.Equal("vrchat_worker_bad_response", payload.RootElement.GetProperty("error").GetString());
                Assert.DoesNotContain("must-not-cross", body, StringComparison.Ordinal);
            }
        }
        finally
        {
            await StopServerAsync(process);
            worker.Stop();
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task WorkerAnalyticsEndpointsForwardOnlyNamedOriginalDatabaseReads()
    {
        var repositoryRoot = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "vrcx-web-analytics-worker-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var setupTokenBytes = RandomNumberGenerator.GetBytes(32);
        var setupToken = Convert.ToBase64String(setupTokenBytes);
        CryptographicOperations.ZeroMemory(setupTokenBytes);
        var setupTokenPath = Path.Combine(temporaryDirectory, "setup-token");
        await File.WriteAllTextAsync(setupTokenPath, setupToken);

        var workerBaseAddress = new Uri($"http://127.0.0.1:{ReserveTcpPort()}/");
        using var worker = new HttpListener();
        worker.Prefixes.Add(workerBaseAddress.ToString());
        worker.Start();

        var serverBaseAddress = new Uri($"http://127.0.0.1:{ReserveTcpPort()}");
        var output = new ConcurrentQueue<string>();
        using var process = StartServer(
            repositoryRoot,
            temporaryDirectory,
            setupTokenPath,
            serverBaseAddress,
            output,
            workerBaseAddress);
        try
        {
            using var client = new HttpClient
            {
                BaseAddress = serverBaseAddress,
                Timeout = TimeSpan.FromSeconds(5)
            };
            await WaitForServerAsync(client, process, output);

            using var bootstrap = await client.PostAsJsonAsync("/api/v1/auth/bootstrap", new
            {
                password = "administrator password",
                setupToken,
                deviceName = "Analytics worker test"
            });
            Assert.Equal(HttpStatusCode.Created, bootstrap.StatusCode);
            var sessionCookie = bootstrap.Headers.GetValues("Set-Cookie").Single().Split(';', 2)[0];

            var datesExchange = ReplyFromWorkerAsync(
                worker,
                "POST",
                "/control/v1/database-read",
                (request, body) =>
                {
                    AssertDatabaseReadRequest(request, body, "getDateOfInstanceActivity");
                    Assert.Empty(body!.RootElement.GetProperty("arguments").EnumerateObject());
                },
                new
                {
                    ok = true,
                    result = new[] { "2026-11-06T12:00:00Z" },
                    errorCode = (string?)null
                });
            using (var response = await SendAuthenticatedAsync(
                client,
                HttpMethod.Get,
                "/api/v1/analytics/instance-activity/dates",
                sessionCookie))
            {
                await datesExchange;
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.Equal("2026-11-06T12:00:00Z", Assert.Single(payload.RootElement.EnumerateArray()).GetString());
            }

            var activityRow = new
            {
                id = 7,
                created_at = "2026-11-06T12:00:00Z",
                type = "OnPlayerLeft",
                display_name = "Current User",
                location = "wrld_example:123",
                user_id = "usr_current",
                time = 60_000
            };
            var activityExchange = ReplyFromWorkerAsync(
                worker,
                "POST",
                "/control/v1/database-read",
                (request, body) =>
                {
                    AssertDatabaseReadRequest(request, body, "getInstanceActivity");
                    var arguments = body!.RootElement.GetProperty("arguments");
                    Assert.Equal(2, arguments.EnumerateObject().Count());
                    Assert.Equal("2026-11-06T00:00:00Z", arguments.GetProperty("startDate").GetString());
                    Assert.Equal("2026-11-06T23:59:59Z", arguments.GetProperty("endDate").GetString());
                },
                new
                {
                    ok = true,
                    result = new
                    {
                        currentUserData = new[] { activityRow },
                        detailData = new[]
                        {
                            new { location = "wrld_example:123", rows = new[] { activityRow } }
                        }
                    },
                    errorCode = (string?)null
                });
            using (var response = await SendAuthenticatedAsync(
                client,
                HttpMethod.Get,
                "/api/v1/analytics/instance-activity?startDate=2026-11-06T00%3A00%3A00Z&endDate=2026-11-06T23%3A59%3A59Z",
                sessionCookie))
            {
                await activityExchange;
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var row = Assert.Single(payload.RootElement.GetProperty("currentUserData").EnumerateArray());
                Assert.Equal("OnPlayerLeft", row.GetProperty("type").GetString());
                Assert.True(row.TryGetProperty("created_at", out _));
                Assert.True(row.TryGetProperty("display_name", out _));
                Assert.True(row.TryGetProperty("user_id", out _));
            }

            var hotWorldsExchange = ReplyFromWorkerAsync(
                worker,
                "POST",
                "/control/v1/database-read",
                (request, body) =>
                {
                    AssertDatabaseReadRequest(request, body, "getHotWorlds");
                    var arguments = body!.RootElement.GetProperty("arguments");
                    Assert.Equal(30, arguments.GetProperty("days").GetInt32());
                    Assert.Equal(20, arguments.GetProperty("limit").GetInt32());
                },
                new
                {
                    ok = true,
                    result = new[]
                    {
                        new
                        {
                            worldId = "wrld_example",
                            worldName = "Example World",
                            visitCount = 4,
                            uniqueFriends = 2,
                            lastVisited = "2026-11-06T12:00:00Z",
                            trend = "rising"
                        }
                    },
                    errorCode = (string?)null
                });
            using (var response = await SendAuthenticatedAsync(
                client,
                HttpMethod.Get,
                "/api/v1/analytics/hot-worlds?days=30&limit=20",
                sessionCookie))
            {
                await hotWorldsExchange;
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.Equal("wrld_example", Assert.Single(payload.RootElement.EnumerateArray()).GetProperty("worldId").GetString());
            }

            var hotWorldFriendsExchange = ReplyFromWorkerAsync(
                worker,
                "POST",
                "/control/v1/database-read",
                (request, body) =>
                {
                    AssertDatabaseReadRequest(request, body, "getHotWorldFriendDetail");
                    var arguments = body!.RootElement.GetProperty("arguments");
                    Assert.Equal("wrld_example", arguments.GetProperty("worldId").GetString());
                    Assert.Equal(30, arguments.GetProperty("days").GetInt32());
                },
                new
                {
                    ok = true,
                    result = new[]
                    {
                        new
                        {
                            userId = "usr_friend",
                            displayName = "Friend",
                            visitCount = 2,
                            lastVisit = "2026-11-06T12:00:00Z"
                        }
                    },
                    errorCode = (string?)null
                });
            using (var response = await SendAuthenticatedAsync(
                client,
                HttpMethod.Get,
                "/api/v1/analytics/hot-worlds/wrld_example/friends?days=30",
                sessionCookie))
            {
                await hotWorldFriendsExchange;
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            var snapshotExchange = ReplyFromWorkerAsync(
                worker,
                "POST",
                "/control/v1/database-read",
                (request, body) => AssertDatabaseReadRequest(request, body, "getMutualGraphSnapshot"),
                new
                {
                    ok = true,
                    result = new[] { new { friendId = "usr_friend", mutualIds = new[] { "usr_mutual" } } },
                    errorCode = (string?)null
                });
            using (var response = await SendAuthenticatedAsync(
                client,
                HttpMethod.Get,
                "/api/v1/analytics/mutual-graph/snapshot",
                sessionCookie))
            {
                await snapshotExchange;
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            var metaExchange = ReplyFromWorkerAsync(
                worker,
                "POST",
                "/control/v1/database-read",
                (request, body) => AssertDatabaseReadRequest(request, body, "getMutualGraphMeta"),
                new
                {
                    ok = true,
                    result = new[]
                    {
                        new
                        {
                            friendId = "usr_friend",
                            lastFetchedAt = "2026-11-06T12:00:00Z",
                            optedOut = false
                        }
                    },
                    errorCode = (string?)null
                });
            using (var response = await SendAuthenticatedAsync(
                client,
                HttpMethod.Get,
                "/api/v1/analytics/mutual-graph/meta",
                sessionCookie))
            {
                await metaExchange;
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            foreach (var path in new[]
            {
                "/api/v1/analytics/instance-activity?startDate=2026-01-01T00%3A00%3A00Z&endDate=2026-11-06T00%3A00%3A00Z",
                "/api/v1/analytics/hot-worlds?days=0&limit=30",
                "/api/v1/analytics/hot-worlds/usr_wrong/friends?days=30"
            })
            {
                using var response = await SendAuthenticatedAsync(client, HttpMethod.Get, path, sessionCookie);
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            }

            var unsafeExchange = ReplyFromWorkerAsync(
                worker,
                "POST",
                "/control/v1/database-read",
                (request, body) => AssertDatabaseReadRequest(request, body, "getMutualGraphSnapshot"),
                new
                {
                    ok = true,
                    result = new[]
                    {
                        new { friendId = "usr_friend", mutualIds = new[] { "usr_mutual" }, cookie = "secret" }
                    },
                    errorCode = (string?)null
                });
            using (var response = await SendAuthenticatedAsync(
                client,
                HttpMethod.Get,
                "/api/v1/analytics/mutual-graph/snapshot",
                sessionCookie))
            {
                await unsafeExchange;
                Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                Assert.DoesNotContain("secret", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }
        }
        finally
        {
            await StopServerAsync(process);
            worker.Stop();
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static void AssertDatabaseReadRequest(
        HttpListenerRequest request,
        JsonDocument? body,
        string expectedName)
    {
        AssertNoForwardedBrowserHeaders(request);
        Assert.NotNull(body);
        Assert.Equal(2, body.RootElement.EnumerateObject().Count());
        Assert.Equal(expectedName, body.RootElement.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Object, body.RootElement.GetProperty("arguments").ValueKind);
    }

    private static async Task ReplyFromWorkerAsync(
        HttpListener listener,
        string expectedMethod,
        string expectedPath,
        Action<HttpListenerRequest, JsonDocument?> inspectRequest,
        object responsePayload,
        HttpStatusCode responseStatus = HttpStatusCode.OK)
    {
        var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(5));
        JsonDocument? body = null;
        try
        {
            if (context.Request.HasEntityBody)
            {
                body = await JsonDocument.ParseAsync(context.Request.InputStream);
            }

            Assert.Equal(expectedMethod, context.Request.HttpMethod);
            Assert.Equal(expectedPath, context.Request.Url?.AbsolutePath);
            inspectRequest(context.Request, body);
        }
        finally
        {
            var responseBytes = JsonSerializer.SerializeToUtf8Bytes(responsePayload);
            context.Response.StatusCode = (int)responseStatus;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = responseBytes.Length;
            await context.Response.OutputStream.WriteAsync(responseBytes);
            context.Response.Close();
            body?.Dispose();
        }
    }

    private static void AssertNoForwardedBrowserHeaders(HttpListenerRequest request)
    {
        Assert.Null(request.Headers["Authorization"]);
        Assert.Null(request.Headers["Cookie"]);
        Assert.Null(request.Headers["X-Worker-Endpoint"]);
    }

    private static async Task CreateFallbackReadModelAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE vrcx_web_entity_snapshot (
                entity_type TEXT NOT NULL,
                entity_id TEXT NOT NULL,
                display_name TEXT NOT NULL,
                owner_id TEXT,
                description TEXT,
                updated_at TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                PRIMARY KEY(entity_type, entity_id)
            );
            INSERT INTO vrcx_web_entity_snapshot VALUES (
                'group',
                'grp_cached',
                'Cached Group',
                'usr_owner',
                'Projection fallback',
                '2026-11-06T10:00:00Z',
                '{"id":"grp_cached","name":"Cached Group","ownerId":"usr_owner"}'
            );
            """;
        await command.ExecuteNonQueryAsync();
    }
}
