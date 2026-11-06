using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Xunit;

namespace VRCX.Web.Server.Tests;

public sealed partial class ReadApiEndpointTests
{
    [Fact]
    public async Task StatusPollingDoesNotConsumeOrDependOnTheLoginAndTwoFactorBudget()
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
        }
        finally
        {
            await StopServerAsync(process);
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }
}
