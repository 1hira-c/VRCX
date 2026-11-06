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
        }
        finally
        {
            await StopServerAsync(process);
            worker.Stop();
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }
}
