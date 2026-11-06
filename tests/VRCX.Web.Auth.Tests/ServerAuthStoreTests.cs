using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using VRCX.Web.Auth;
using Xunit;

namespace VRCX.Web.Auth.Tests;

public sealed class ServerAuthStoreTests
{
    [Fact]
    public void PasswordCredentialsAreSaltedCompatibleAndRejectMalformedDatabaseValues()
    {
        var first = PasswordHasher.Hash("correct horse battery staple");
        var second = PasswordHasher.Hash("correct horse battery staple");

        Assert.NotEqual(first.Salt, second.Salt);
        Assert.NotEqual(first.Hash, second.Hash);
        Assert.True(PasswordHasher.Verify("correct horse battery staple", first));
        Assert.False(PasswordHasher.Verify("wrong password", first));
        Assert.False(PasswordHasher.Verify("correct horse battery staple", first with { Salt = "not-base64" }));
        Assert.False(PasswordHasher.Verify("correct horse battery staple", first with { Hash = Convert.ToBase64String(new byte[31]) }));
        Assert.False(PasswordHasher.Verify("correct horse battery staple", first with { Iterations = int.MaxValue }));
        Assert.False(PasswordHasher.Verify(new string('x', 257), first));
        Assert.Throws<ArgumentOutOfRangeException>(() => PasswordHasher.Hash(new string('x', 257)));
        var whitespace = PasswordHasher.Hash(new string(' ', 12));
        Assert.True(PasswordHasher.Verify(new string(' ', 12), whitespace));
    }

    [Fact]
    public async Task SessionsStoreOnlyTokenHashesAndCanBeRevoked()
    {
        await using var fixture = await StoreFixture.CreateAsync();

        var issued = await fixture.Store.CreateSessionAsync("  Browser on phone  ");
        var storedHash = await fixture.ScalarStringAsync("SELECT token_hash FROM auth_sessions WHERE id = @id;", ("@id", issued.Session.Id));

        Assert.Equal("Browser on phone", issued.Session.DeviceName);
        Assert.Equal(TokenGenerator.Hash(issued.Token), storedHash);
        Assert.DoesNotContain(issued.Token, storedHash, StringComparison.Ordinal);
        Assert.NotNull(await fixture.Store.FindActiveSessionAsync(storedHash));
        Assert.True(await fixture.Store.RevokeSessionAsync(issued.Session.Id));
        Assert.False(await fixture.Store.RevokeSessionAsync(issued.Session.Id));
        Assert.Null(await fixture.Store.FindActiveSessionAsync(storedHash));
    }

    [Fact]
    public async Task PasswordAuthenticationChecksCurrentCredentialAndIssuesAHashedSession()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        Assert.True(await fixture.Store.CreateAdminAsync(PasswordHasher.Hash("current administrator password")));

        Assert.Null(await fixture.Store.AuthenticateAdminAsync("wrong administrator password", "Wrong browser"));
        var issued = await fixture.Store.AuthenticateAdminAsync("current administrator password", "Authenticated browser");

        Assert.NotNull(issued);
        Assert.Equal("Authenticated browser", issued!.Session.DeviceName);
        Assert.NotNull(await fixture.Store.FindActiveSessionAsync(TokenGenerator.Hash(issued.Token)));
        Assert.Equal(1L, await fixture.ScalarInt64Async("SELECT COUNT(*) FROM auth_sessions;"));
    }

    [Fact]
    public async Task BootstrapCreatesAdministratorAndInitialSessionInOneOperationOnlyOnce()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var credential = PasswordHasher.Hash("initial administrator password");

        var initial = await fixture.Store.BootstrapAdminAsync(credential, "Initial browser");
        var repeated = await fixture.Store.BootstrapAdminAsync(
            PasswordHasher.Hash("replacement administrator password"),
            "Unexpected browser");

        Assert.NotNull(initial);
        Assert.Equal("Initial browser", initial!.Session.DeviceName);
        Assert.NotNull(await fixture.Store.FindActiveSessionAsync(TokenGenerator.Hash(initial.Token)));
        Assert.Null(repeated);
        Assert.Equal(1L, await fixture.ScalarInt64Async("SELECT COUNT(*) FROM admin_credentials;"));
        Assert.Equal(1L, await fixture.ScalarInt64Async("SELECT COUNT(*) FROM auth_sessions;"));
        var stored = Assert.IsType<PasswordCredential>(await fixture.Store.GetAdminCredentialAsync());
        Assert.True(PasswordHasher.Verify("initial administrator password", stored));
        Assert.False(PasswordHasher.Verify("replacement administrator password", stored));
    }

    [Fact]
    public async Task PairingCodesAreHashedSingleUseAndCreateDeviceSessions()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var pairing = await fixture.Store.CreatePairingCodeAsync();

        var storedHash = await fixture.ScalarStringAsync("SELECT code_hash FROM pairing_codes WHERE id = @id;", ("@id", pairing.Id));
        var first = await fixture.Store.RedeemPairingCodeAsync(pairing.Code, "Phone");
        var reused = await fixture.Store.RedeemPairingCodeAsync(pairing.Code, "Other phone");

        Assert.Equal(TokenGenerator.Hash(pairing.Code), storedHash);
        Assert.DoesNotContain(pairing.Code, storedHash, StringComparison.Ordinal);
        Assert.NotNull(first);
        Assert.Equal("Phone", first!.Session.DeviceName);
        Assert.Null(reused);
        Assert.NotNull(await fixture.Store.FindActiveSessionAsync(TokenGenerator.Hash(first.Token)));
    }

    [Fact]
    public async Task RecoveryIsSingleUseReplacesPasswordAndRevokesExistingSessions()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        Assert.True(await fixture.Store.CreateAdminAsync(PasswordHasher.Hash("old administrator password")));
        var oldSession = await fixture.Store.CreateSessionAsync("Old browser");
        var outstandingPairing = await fixture.Store.CreatePairingCodeAsync();
        var recovery = await fixture.Store.CreateAdminRecoveryCodeAsync();

        var replacement = await fixture.Store.ResetAdminPasswordWithRecoveryCodeAsync(
            recovery.Code,
            PasswordHasher.Hash("new administrator password"),
            "Recovered browser");
        var reused = await fixture.Store.ResetAdminPasswordWithRecoveryCodeAsync(
            recovery.Code,
            PasswordHasher.Hash("another administrator password"),
            "Other browser");

        Assert.NotNull(replacement);
        Assert.Null(reused);
        var credential = Assert.IsType<PasswordCredential>(await fixture.Store.GetAdminCredentialAsync());
        Assert.True(PasswordHasher.Verify("new administrator password", credential));
        Assert.False(PasswordHasher.Verify("old administrator password", credential));
        Assert.Null(await fixture.Store.FindActiveSessionAsync(TokenGenerator.Hash(oldSession.Token)));
        Assert.Null(await fixture.Store.RedeemPairingCodeAsync(outstandingPairing.Code, "Stale pairing"));
        Assert.NotNull(await fixture.Store.FindActiveSessionAsync(TokenGenerator.Hash(replacement!.Token)));
        var storedHash = await fixture.ScalarStringAsync("SELECT code_hash FROM admin_recovery_codes WHERE id = 1;");
        Assert.Equal(TokenGenerator.Hash(recovery.Code), storedHash);
        Assert.DoesNotContain(recovery.Code, storedHash, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpensExistingPrototypeSchemaWithoutReplacingAdminOrDroppingOtherTables()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vrcx-web-auth-compat", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var databasePath = Path.Combine(directory, "vrcx-server.db");
            var salt = Enumerable.Range(1, 16).Select(value => (byte)value).ToArray();
            var hash = Rfc2898DeriveBytes.Pbkdf2(
                "existing administrator password",
                salt,
                PasswordCredential.CurrentIterations,
                HashAlgorithmName.SHA512,
                32);
            var sessionToken = "existing-session-token";
            var sessionHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionToken)));
            await CreatePrototypeDatabaseAsync(databasePath, Convert.ToBase64String(salt), Convert.ToBase64String(hash), sessionHash);
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(hash);

            var store = new ServerAuthStore(new ServerAuthOptions { DataDirectory = directory });
            await store.InitializeAsync();

            Assert.True(await store.IsBootstrappedAsync());
            Assert.False(await store.CreateAdminAsync(PasswordHasher.Hash("replacement must not win")));
            var credential = Assert.IsType<PasswordCredential>(await store.GetAdminCredentialAsync());
            Assert.True(PasswordHasher.Verify("existing administrator password", credential));
            Assert.NotNull(await store.AuthenticateAdminAsync("existing administrator password", "Compatible browser"));
            Assert.NotNull(await store.FindActiveSessionAsync(sessionHash));

            await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
            await connection.OpenAsync();
            await using var marker = connection.CreateCommand();
            marker.CommandText = "SELECT value FROM existing_domain_marker WHERE id = 1;";
            Assert.Equal("preserved", (string?)await marker.ExecuteScalarAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SetupTokenProviderAcceptsOnlyTheExactHostSecret()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vrcx-web-auth-token", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "setup-token");
            var token = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
            File.WriteAllText(path, Convert.ToBase64String(token));
            var provider = new FileSetupTokenProvider(new ServerAuthOptions { SetupTokenFile = path });

            Assert.True(provider.Verify(Convert.ToBase64String(token)));
            Assert.False(provider.Verify(Convert.ToBase64String(new byte[32])));
            Assert.False(provider.Verify("not-base64"));
            Assert.False(provider.Verify(null));
            CryptographicOperations.ZeroMemory(token);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void HostSecretsMustBeDistinctAndBounded()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vrcx-web-host-secrets", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var setupPath = Path.Combine(directory, "setup-token");
            var encryptionPath = Path.Combine(directory, "encryption-key");
            var setup = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
            var encryption = Enumerable.Range(101, 32).Select(value => (byte)value).ToArray();
            File.WriteAllText(setupPath, Convert.ToBase64String(setup));
            File.WriteAllText(encryptionPath, Convert.ToBase64String(encryption));

            HostSecretValidator.EnsureDistinct(setupPath, encryptionPath);
            File.WriteAllText(encryptionPath, Convert.ToBase64String(setup));
            Assert.Throws<InvalidOperationException>(() => HostSecretValidator.EnsureDistinct(setupPath, encryptionPath));
            File.WriteAllBytes(encryptionPath, new byte[1025]);
            Assert.Throws<InvalidOperationException>(() => HostSecretValidator.EnsureDistinct(setupPath, encryptionPath));

            CryptographicOperations.ZeroMemory(setup);
            CryptographicOperations.ZeroMemory(encryption);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task CreatePrototypeDatabaseAsync(
        string path,
        string salt,
        string passwordHash,
        string sessionHash)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE admin_credentials (
                id INTEGER PRIMARY KEY CHECK(id = 1),
                salt TEXT NOT NULL,
                password_hash TEXT NOT NULL,
                iterations INTEGER NOT NULL,
                created_at TEXT NOT NULL
            );
            CREATE TABLE auth_sessions (
                id TEXT PRIMARY KEY,
                token_hash TEXT NOT NULL UNIQUE,
                device_name TEXT NOT NULL,
                issued_at TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                revoked_at TEXT NULL,
                last_seen_at TEXT NULL
            );
            CREATE TABLE pairing_codes (
                id TEXT PRIMARY KEY,
                code_hash TEXT NOT NULL UNIQUE,
                expires_at TEXT NOT NULL,
                claimed_at TEXT NULL
            );
            CREATE TABLE admin_recovery_codes (
                id INTEGER PRIMARY KEY CHECK(id = 1),
                code_hash TEXT NOT NULL,
                created_at TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                claimed_at TEXT NULL
            );
            CREATE TABLE existing_domain_marker (id INTEGER PRIMARY KEY, value TEXT NOT NULL);
            INSERT INTO admin_credentials (id, salt, password_hash, iterations, created_at)
            VALUES (1, @salt, @passwordHash, @iterations, @createdAt);
            INSERT INTO auth_sessions (id, token_hash, device_name, issued_at, expires_at, revoked_at, last_seen_at)
            VALUES ('existing', @sessionHash, 'Existing browser', @issuedAt, @expiresAt, NULL, @issuedAt);
            INSERT INTO existing_domain_marker (id, value) VALUES (1, 'preserved');
            """;
        var now = DateTimeOffset.UtcNow;
        command.Parameters.AddWithValue("@salt", salt);
        command.Parameters.AddWithValue("@passwordHash", passwordHash);
        command.Parameters.AddWithValue("@iterations", PasswordCredential.CurrentIterations);
        command.Parameters.AddWithValue("@createdAt", ToStorage(now.AddDays(-1)));
        command.Parameters.AddWithValue("@sessionHash", sessionHash);
        command.Parameters.AddWithValue("@issuedAt", ToStorage(now.AddMinutes(-1)));
        command.Parameters.AddWithValue("@expiresAt", ToStorage(now.AddHours(1)));
        await command.ExecuteNonQueryAsync();
    }

    private static string ToStorage(DateTimeOffset value) =>
        value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private sealed class StoreFixture : IAsyncDisposable
    {
        private StoreFixture(string directory, ServerAuthStore store)
        {
            DirectoryPath = directory;
            Store = store;
        }

        public string DirectoryPath { get; }

        public ServerAuthStore Store { get; }

        public static async Task<StoreFixture> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), "vrcx-web-auth-tests", Guid.NewGuid().ToString("N"));
            var options = new ServerAuthOptions
            {
                DataDirectory = directory,
                SessionLifetime = TimeSpan.FromHours(1),
                PairingLifetime = TimeSpan.FromMinutes(5),
                RecoveryCodeLifetime = TimeSpan.FromMinutes(5)
            };
            var store = new ServerAuthStore(options);
            await store.InitializeAsync();
            return new StoreFixture(directory, store);
        }

        public async Task<string> ScalarStringAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using var connection = new SqliteConnection($"Data Source={Store.DatabasePath};Mode=ReadOnly");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            return Assert.IsType<string>(await command.ExecuteScalarAsync());
        }

        public async Task<long> ScalarInt64Async(string sql)
        {
            await using var connection = new SqliteConnection($"Data Source={Store.DatabasePath};Mode=ReadOnly");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Assert.IsType<long>(await command.ExecuteScalarAsync());
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(DirectoryPath, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
