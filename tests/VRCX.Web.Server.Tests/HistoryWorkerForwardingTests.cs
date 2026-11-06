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
    public async Task HistoryEndpointsForwardOnlyFixedOriginalDatabaseReads()
    {
        var repositoryRoot = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "vrcx-web-history-worker-tests",
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
                deviceName = "History worker test"
            });
            Assert.Equal(HttpStatusCode.Created, bootstrap.StatusCode);
            var sessionCookie = bootstrap.Headers.GetValues("Set-Cookie").Single().Split(';', 2)[0];

            var feedExchange = ReplyFromWorkerAsync(
                worker,
                "POST",
                "/control/v1/database-read",
                (request, body) =>
                {
                    AssertDatabaseReadRequest(request, body, "searchFeedDatabase");
                    var arguments = body!.RootElement.GetProperty("arguments");
                    Assert.Equal(6, arguments.EnumerateObject().Count());
                    Assert.Equal("public", arguments.GetProperty("search").GetString());
                    Assert.Equal("Avatar", Assert.Single(arguments.GetProperty("filters").EnumerateArray()).GetString());
                    Assert.True(arguments.GetProperty("favoritesOnly").GetBoolean());
                    Assert.Equal(50_000, arguments.GetProperty("maxEntries").GetInt32());
                    Assert.Equal("2026-11-01T00:00:00Z", arguments.GetProperty("dateFrom").GetString());
                    Assert.Equal("2026-11-06T00:00:00Z", arguments.GetProperty("dateTo").GetString());
                },
                new
                {
                    ok = true,
                    result = new[]
                    {
                        new
                        {
                            rowId = 7,
                            created_at = "2026-11-06T00:00:00Z",
                            userId = "usr_example",
                            displayName = "Example User",
                            type = "Avatar",
                            location = (string?)null,
                            worldName = (string?)null,
                            previousLocation = (string?)null,
                            time = (double?)null,
                            groupName = (string?)null,
                            status = (string?)null,
                            statusDescription = (string?)null,
                            previousStatus = (string?)null,
                            previousStatusDescription = (string?)null,
                            bio = (string?)null,
                            previousBio = (string?)null,
                            bioLinks = Array.Empty<string>(),
                            previousBioLinks = Array.Empty<string>(),
                            ownerId = "usr_example",
                            avatarName = "Example Avatar",
                            currentAvatarImageUrl = "https://example.invalid/current.png",
                            currentAvatarThumbnailImageUrl = "https://example.invalid/current-thumb.png",
                            previousCurrentAvatarImageUrl = "https://example.invalid/previous.png",
                            previousCurrentAvatarThumbnailImageUrl = "https://example.invalid/previous-thumb.png"
                        }
                    },
                    errorCode = (string?)null
                });
            using (var response = await SendAuthenticatedAsync(
                client,
                HttpMethod.Get,
                "/api/v1/history-native/feed?search=public&filters=Avatar&favoritesOnly=true&maxEntries=50000" +
                "&dateFrom=2026-11-01T00%3A00%3A00Z&dateTo=2026-11-06T00%3A00%3A00Z",
                sessionCookie))
            {
                await feedExchange;
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var row = Assert.Single(payload.RootElement.EnumerateArray());
                Assert.Equal("Avatar", row.GetProperty("type").GetString());
                Assert.Equal(
                    "https://example.invalid/current.png",
                    row.GetProperty("currentAvatarImageUrl").GetString());
            }

            using (var invalidRange = await SendAuthenticatedAsync(
                client,
                HttpMethod.Get,
                "/api/v1/history-native/feed?maxEntries=500&dateFrom=2026-11-07T00%3A00%3A00Z" +
                "&dateTo=2026-11-06T00%3A00%3A00Z",
                sessionCookie))
            {
                Assert.Equal(HttpStatusCode.BadRequest, invalidRange.StatusCode);
            }

            var gameLogExchange = ReplyFromWorkerAsync(
                worker,
                "POST",
                "/control/v1/database-read",
                (request, body) =>
                {
                    AssertDatabaseReadRequest(request, body, "searchGameLogDatabase");
                    var arguments = body!.RootElement.GetProperty("arguments");
                    Assert.Equal("Example World", arguments.GetProperty("search").GetString());
                    Assert.Equal("Location", Assert.Single(arguments.GetProperty("filters").EnumerateArray()).GetString());
                    Assert.False(arguments.GetProperty("favoritesOnly").GetBoolean());
                    Assert.Equal(50_000, arguments.GetProperty("maxEntries").GetInt32());
                },
                new
                {
                    ok = true,
                    result = new[]
                    {
                        new
                        {
                            rowId = 8,
                            created_at = "2026-11-06T01:00:00Z",
                            type = "Location",
                            displayName = (string?)null,
                            location = "wrld_example:123",
                            userId = (string?)null,
                            time = 120_000d,
                            worldId = "wrld_example",
                            worldName = "Example World",
                            groupName = (string?)null,
                            instanceId = (string?)null,
                            videoUrl = (string?)null,
                            videoName = (string?)null,
                            videoId = (string?)null,
                            resourceUrl = (string?)null,
                            resourceType = (string?)null,
                            data = (string?)null,
                            message = (string?)null
                        }
                    },
                    errorCode = (string?)null
                });
            using (var response = await SendAuthenticatedAsync(
                client,
                HttpMethod.Get,
                "/api/v1/history-native/game-log?search=Example%20World&filters=Location" +
                "&favoritesOnly=false&maxEntries=50000",
                sessionCookie))
            {
                await gameLogExchange;
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var row = Assert.Single(payload.RootElement.EnumerateArray());
                Assert.Equal("wrld_example:123", row.GetProperty("location").GetString());
                Assert.Equal(120_000d, row.GetProperty("time").GetDouble());
            }

            var mismatchedMemoExchange = ReplyFromWorkerAsync(
                worker,
                "POST",
                "/control/v1/database-read",
                (request, body) =>
                {
                    AssertDatabaseReadRequest(request, body, "getUserMemo");
                    Assert.Equal(
                        "usr_expected",
                        body!.RootElement.GetProperty("arguments").GetProperty("userId").GetString());
                },
                new
                {
                    ok = true,
                    result = new { userId = "usr_other", editedAt = (string?)null, memo = "must not escape" },
                    errorCode = (string?)null
                });
            using (var response = await SendAuthenticatedAsync(
                client,
                HttpMethod.Get,
                "/api/v1/history-native/memos/user/usr_expected",
                sessionCookie))
            {
                await mismatchedMemoExchange;
                Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                Assert.DoesNotContain("must not escape", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
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
