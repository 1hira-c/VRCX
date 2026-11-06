using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace VRCX.Web.Server.Tests;

public sealed partial class ReadApiEndpointTests
{
    [Fact]
    public async Task StartupRejectsMatchingSetupAndEncryptionSecrets()
    {
        var repositoryRoot = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "vrcx-web-server-secrets", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var setupTokenPath = Path.Combine(temporaryDirectory, "setup-token");
        var setupTokenBytes = RandomNumberGenerator.GetBytes(32);
        try
        {
            await File.WriteAllTextAsync(setupTokenPath, Convert.ToBase64String(setupTokenBytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(setupTokenBytes);
        }

        var output = new ConcurrentQueue<string>();
        using var process = StartServer(
            repositoryRoot,
            temporaryDirectory,
            setupTokenPath,
            new Uri($"http://127.0.0.1:{ReserveTcpPort()}"),
            output,
            useSetupTokenAsEncryptionKey: true);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(timeout.Token);

            Assert.NotEqual(0, process.ExitCode);
            Assert.Contains(output, line => line.Contains("host secret validation failed", StringComparison.Ordinal));
            Assert.DoesNotContain(output, line => line.Contains(setupTokenPath, StringComparison.Ordinal));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task FixedReadEndpointsRequireSessionAndReturnOnlyNamedResources()
    {
        var repositoryRoot = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "vrcx-web-server-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var setupTokenBytes = RandomNumberGenerator.GetBytes(32);
        var setupToken = Convert.ToBase64String(setupTokenBytes);
        CryptographicOperations.ZeroMemory(setupTokenBytes);
        var setupTokenPath = Path.Combine(temporaryDirectory, "setup-token");
        await File.WriteAllTextAsync(setupTokenPath, setupToken);

        var port = ReserveTcpPort();
        var baseAddress = new Uri($"http://127.0.0.1:{port}");
        var output = new ConcurrentQueue<string>();
        using var process = StartServer(repositoryRoot, temporaryDirectory, setupTokenPath, baseAddress, output);
        try
        {
            using var client = new HttpClient
            {
                BaseAddress = baseAddress,
                Timeout = TimeSpan.FromSeconds(5)
            };
            await WaitForServerAsync(client, process, output);

            using (var unauthorized = await client.GetAsync("/api/v1/status"))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
            }
            using (var unauthorizedEvents = await client.GetAsync("/api/v1/events"))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedEvents.StatusCode);
            }
            using (var unauthorizedWorkerStatus = await client.GetAsync("/api/v1/vrchat/auth/status"))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedWorkerStatus.StatusCode);
            }
            using (var unauthorizedWorkerLogin = await client.PostAsJsonAsync("/api/v1/vrchat/auth/login", new
            {
                username = "test-user",
                password = "test-password"
            }))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedWorkerLogin.StatusCode);
            }
            using (var authStatus = await client.GetAsync("/api/v1/auth/status"))
            {
                Assert.Equal(HttpStatusCode.OK, authStatus.StatusCode);
            }

            using var bootstrap = await client.PostAsJsonAsync("/api/v1/auth/bootstrap", new
            {
                password = "administrator password",
                setupToken,
                deviceName = "Endpoint test"
            });
            Assert.Equal(HttpStatusCode.Created, bootstrap.StatusCode);
            var sessionCookie = bootstrap.Headers.GetValues("Set-Cookie").Single().Split(';', 2)[0];

            using (var unavailableStatus = await SendAuthenticatedAsync(
                client, HttpMethod.Get, "/api/v1/vrchat/auth/status", sessionCookie))
            {
                await AssertWorkerUnavailableAsync(unavailableStatus);
            }
            using (var unavailableLogin = await SendAuthenticatedJsonAsync(
                client, "/api/v1/vrchat/auth/login", sessionCookie, new
                {
                    username = "test-user",
                    password = "test-password"
                }))
            {
                await AssertWorkerUnavailableAsync(unavailableLogin);
            }

            var invalidWorkerRequests = new (string Path, object Payload)[]
            {
                ("/api/v1/vrchat/auth/login", new { username = new string('u', 257), password = "password" }),
                ("/api/v1/vrchat/auth/login", new { username = "user", password = "" }),
                ("/api/v1/vrchat/auth/login", new { username = "user", password = new string('p', 1025) }),
                ("/api/v1/vrchat/auth/login", new
                {
                    username = "user",
                    password = "password",
                    endpoint = "http://example.invalid",
                    headers = new { Authorization = "arbitrary" }
                }),
                ("/api/v1/vrchat/auth/two-factor", new { kind = "sms", code = "123456" }),
                ("/api/v1/vrchat/auth/two-factor", new { kind = 0, code = "123456" }),
                ("/api/v1/vrchat/auth/two-factor", new { code = "123456" }),
                ("/api/v1/vrchat/auth/two-factor", new { kind = (string?)null, code = "123456" }),
                ("/api/v1/vrchat/auth/two-factor", new { kind = "totp", code = new string('1', 129) }),
                ("/api/v1/vrchat/auth/two-factor", new
                {
                    kind = "totp",
                    code = "123456",
                    url = "http://example.invalid"
                })
            };
            foreach (var (path, payload) in invalidWorkerRequests)
            {
                using var response = await SendAuthenticatedJsonAsync(client, path, sessionCookie, payload);
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            }

            using (var unavailableTwoFactor = await SendAuthenticatedJsonAsync(
                client,
                "/api/v1/vrchat/auth/two-factor",
                sessionCookie,
                new { kind = "emailOtp", code = "123456" }))
            {
                await AssertWorkerUnavailableAsync(unavailableTwoFactor);
            }
            using (var unavailableResend = await SendAuthenticatedAsync(
                client, HttpMethod.Post, "/api/v1/vrchat/auth/email-otp/resend", sessionCookie))
            {
                await AssertWorkerUnavailableAsync(unavailableResend);
            }
            using (var unavailableLogout = await SendAuthenticatedAsync(
                client, HttpMethod.Post, "/api/v1/vrchat/auth/logout", sessionCookie))
            {
                await AssertWorkerUnavailableAsync(unavailableLogout);
            }
            using (var arbitraryWorkerPath = await SendAuthenticatedAsync(
                client, HttpMethod.Get, "/api/v1/vrchat/auth/request", sessionCookie))
            {
                Assert.Equal(HttpStatusCode.NotFound, arbitraryWorkerPath.StatusCode);
            }

            using (var friends = await SendAuthenticatedAsync(
                client,
                HttpMethod.Get,
                "/api/v1/friends?offset=-1&limit=999",
                sessionCookie))
            {
                Assert.Equal(HttpStatusCode.OK, friends.StatusCode);
                using var payload = JsonDocument.Parse(await friends.Content.ReadAsStringAsync());
                Assert.Equal(0, payload.RootElement.GetProperty("offset").GetInt32());
                Assert.Equal(200, payload.RootElement.GetProperty("limit").GetInt32());
            }

            using (var boundedOffset = await SendAuthenticatedAsync(
                client,
                HttpMethod.Get,
                "/api/v1/friends?offset=2147483647&limit=1",
                sessionCookie))
            {
                Assert.Equal(HttpStatusCode.OK, boundedOffset.StatusCode);
                using var payload = JsonDocument.Parse(await boundedOffset.Content.ReadAsStringAsync());
                Assert.Equal(10_000, payload.RootElement.GetProperty("offset").GetInt32());
            }

            var successfulReads = new[]
            {
                "/api/v1/status",
                "/api/v1/history/friends",
                "/api/v1/feed",
                "/api/v1/history/game-log",
                "/api/v1/notifications",
                "/api/v1/favorites",
                "/api/v1/favorites/groups",
                "/api/v1/analytics/summary",
                "/api/v1/search?type=user&q=test"
            };
            foreach (var path in successfulReads)
            {
                using var response = await SendAuthenticatedAsync(client, HttpMethod.Get, path, sessionCookie);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            var missingDetails = new[]
            {
                "/api/v1/friends/usr_missing",
                "/api/v1/friends/usr_missing/history?limit=1",
                "/api/v1/memos/user/usr_missing",
                "/api/v1/users/usr_missing",
                "/api/v1/worlds/wrld_missing",
                "/api/v1/avatars/avtr_missing",
                "/api/v1/groups/grp_missing"
            };
            foreach (var path in missingDetails)
            {
                using var response = await SendAuthenticatedAsync(client, HttpMethod.Get, path, sessionCookie);
                var expected = path.Contains("/history", StringComparison.Ordinal)
                    ? HttpStatusCode.OK
                    : HttpStatusCode.NotFound;
                Assert.Equal(expected, response.StatusCode);
            }

            using (var invalidSearch = await SendAuthenticatedAsync(
                client,
                HttpMethod.Get,
                "/api/v1/search?type=everything&q=test",
                sessionCookie))
            {
                Assert.Equal(HttpStatusCode.BadRequest, invalidSearch.StatusCode);
            }
            using (var genericSql = await SendAuthenticatedAsync(
                client,
                HttpMethod.Get,
                "/api/v1/sql?query=select%20*%20from%20auth_sessions",
                sessionCookie))
            {
                Assert.Equal(HttpStatusCode.NotFound, genericSql.StatusCode);
            }

            using var eventRequest = AuthenticatedRequest(HttpMethod.Get, "/api/v1/events", sessionCookie);
            using var eventResponse = await client.SendAsync(eventRequest, HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal(HttpStatusCode.OK, eventResponse.StatusCode);
            Assert.Equal("text/event-stream", eventResponse.Content.Headers.ContentType?.MediaType);
            using var eventReader = new StreamReader(await eventResponse.Content.ReadAsStreamAsync());
            using var eventTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var eventNames = new HashSet<string>(StringComparer.Ordinal);
            while (eventNames.Count < 2)
            {
                var line = await eventReader.ReadLineAsync(eventTimeout.Token);
                Assert.NotNull(line);
                if (line.StartsWith("event: ", StringComparison.Ordinal))
                {
                    eventNames.Add(line[7..]);
                }
            }
            Assert.Contains("status", eventNames);
            Assert.Contains("pipeline", eventNames);
        }
        finally
        {
            await StopServerAsync(process);
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static Process StartServer(
        string repositoryRoot,
        string dataDirectory,
        string setupTokenPath,
        Uri baseAddress,
        ConcurrentQueue<string> output,
        Uri? workerBaseAddress = null,
        bool useSetupTokenAsEncryptionKey = false,
        string? readModelDatabasePath = null)
    {
        var testOutputRoot = Path.Combine(repositoryRoot, "tests", "VRCX.Web.Server.Tests", "bin");
        var relativeOutputPath = Path.GetRelativePath(testOutputRoot, AppContext.BaseDirectory);
        if (relativeOutputPath.StartsWith("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The test output is outside its expected bin directory.");
        }
        var executableName = OperatingSystem.IsWindows() ? "VRCX.Web.Server.exe" : "VRCX.Web.Server";
        var serverOutputPath = Path.Combine(
            repositoryRoot,
            "Dotnet",
            "VRCX.Web.Server",
            "bin",
            "VRCX.Web.Server",
            relativeOutputPath);
        var executablePath = Path.Combine(serverOutputPath, executableName);
        if (!File.Exists(executablePath))
        {
            executablePath = Path.Combine(serverOutputPath, RuntimeInformation.RuntimeIdentifier, executableName);
        }
        Assert.True(File.Exists(executablePath), $"Server executable was not built: {executablePath}");

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.Combine(repositoryRoot, "Dotnet", "VRCX.Web.Server"),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--urls");
        startInfo.ArgumentList.Add(baseAddress.ToString().TrimEnd('/'));
        startInfo.Environment["VrcxWeb__Auth__DataDirectory"] = dataDirectory;
        startInfo.Environment["VrcxWeb__Auth__SetupTokenFile"] = setupTokenPath;
        if (useSetupTokenAsEncryptionKey)
        {
            startInfo.Environment["VRCX_WEB_SECRET_KEY_FILE"] = setupTokenPath;
        }
        else
        {
            var encryptionKeyPath = Path.Combine(dataDirectory, "encryption-key");
            var encryptionKey = RandomNumberGenerator.GetBytes(32);
            try
            {
                File.WriteAllText(encryptionKeyPath, Convert.ToBase64String(encryptionKey));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encryptionKey);
            }
            startInfo.Environment["VRCX_WEB_SECRET_KEY_FILE"] = encryptionKeyPath;
        }
        startInfo.Environment["VrcxWeb__ReadModel__DatabasePath"] =
            readModelDatabasePath ?? Path.Combine(dataDirectory, "missing-vrcx.sqlite3");
        startInfo.Environment["VrcxWeb__SnapshotImport__StagingDirectory"] =
            Path.Combine(dataDirectory, "snapshot-staging");
        if (workerBaseAddress is null)
        {
            var workerPort = ReserveTcpPort();
            while (workerPort == baseAddress.Port)
            {
                workerPort = ReserveTcpPort();
            }
            workerBaseAddress = new Uri("http://127.0.0.1:" + workerPort);
        }
        startInfo.Environment["VrcxWeb__Worker__BaseAddress"] = workerBaseAddress.ToString().TrimEnd((char)47);
        startInfo.Environment["Logging__LogLevel__Default"] = "Warning";

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start VRCX.Web.Server.");
        process.OutputDataReceived += (_, eventArgs) => EnqueueOutput(output, eventArgs.Data);
        process.ErrorDataReceived += (_, eventArgs) => EnqueueOutput(output, eventArgs.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task WaitForServerAsync(
        HttpClient client,
        Process process,
        ConcurrentQueue<string> output)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException($"Server exited during startup.\n{string.Join(Environment.NewLine, output)}");
            }
            try
            {
                using var response = await client.GetAsync("/healthz");
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Kestrel has not bound the port yet.
            }
            await Task.Delay(100);
        }

        throw new TimeoutException($"Server did not become ready.\n{string.Join(Environment.NewLine, output)}");
    }

    private static HttpRequestMessage AuthenticatedRequest(HttpMethod method, string path, string sessionCookie)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("Cookie", sessionCookie);
        return request;
    }

    private static async Task AssertWorkerUnavailableAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var payload = JsonDocument.Parse(body);
        Assert.Equal("vrchat_worker_unavailable", payload.RootElement.GetProperty("error").GetString());
        Assert.DoesNotContain("127.0.0.1", body, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpRequestException", body, StringComparison.Ordinal);
    }

    private static async Task<HttpResponseMessage> SendAuthenticatedJsonAsync(
        HttpClient client,
        string path,
        string sessionCookie,
        object payload)
    {
        using var request = AuthenticatedRequest(HttpMethod.Post, path, sessionCookie);
        request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendAuthenticatedAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string sessionCookie)
    {
        using var request = AuthenticatedRequest(method, path, sessionCookie);
        return await client.SendAsync(request);
    }

    private static async Task StopServerAsync(Process process)
    {
        if (process.HasExited)
        {
            return;
        }

        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static int ReserveTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "VRCX.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the VRCX repository root.");
    }

    private static void EnqueueOutput(ConcurrentQueue<string> output, string? line)
    {
        if (line is null)
        {
            return;
        }

        output.Enqueue(line);
        while (output.Count > 100)
        {
            output.TryDequeue(out _);
        }
    }
}
