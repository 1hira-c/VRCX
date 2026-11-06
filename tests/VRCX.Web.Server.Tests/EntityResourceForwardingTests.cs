using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace VRCX.Web.Server.Tests;

public sealed partial class ReadApiEndpointTests
{
    [Fact]
    public async Task EntityResourceEndpointsForwardOnlyFixedTypedReadsAndRejectSecrets()
    {
        var repositoryRoot = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "vrcx-web-entity-resource-tests",
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
                deviceName = "Entity resource worker test"
            });
            Assert.Equal(HttpStatusCode.Created, bootstrap.StatusCode);
            var sessionCookie = bootstrap.Headers.GetValues("Set-Cookie").Single().Split(';', 2)[0];

            var groupsExchange = ReplyFromWorkerAsync(
                worker,
                "POST",
                "/control/v1/entity-resource",
                (request, body) =>
                {
                    AssertNoForwardedBrowserHeaders(request);
                    Assert.NotNull(body);
                    Assert.Equal(2, body.RootElement.EnumerateObject().Count());
                    Assert.Equal("userGroups", body.RootElement.GetProperty("name").GetString());
                    var arguments = body.RootElement.GetProperty("arguments");
                    Assert.Single(arguments.EnumerateObject());
                    Assert.Equal("usr_example", arguments.GetProperty("userId").GetString());
                },
                new
                {
                    ok = true,
                    result = new[]
                    {
                        new
                        {
                            id = "grp_example",
                            name = "Example Group",
                            ownerId = "usr_owner",
                            memberCount = 42,
                            myMember = new { membershipStatus = "member", visibility = "visible" }
                        }
                    },
                    errorCode = (string?)null
                });
            using (var response = await SendAuthenticatedAsync(
                client,
                HttpMethod.Get,
                "/api/v1/users/usr_example/groups",
                sessionCookie))
            {
                await groupsExchange;
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var group = Assert.Single(payload.RootElement.EnumerateArray());
                Assert.Equal("grp_example", group.GetProperty("id").GetString());
                Assert.Equal("Example Group", group.GetProperty("name").GetString());
                Assert.Equal("usr_owner", group.GetProperty("ownerId").GetString());
            }

            var secretExchange = ReplyFromWorkerAsync(
                worker,
                "POST",
                "/control/v1/entity-resource",
                (_, body) =>
                {
                    Assert.NotNull(body);
                    Assert.Equal("userPublicProfile", body.RootElement.GetProperty("name").GetString());
                },
                new
                {
                    ok = true,
                    result = new
                    {
                        id = "usr_example",
                        displayName = "Example User",
                        nested = new { authToken = "must-not-escape" }
                    },
                    errorCode = (string?)null
                });
            using (var response = await SendAuthenticatedAsync(
                client,
                HttpMethod.Get,
                "/api/v1/users/usr_example/public-profile",
                sessionCookie))
            {
                await secretExchange;
                Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                Assert.DoesNotContain(
                    "must-not-escape",
                    await response.Content.ReadAsStringAsync(),
                    StringComparison.Ordinal);
            }

            using var privateInstance = await SendAuthenticatedAsync(
                client,
                HttpMethod.Get,
                "/api/v1/worlds/wrld_example/instances/123~private(usr_secret)",
                sessionCookie);
            Assert.Equal(HttpStatusCode.BadRequest, privateInstance.StatusCode);

            // The original provider returns a complete author lookup once,
            // rather than pages which repeat the same provider request.
            var remoteAvatars = Enumerable.Range(0, 5_000).Select(index => new
            {
                id = $"avtr_remote_{index}",
                name = $"Remote avatar {index}",
                authorId = "usr_example",
                authorName = "Example User",
                description = new string('d', 600),
                imageUrl = "https://example.invalid/avatar.png",
                thumbnailImageUrl = "https://example.invalid/thumb.png",
                releaseStatus = "public",
                created_at = "2026-11-06T00:00:00Z",
                updated_at = "2026-11-06T00:00:00Z"
            }).ToArray();
            var remoteExchange = ReplyFromWorkerAsync(worker, "POST", "/control/v1/entity-resource", (_, body) =>
            {
                Assert.NotNull(body);
                Assert.Equal("userRemoteAvatars", body.RootElement.GetProperty("name").GetString());
                var arguments = body.RootElement.GetProperty("arguments");
                Assert.Single(arguments.EnumerateObject());
                Assert.Equal("usr_example", arguments.GetProperty("userId").GetString());
            }, new { ok = true, result = new { items = remoteAvatars, warningCode = (string?)null }, errorCode = (string?)null });
            using (var remote = await SendAuthenticatedAsync(
                client, HttpMethod.Get, "/api/v1/users/usr_example/avatars/remote", sessionCookie))
            {
                await remoteExchange;
                Assert.Equal(HttpStatusCode.OK, remote.StatusCode);
                using var payload = JsonDocument.Parse(await remote.Content.ReadAsStringAsync());
                Assert.Equal(5_000, payload.RootElement.GetProperty("items").GetArrayLength());
                Assert.Equal("avtr_remote_4999", payload.RootElement.GetProperty("items")[4_999].GetProperty("id").GetString());
                Assert.Equal(JsonValueKind.Null, payload.RootElement.GetProperty("warningCode").ValueKind);
            }

            var partialExchange = ReplyFromWorkerAsync(worker, "POST", "/control/v1/entity-resource", (_, _) => { },
                new { ok = true, result = new { items = remoteAvatars.Take(2).ToArray(),
                    warningCode = "avatar_provider_partial_failure" }, errorCode = (string?)null });
            using (var partial = await SendAuthenticatedAsync(
                client, HttpMethod.Get, "/api/v1/users/usr_example/avatars/remote", sessionCookie))
            {
                await partialExchange;
                Assert.Equal(HttpStatusCode.OK, partial.StatusCode);
                using var payload = JsonDocument.Parse(await partial.Content.ReadAsStringAsync());
                Assert.Equal(2, payload.RootElement.GetProperty("items").GetArrayLength());
                Assert.Equal("avatar_provider_partial_failure", payload.RootElement.GetProperty("warningCode").GetString());
            }

            var unsafeWarningExchange = ReplyFromWorkerAsync(worker, "POST", "/control/v1/entity-resource", (_, _) => { },
                new { ok = true, result = new { items = Array.Empty<object>(),
                    warningCode = "https://provider.invalid/secret-must-not-escape" }, errorCode = (string?)null });
            using (var unsafeWarning = await SendAuthenticatedAsync(
                client, HttpMethod.Get, "/api/v1/users/usr_example/avatars/remote", sessionCookie))
            {
                await unsafeWarningExchange;
                Assert.Equal(HttpStatusCode.ServiceUnavailable, unsafeWarning.StatusCode);
                Assert.DoesNotContain("secret-must-not-escape", await unsafeWarning.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }

            foreach (var errorCode in new[] { "avatar_remote_database_disabled", "avatar_remote_database_limit_exceeded" })
            {
                var failureExchange = ReplyFromWorkerAsync(worker, "POST", "/control/v1/entity-resource", (_, _) => { },
                    new { ok = false, result = (object?)null, errorCode });
                using var failure = await SendAuthenticatedAsync(
                    client, HttpMethod.Get, "/api/v1/users/usr_example/avatars/remote", sessionCookie);
                await failureExchange;
                Assert.Equal(HttpStatusCode.ServiceUnavailable, failure.StatusCode);
                using var payload = JsonDocument.Parse(await failure.Content.ReadAsStringAsync());
                Assert.Equal(errorCode, payload.RootElement.GetProperty("error").GetString());
            }

            var excessiveExchange = ReplyFromWorkerAsync(worker, "POST", "/control/v1/entity-resource", (_, _) => { },
                new { ok = true, result = new { items = remoteAvatars.Append(remoteAvatars[0]).ToArray(),
                    warningCode = (string?)null }, errorCode = (string?)null });
            using (var excessive = await SendAuthenticatedAsync(
                client, HttpMethod.Get, "/api/v1/users/usr_example/avatars/remote", sessionCookie))
            {
                await excessiveExchange;
                Assert.Equal(HttpStatusCode.ServiceUnavailable, excessive.StatusCode);
                using var payload = JsonDocument.Parse(await excessive.Content.ReadAsStringAsync());
                Assert.Equal("avatar_remote_database_limit_exceeded", payload.RootElement.GetProperty("error").GetString());
            }

            var searchItems = remoteAvatars.Select(avatar => new
            {
                type = "avatar", avatar.id, name = avatar.name, ownerId = avatar.authorId,
                description = avatar.description, updatedAt = (string?)null, payload = avatar
            }).ToArray();
            var searchExchange = ReplyFromWorkerAsync(worker, "POST", "/control/v1/search", (_, body) =>
            {
                Assert.NotNull(body);
                Assert.Equal("avatar", body.RootElement.GetProperty("type").GetString());
                Assert.Equal(0, body.RootElement.GetProperty("offset").GetInt32());
                Assert.Equal(5_000, body.RootElement.GetProperty("limit").GetInt32());
            }, new { ok = true, items = searchItems, total = 5_000, offset = 0, limit = 5_000, errorCode = (string?)null });
            using (var search = await SendAuthenticatedAsync(
                client, HttpMethod.Get, "/api/v1/search?type=avatar&q=remote&limit=5000", sessionCookie))
            {
                await searchExchange;
                Assert.Equal(HttpStatusCode.OK, search.StatusCode);
                using var payload = JsonDocument.Parse(await search.Content.ReadAsStringAsync());
                Assert.Equal(5_000, payload.RootElement.GetProperty("items").GetArrayLength());
                Assert.Equal(5_000, payload.RootElement.GetProperty("limit").GetInt32());
            }

            foreach (var errorCode in new[] { "avatar_remote_database_disabled", "avatar_remote_database_limit_exceeded",
                "upstream_error", "network_error", "request_aborted", "http_429", "http_503" })
            {
                var failureExchange = ReplyFromWorkerAsync(worker, "POST", "/control/v1/search", (_, _) => { },
                    new { ok = false, items = Array.Empty<object>(), total = 0, offset = 0, limit = 5_000, errorCode });
                using var failure = await SendAuthenticatedAsync(
                    client, HttpMethod.Get, "/api/v1/search?type=avatar&q=remote&limit=5000", sessionCookie);
                await failureExchange;
                Assert.Equal(HttpStatusCode.ServiceUnavailable, failure.StatusCode);
                using var payload = JsonDocument.Parse(await failure.Content.ReadAsStringAsync());
                Assert.Equal(errorCode, payload.RootElement.GetProperty("error").GetString());
            }

            foreach (var unavailable in new[] { false, true })
            {
                object reply = unavailable
                    ? new { ok = false, status = "requires_authentication", currentUserId = (string?)null,
                        challenge = (object?)null, errorCode = "renderer_unavailable" }
                    : new { ok = false, items = Array.Empty<object>(), total = 0, offset = 0, limit = 5_000,
                        errorCode = "requires_authentication" };
                var fallbackExchange = ReplyFromWorkerAsync(worker, "POST", "/control/v1/search", (_, _) => { }, reply,
                    unavailable ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
                using var fallback = await SendAuthenticatedAsync(
                    client, HttpMethod.Get, "/api/v1/search?type=avatar&q=remote&limit=5000", sessionCookie);
                await fallbackExchange;
                Assert.Equal(HttpStatusCode.OK, fallback.StatusCode);
            }

            // The larger provider response is allowed only for avatar lookup.
            var userSearchExchange = ReplyFromWorkerAsync(worker, "POST", "/control/v1/search", (_, body) =>
            {
                Assert.NotNull(body);
                Assert.Equal("user", body.RootElement.GetProperty("type").GetString());
                Assert.Equal(200, body.RootElement.GetProperty("limit").GetInt32());
            }, new { ok = true, items = Array.Empty<object>(), total = 0, offset = 0, limit = 200, errorCode = (string?)null });
            using (var userSearch = await SendAuthenticatedAsync(
                client, HttpMethod.Get, "/api/v1/search?type=user&q=example&limit=5000", sessionCookie))
            {
                await userSearchExchange;
                Assert.Equal(HttpStatusCode.OK, userSearch.StatusCode);
            }
        }
        finally
        {
            await StopServerAsync(process);
            worker.Stop();
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }
}
