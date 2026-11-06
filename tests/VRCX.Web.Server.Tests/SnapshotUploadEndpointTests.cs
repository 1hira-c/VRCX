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
    public async Task SnapshotSetupRequiresAdminPreservesWalAndClosesAfterFirstLogicalImport()
    {
        var repositoryRoot = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "vrcx-web-snapshot-endpoint",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var setupTokenBytes = RandomNumberGenerator.GetBytes(32);
        var setupToken = Convert.ToBase64String(setupTokenBytes);
        CryptographicOperations.ZeroMemory(setupTokenBytes);
        var setupTokenPath = Path.Combine(temporaryDirectory, "setup-token");
        await File.WriteAllTextAsync(setupTokenPath, setupToken);
        var runtimeDatabasePath = Path.Combine(temporaryDirectory, "runtime", "VRCX.sqlite3");
        var snapshot = await CreateWalSnapshotAsync(temporaryDirectory);
        var differentSnapshot = await CreateClosedSnapshotAsync(temporaryDirectory);

        var baseAddress = new Uri($"http://127.0.0.1:{ReserveTcpPort()}");
        var output = new ConcurrentQueue<string>();
        using var process = StartServer(
            repositoryRoot,
            temporaryDirectory,
            setupTokenPath,
            baseAddress,
            output,
            readModelDatabasePath: runtimeDatabasePath);
        try
        {
            using var client = new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(15) };
            await WaitForServerAsync(client, process, output);

            using (var content = CreateSnapshotContent(snapshot.Database, snapshot.Wal))
            using (var unauthorized = await client.PostAsync("/api/v1/setup/import", content))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
            }

            using var bootstrap = await client.PostAsJsonAsync("/api/v1/auth/bootstrap", new
            {
                password = "administrator password",
                setupToken,
                deviceName = "Snapshot endpoint test"
            });
            Assert.Equal(HttpStatusCode.Created, bootstrap.StatusCode);
            var sessionCookie = bootstrap.Headers.GetValues("Set-Cookie").Single().Split(';', 2)[0];

            using (var imported = await SendSnapshotAsync(client, sessionCookie, snapshot.Database, snapshot.Wal))
            {
                Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
                var body = await imported.Content.ReadAsStringAsync();
                using var payload = JsonDocument.Parse(body);
                Assert.False(payload.RootElement.GetProperty("alreadyImported").GetBoolean());
                Assert.True(payload.RootElement.GetProperty("importedRowCount").GetInt64() >= 1);
                Assert.DoesNotContain("snapshotId", body, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("memos", body, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(temporaryDirectory, body, StringComparison.Ordinal);
            }

            await using (var runtime = await OpenReadOnlyAsync(runtimeDatabasePath))
            await using (var command = runtime.CreateCommand())
            {
                command.CommandText = "SELECT memo FROM memos WHERE user_id = 'usr_wal';";
                Assert.Equal("visible through uploaded WAL", Convert.ToString(await command.ExecuteScalarAsync()));
            }

            using (var duplicate = await SendSnapshotAsync(client, sessionCookie, snapshot.Database, snapshot.Wal))
            {
                Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
                var payload = await duplicate.Content.ReadFromJsonAsync<JsonElement>();
                Assert.True(payload.GetProperty("alreadyImported").GetBoolean());
                Assert.Equal(0, payload.GetProperty("importedRowCount").GetInt64());
            }

            using (var closed = await SendSnapshotAsync(client, sessionCookie, differentSnapshot, null))
            {
                Assert.Equal(HttpStatusCode.Conflict, closed.StatusCode);
                var payload = await closed.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("snapshot_setup_closed", payload.GetProperty("error").GetString());
            }

            var stagingDirectory = Path.Combine(temporaryDirectory, "snapshot-staging");
            Assert.True(Directory.Exists(stagingDirectory));
            Assert.Empty(Directory.EnumerateFiles(stagingDirectory));
        }
        finally
        {
            await StopServerAsync(process);
            SqliteConnection.ClearAllPools();
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static async Task<(byte[] Database, byte[] Wal)> CreateWalSnapshotAsync(string directory)
    {
        var path = Path.Combine(directory, "desktop.sqlite3");
        await using var connection = await OpenWritableAsync(path);
        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA wal_autocheckpoint = 0;
                CREATE TABLE memos (user_id TEXT PRIMARY KEY, edited_at TEXT, memo TEXT);
                PRAGMA wal_checkpoint(TRUNCATE);
                INSERT INTO memos VALUES ('usr_wal', '2026-11-06', 'visible through uploaded WAL');
                """;
            await setup.ExecuteNonQueryAsync();
        }

        var walPath = path + "-wal";
        Assert.True(File.Exists(walPath));
        Assert.True(new FileInfo(walPath).Length > 0);
        return (await File.ReadAllBytesAsync(path), await File.ReadAllBytesAsync(walPath));
    }

    private static async Task<byte[]> CreateClosedSnapshotAsync(string directory)
    {
        var path = Path.Combine(directory, "different.sqlite3");
        await using (var connection = await OpenWritableAsync(path))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE memos (user_id TEXT PRIMARY KEY, edited_at TEXT, memo TEXT);
                INSERT INTO memos VALUES ('usr_different', '2026-11-06', 'different snapshot');
                """;
            await command.ExecuteNonQueryAsync();
        }
        return await File.ReadAllBytesAsync(path);
    }

    private static MultipartFormDataContent CreateSnapshotContent(byte[] database, byte[]? wal)
    {
        var content = new MultipartFormDataContent("vrcx-endpoint-snapshot-boundary");
        content.Add(new ByteArrayContent(database), "database", "caller-controlled-name.sqlite3");
        if (wal is not null)
        {
            content.Add(new ByteArrayContent(wal), "wal", "unrelated-name-wal");
        }
        return content;
    }

    private static async Task<HttpResponseMessage> SendSnapshotAsync(
        HttpClient client,
        string sessionCookie,
        byte[] database,
        byte[]? wal)
    {
        using var request = AuthenticatedRequest(HttpMethod.Post, "/api/v1/setup/import", sessionCookie);
        request.Content = CreateSnapshotContent(database, wal);
        return await client.SendAsync(request);
    }

    private static async Task<SqliteConnection> OpenWritableAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<SqliteConnection> OpenReadOnlyAsync(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        return connection;
    }
}
