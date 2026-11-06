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
    public async Task AdministratorAuthLifecycleUsesHardenedCookiesOriginChecksAndSingleUsePairing()
    {
        var repositoryRoot = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "vrcx-web-admin-auth-endpoint",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var setupTokenBytes = RandomNumberGenerator.GetBytes(32);
        var setupToken = Convert.ToBase64String(setupTokenBytes);
        CryptographicOperations.ZeroMemory(setupTokenBytes);
        var setupTokenPath = Path.Combine(temporaryDirectory, "setup-token");
        await File.WriteAllTextAsync(setupTokenPath, setupToken);

        const string administratorPassword = "administrator endpoint password";
        var baseAddress = new Uri($"http://127.0.0.1:{ReserveTcpPort()}");
        var trustedOrigin = baseAddress.GetLeftPart(UriPartial.Authority);
        var output = new ConcurrentQueue<string>();
        using var process = StartServer(repositoryRoot, temporaryDirectory, setupTokenPath, baseAddress, output);
        try
        {
            using var client = new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(10) };
            await WaitForServerAsync(client, process, output);

            using (var status = await client.GetAsync("/api/v1/auth/status"))
            {
                Assert.Equal(HttpStatusCode.OK, status.StatusCode);
                var payload = await status.Content.ReadFromJsonAsync<JsonElement>();
                Assert.False(payload.GetProperty("bootstrapped").GetBoolean());
                Assert.False(payload.GetProperty("authenticated").GetBoolean());
            }
            using (var devices = await client.GetAsync("/api/v1/auth/devices"))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, devices.StatusCode);
            }

            using (var crossOriginBootstrap = JsonRequest(
                HttpMethod.Post,
                "/api/v1/auth/bootstrap",
                "https://other-device.example.invalid",
                new { password = administratorPassword, setupToken, deviceName = "Blocked browser" }))
            using (var blocked = await client.SendAsync(crossOriginBootstrap))
            {
                Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
                var payload = await blocked.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("untrusted_request_origin", payload.GetProperty("error").GetString());
            }

            using var bootstrapRequest = JsonRequest(
                HttpMethod.Post,
                "/api/v1/auth/bootstrap",
                trustedOrigin,
                new { password = administratorPassword, setupToken, deviceName = "Initial browser" });
            using var bootstrap = await client.SendAsync(bootstrapRequest);
            Assert.Equal(HttpStatusCode.Created, bootstrap.StatusCode);
            var initialSetCookie = bootstrap.Headers.GetValues("Set-Cookie").Single();
            AssertHardenedSessionCookie(initialSetCookie);
            Assert.DoesNotContain(administratorPassword, await bootstrap.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.DoesNotContain(setupToken, await bootstrap.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            var initialCookie = initialSetCookie.Split(';', 2)[0];

            using (var authenticatedStatus = await SendAuthenticatedAsync(
                client, HttpMethod.Get, "/api/v1/auth/status", initialCookie))
            {
                var payload = await authenticatedStatus.Content.ReadFromJsonAsync<JsonElement>();
                Assert.True(payload.GetProperty("bootstrapped").GetBoolean());
                Assert.True(payload.GetProperty("authenticated").GetBoolean());
                Assert.Equal("Initial browser", payload.GetProperty("device").GetProperty("name").GetString());
            }

            using (var blockedPairingRequest = AuthenticatedRequest(
                HttpMethod.Post,
                "/api/v1/auth/pairing",
                initialCookie))
            {
                blockedPairingRequest.Headers.TryAddWithoutValidation("Origin", "https://other-device.example.invalid");
                using var blockedPairing = await client.SendAsync(blockedPairingRequest);
                Assert.Equal(HttpStatusCode.Forbidden, blockedPairing.StatusCode);
            }

            string pairingCode;
            using (var pairingRequest = AuthenticatedRequest(
                HttpMethod.Post,
                "/api/v1/auth/pairing",
                initialCookie))
            {
                pairingRequest.Headers.TryAddWithoutValidation("Origin", trustedOrigin);
                using var pairing = await client.SendAsync(pairingRequest);
                Assert.Equal(HttpStatusCode.OK, pairing.StatusCode);
                var payload = await pairing.Content.ReadFromJsonAsync<JsonElement>();
                pairingCode = Assert.IsType<string>(payload.GetProperty("code").GetString());
                var pairingUrl = Assert.IsType<string>(payload.GetProperty("url").GetString());
                Assert.StartsWith("/#/?code=", pairingUrl, StringComparison.Ordinal);
                Assert.DoesNotContain(pairingCode, pairingUrl.Split('#', 2)[0], StringComparison.Ordinal);
            }

            string phoneCookie;
            using (var redeemRequest = JsonRequest(
                HttpMethod.Post,
                "/api/v1/auth/pairing/redeem",
                trustedOrigin,
                new { code = pairingCode, deviceName = "Phone" }))
            using (var redeem = await client.SendAsync(redeemRequest))
            {
                Assert.Equal(HttpStatusCode.OK, redeem.StatusCode);
                var setCookie = redeem.Headers.GetValues("Set-Cookie").Single();
                AssertHardenedSessionCookie(setCookie);
                phoneCookie = setCookie.Split(';', 2)[0];
            }
            using (var reusedRequest = JsonRequest(
                HttpMethod.Post,
                "/api/v1/auth/pairing/redeem",
                trustedOrigin,
                new { code = pairingCode, deviceName = "Reused phone" }))
            using (var reused = await client.SendAsync(reusedRequest))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, reused.StatusCode);
                var payload = await reused.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("invalid_pairing_code", payload.GetProperty("error").GetString());
            }

            string phoneSessionId;
            using (var devices = await SendAuthenticatedAsync(
                client, HttpMethod.Get, "/api/v1/auth/devices", initialCookie))
            {
                Assert.Equal(HttpStatusCode.OK, devices.StatusCode);
                var payload = await devices.Content.ReadFromJsonAsync<JsonElement>();
                var sessions = payload.GetProperty("items").EnumerateArray().ToArray();
                Assert.Contains(sessions, item => item.GetProperty("deviceName").GetString() == "Initial browser");
                var phone = Assert.Single(
                    sessions,
                    item => item.GetProperty("deviceName").GetString() == "Phone");
                phoneSessionId = Assert.IsType<string>(phone.GetProperty("id").GetString());
            }

            using (var revokeRequest = AuthenticatedRequest(
                HttpMethod.Delete,
                "/api/v1/auth/devices/" + Uri.EscapeDataString(phoneSessionId),
                initialCookie))
            {
                revokeRequest.Headers.TryAddWithoutValidation("Origin", trustedOrigin);
                using var revoked = await client.SendAsync(revokeRequest);
                Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
            }
            using (var phoneStatus = await SendAuthenticatedAsync(
                client, HttpMethod.Get, "/api/v1/auth/status", phoneCookie))
            {
                var payload = await phoneStatus.Content.ReadFromJsonAsync<JsonElement>();
                Assert.False(payload.GetProperty("authenticated").GetBoolean());
            }

            using (var invalidLoginRequest = JsonRequest(
                HttpMethod.Post,
                "/api/v1/auth/login",
                trustedOrigin,
                new { password = "wrong administrator password", deviceName = "Wrong browser" }))
            using (var invalidLogin = await client.SendAsync(invalidLoginRequest))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, invalidLogin.StatusCode);
                var payload = await invalidLogin.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("invalid_admin_credentials", payload.GetProperty("error").GetString());
            }

            string loginCookie;
            using (var loginRequest = JsonRequest(
                HttpMethod.Post,
                "/api/v1/auth/login",
                trustedOrigin,
                new { password = administratorPassword, deviceName = "Password browser" }))
            using (var login = await client.SendAsync(loginRequest))
            {
                Assert.Equal(HttpStatusCode.OK, login.StatusCode);
                var setCookie = login.Headers.GetValues("Set-Cookie").Single();
                AssertHardenedSessionCookie(setCookie);
                loginCookie = setCookie.Split(';', 2)[0];
            }

            using (var invalidRecoveryRequest = JsonRequest(
                HttpMethod.Post,
                "/api/v1/auth/recovery/redeem",
                trustedOrigin,
                new { code = "invalid", password = "replacement administrator password", deviceName = "Recovery" }))
            using (var invalidRecovery = await client.SendAsync(invalidRecoveryRequest))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, invalidRecovery.StatusCode);
                var payload = await invalidRecovery.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("invalid_recovery_code", payload.GetProperty("error").GetString());
            }

            using (var crossOriginLogout = AuthenticatedRequest(
                HttpMethod.Post,
                "/api/v1/auth/logout",
                loginCookie))
            {
                crossOriginLogout.Headers.TryAddWithoutValidation("Origin", "https://other-device.example.invalid");
                using var blockedLogout = await client.SendAsync(crossOriginLogout);
                Assert.Equal(HttpStatusCode.Forbidden, blockedLogout.StatusCode);
            }
            using (var stillAuthenticated = await SendAuthenticatedAsync(
                client, HttpMethod.Get, "/api/v1/auth/devices", loginCookie))
            {
                Assert.Equal(HttpStatusCode.OK, stillAuthenticated.StatusCode);
            }

            using (var logoutRequest = AuthenticatedRequest(
                HttpMethod.Post,
                "/api/v1/auth/logout",
                loginCookie))
            {
                logoutRequest.Headers.TryAddWithoutValidation("Origin", trustedOrigin);
                using var logout = await client.SendAsync(logoutRequest);
                Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
                AssertHardenedSessionCookie(logout.Headers.GetValues("Set-Cookie").Single());
            }
            using (var loggedOut = await SendAuthenticatedAsync(
                client, HttpMethod.Get, "/api/v1/auth/devices", loginCookie))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, loggedOut.StatusCode);
            }

            var loginStatuses = new List<HttpStatusCode>();
            for (var attempt = 0; attempt < 10; attempt++)
            {
                using var request = JsonRequest(
                    HttpMethod.Post,
                    "/api/v1/auth/login",
                    trustedOrigin,
                    new { password = "wrong administrator password", deviceName = "Rate limited" });
                using var response = await client.SendAsync(request);
                loginStatuses.Add(response.StatusCode);
            }
            Assert.Contains(HttpStatusCode.TooManyRequests, loginStatuses);

            var combinedOutput = string.Join(Environment.NewLine, output);
            Assert.DoesNotContain(setupToken, combinedOutput, StringComparison.Ordinal);
            Assert.DoesNotContain(administratorPassword, combinedOutput, StringComparison.Ordinal);
            Assert.DoesNotContain(pairingCode, combinedOutput, StringComparison.Ordinal);
            Assert.DoesNotContain(initialCookie.Split('=', 2)[1], combinedOutput, StringComparison.Ordinal);
            Assert.DoesNotContain(loginCookie.Split('=', 2)[1], combinedOutput, StringComparison.Ordinal);
        }
        finally
        {
            await StopServerAsync(process);
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static HttpRequestMessage JsonRequest(HttpMethod method, string path, string origin, object payload)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.TryAddWithoutValidation("Origin", origin);
        return request;
    }

    private static void AssertHardenedSessionCookie(string setCookie)
    {
        Assert.StartsWith("vrcx_session=", setCookie, StringComparison.Ordinal);
        Assert.Contains("path=/", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", setCookie, StringComparison.OrdinalIgnoreCase);
    }
}
