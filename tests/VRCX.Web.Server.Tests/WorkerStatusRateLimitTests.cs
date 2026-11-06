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
    public async Task StatusPollingAndBrowsingDoNotConsumeOrDependOnTheLoginAndTwoFactorBudget()
    {
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(), "vrcx-web-worker-rate-tests", Guid.NewGuid().ToString("N"));
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
        var output = new ConcurrentQueue<string>();
        var serverBaseAddress = new Uri($"http://127.0.0.1:{ReserveTcpPort()}");
        using var process = StartServer(
            FindRepositoryRoot(), temporaryDirectory, setupTokenPath, serverBaseAddress, output, workerBaseAddress);
        try
        {
            using var client = new HttpClient { BaseAddress = serverBaseAddress, Timeout = TimeSpan.FromSeconds(5) };
            await WaitForServerAsync(client, process, output);
            using var bootstrap = await client.PostAsJsonAsync("/api/v1/auth/bootstrap", new
            {
                password = "administrator password",
                setupToken,
                deviceName = "Status budget test"
            });
            Assert.Equal(HttpStatusCode.Created, bootstrap.StatusCode);
            var cookie = bootstrap.Headers.GetValues("Set-Cookie").Single().Split(';', 2)[0];
            var workerStatus = new
            {
                ok = true,
                status = "requires_authentication",
                currentUserId = (string?)null,
                challenge = (object?)null,
                errorCode = (string?)null
            };

            // Several tabs reconnecting can exceed 30 local status reads in a
            // minute. None of these may spend a real authentication permit.
            for (var request = 0; request < 64; request++)
            {
                var exchange = ReplyFromWorkerAsync(worker, "GET", "/control/v1/status",
                    (_, body) => Assert.Null(body), workerStatus);
                using var response = await SendAuthenticatedAsync(
                    client, HttpMethod.Get, "/api/v1/vrchat/auth/status", cookie);
                await exchange;
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            var reads = new (string Path, string WorkerPath, object Reply)[]
            {
                ("/api/v1/users/usr_example/groups", "/control/v1/entity-resource",
                    new { ok = true, result = Array.Empty<object>(), errorCode = (string?)null }),
                ("/api/v1/search?type=user&q=example&limit=1", "/control/v1/search",
                    new { ok = true, items = Array.Empty<object>(), total = 0, offset = 0, limit = 1, errorCode = (string?)null }),
                ("/api/v1/users/usr_example", "/control/v1/entity",
                    new { ok = true, item = new { type = "user", id = "usr_example", name = "Example",
                        ownerId = (string?)null, description = (string?)null, updatedAt = (string?)null,
                        payload = new { id = "usr_example", displayName = "Example" } }, errorCode = (string?)null }),
                ("/api/v1/history-native/feed", "/control/v1/database-read",
                    new { ok = true, result = Array.Empty<object>(), errorCode = (string?)null }),
                ("/api/v1/analytics/instance-activity/dates", "/control/v1/database-read",
                    new { ok = true, result = Array.Empty<string>(), errorCode = (string?)null })
            };
            async Task ReadAsync(int index)
            {
                var read = reads[index % reads.Length];
                var exchange = ReplyFromWorkerAsync(worker, "POST", read.WorkerPath, (_, _) => { }, read.Reply);
                using var response = await SendAuthenticatedAsync(client, HttpMethod.Get, read.Path, cookie);
                await exchange;
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
            // Profile tabs, search, and refreshes together readily exceed the
            // old shared 30/minute budget, even without local status polling.
            for (var request = 0; request < 64; request++) await ReadAsync(request);

            // Multiple profiles and the initial refresh can run together. The
            // browsing cap still bounds outstanding work without blocking 2FA.
            var pendingReads = new List<Task<HttpResponseMessage>>();
            var heldReads = new List<HttpListenerContext>();
            for (var index = 0; index < 32; index++)
            {
                pendingReads.Add(SendAuthenticatedAsync(
                    client, HttpMethod.Get, "/api/v1/users/usr_example/groups", cookie));
                var context = await worker.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal("/control/v1/entity-resource", context.Request.Url!.AbsolutePath);
                heldReads.Add(context);
            }
            using (var overloaded = await SendAuthenticatedAsync(
                client, HttpMethod.Get, "/api/v1/users/usr_example/groups", cookie))
            {
                Assert.Equal(HttpStatusCode.TooManyRequests, overloaded.StatusCode);
            }

            var loginExchange = ReplyFromWorkerAsync(worker, "POST", "/control/v1/login", (_, _) => { }, new
            {
                ok = true,
                status = "two_factor_required",
                currentUserId = (string?)null,
                challenge = new { kinds = new[] { "totp" } },
                errorCode = (string?)null
            });
            using (var login = await SendAuthenticatedJsonAsync(client, "/api/v1/vrchat/auth/login", cookie,
                new { username = "test-user", password = "test-password" }))
            {
                await loginExchange;
                Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            }

            var twoFactorExchange = ReplyFromWorkerAsync(worker, "POST", "/control/v1/two-factor", (_, _) => { }, new
            {
                ok = true,
                status = "authenticated",
                currentUserId = "usr_rate_limit_test",
                challenge = (object?)null,
                errorCode = (string?)null
            });
            using (var twoFactor = await SendAuthenticatedJsonAsync(client, "/api/v1/vrchat/auth/two-factor", cookie,
                new { kind = "totp", code = "123456" }))
            {
                await twoFactorExchange;
                Assert.Equal(HttpStatusCode.OK, twoFactor.StatusCode);
            }

            var readReply = JsonSerializer.SerializeToUtf8Bytes(
                new { ok = true, result = Array.Empty<object>(), errorCode = (string?)null });
            foreach (var context in heldReads)
            {
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = readReply.Length;
                await context.Response.OutputStream.WriteAsync(readReply);
                context.Response.Close();
            }
            foreach (var pending in pendingReads)
            {
                using var response = await pending;
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            // Authentication still has its own 30/minute protection. Invalid
            // commands consume a permit without making any upstream request.
            for (var request = 0; request < 28; request++)
            {
                using var invalidLogin = await SendAuthenticatedJsonAsync(client, "/api/v1/vrchat/auth/login", cookie,
                    new { username = "", password = "test-password" });
                Assert.Equal(HttpStatusCode.BadRequest, invalidLogin.StatusCode);
            }
            using (var throttled = await SendAuthenticatedJsonAsync(client, "/api/v1/vrchat/auth/login", cookie,
                new { username = "", password = "test-password" }))
            {
                Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
            }

            // Exhausting the mutation budget must not block state recovery.
            var finalExchange = ReplyFromWorkerAsync(worker, "GET", "/control/v1/status",
                (_, body) => Assert.Null(body), workerStatus);
            using var finalStatus = await SendAuthenticatedAsync(
                client, HttpMethod.Get, "/api/v1/vrchat/auth/status", cookie);
            await finalExchange;
            Assert.Equal(HttpStatusCode.OK, finalStatus.StatusCode);
            for (var index = 0; index < reads.Length; index++) await ReadAsync(index);
        }
        finally
        {
            await StopServerAsync(process);
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }
}
