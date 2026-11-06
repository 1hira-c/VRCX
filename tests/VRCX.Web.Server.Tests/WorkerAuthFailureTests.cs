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
    public async Task AuthenticationTimeoutsAndBadRepliesAreNotReportedAsWorkerOutages()
    {
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(), "vrcx-web-worker-failure-tests", Guid.NewGuid().ToString("N"));
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
                deviceName = "Worker failure test"
            });
            Assert.Equal(HttpStatusCode.Created, bootstrap.StatusCode);
            var cookie = bootstrap.Headers.GetValues("Set-Cookie").Single().Split(';', 2)[0];
            var cases = new[]
            {
                (HttpStatusCode.GatewayTimeout, "renderer_timeout", false, HttpStatusCode.GatewayTimeout, "vrchat_worker_timeout", (string?)null),
                (HttpStatusCode.ServiceUnavailable, "renderer_unavailable", false, HttpStatusCode.ServiceUnavailable, "vrchat_worker_unavailable", (string?)null),
                (HttpStatusCode.ServiceUnavailable, "database_initialization_failed", false, HttpStatusCode.ServiceUnavailable, "vrchat_worker_unavailable", "database_initialization_failed"),
                (HttpStatusCode.BadGateway, "renderer_unavailable", false, HttpStatusCode.BadGateway, "vrchat_worker_bad_response", (string?)null),
                (HttpStatusCode.InternalServerError, "renderer_timeout", false, HttpStatusCode.BadGateway, "vrchat_worker_bad_response", (string?)null),
                (HttpStatusCode.ServiceUnavailable, "unknown_failure", false, HttpStatusCode.BadGateway, "vrchat_worker_bad_response", (string?)null),
                (HttpStatusCode.ServiceUnavailable, "database_initialization_failed", true, HttpStatusCode.BadGateway, "vrchat_worker_bad_response", (string?)null)
            };
            foreach (var (workerHttpStatus, workerError, includeUnsafeField, expectedStatus, expectedError, reason) in cases)
            {
                var workerPayload = new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["status"] = "requires_authentication",
                    ["currentUserId"] = null,
                    ["challenge"] = null,
                    ["errorCode"] = workerError
                };
                if (includeUnsafeField) workerPayload["password"] = "must-not-cross-the-boundary";
                var exchange = ReplyFromWorkerAsync(worker, "POST", "/control/v1/login",
                    (_, body) => Assert.NotNull(body), workerPayload, workerHttpStatus);
                using var response = await SendAuthenticatedJsonAsync(client, "/api/v1/vrchat/auth/login", cookie,
                    new { username = "test-user", password = "test-password" });
                await exchange;

                Assert.Equal(expectedStatus, response.StatusCode);
                var body = await response.Content.ReadAsStringAsync();
                using var payload = JsonDocument.Parse(body);
                Assert.Equal(expectedError, payload.RootElement.GetProperty("error").GetString());
                if (reason is null)
                {
                    Assert.Single(payload.RootElement.EnumerateObject());
                }
                else
                {
                    Assert.Equal(reason, payload.RootElement.GetProperty("reason").GetString());
                    Assert.Equal(2, payload.RootElement.EnumerateObject().Count());
                }
                Assert.DoesNotContain("must-not-cross", body, StringComparison.Ordinal);
                Assert.DoesNotContain("test-password", body, StringComparison.Ordinal);
                Assert.DoesNotContain("127.0.0.1", body, StringComparison.Ordinal);
            }
        }
        finally
        {
            await StopServerAsync(process);
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }
}
