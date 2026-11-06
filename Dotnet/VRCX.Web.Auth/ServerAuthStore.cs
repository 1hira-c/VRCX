using System.Globalization;
using Microsoft.Data.Sqlite;

namespace VRCX.Web.Auth;

/// <summary>
/// Persistence for the Web server's own administrator and device sessions. This database does not
/// contain VRChat credentials or domain data. The table layout remains compatible with the first
/// self-hosted prototype so an already configured administrator password is preserved.
/// </summary>
public sealed class ServerAuthStore
{
    private readonly ServerAuthOptions options;
    private readonly string connectionString;

    public ServerAuthStore(ServerAuthOptions options)
    {
        this.options = options;
        DatabasePath = Path.Combine(Path.GetFullPath(options.DataDirectory), "vrcx-server.db");
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 5
        }.ToString();
    }

    public string DatabasePath { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = Schema;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> IsBootstrappedAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM admin_credentials WHERE id = 1;";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
    }

    public async Task<bool> CreateAdminAsync(PasswordCredential credential, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO admin_credentials (id, salt, password_hash, iterations, created_at)
            SELECT 1, @salt, @hash, @iterations, @createdAt
            WHERE NOT EXISTS (SELECT 1 FROM admin_credentials WHERE id = 1);
            """;
        command.Parameters.AddWithValue("@salt", credential.Salt);
        command.Parameters.AddWithValue("@hash", credential.Hash);
        command.Parameters.AddWithValue("@iterations", credential.Iterations);
        command.Parameters.AddWithValue("@createdAt", ToStorage(DateTimeOffset.UtcNow));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>
    /// Creates the first administrator and its initial browser session atomically. This prevents
    /// password recovery from revoking sessions between administrator creation and initial session
    /// issuance, only for the bootstrap request to issue a session after recovery has completed.
    /// </summary>
    public async Task<SessionToken?> BootstrapAdminAsync(
        PasswordCredential credential,
        string? deviceName,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var createAdmin = connection.CreateCommand())
        {
            createAdmin.Transaction = transaction;
            createAdmin.CommandText = """
                INSERT INTO admin_credentials (id, salt, password_hash, iterations, created_at)
                SELECT 1, @salt, @hash, @iterations, @createdAt
                WHERE NOT EXISTS (SELECT 1 FROM admin_credentials WHERE id = 1);
                """;
            createAdmin.Parameters.AddWithValue("@salt", credential.Salt);
            createAdmin.Parameters.AddWithValue("@hash", credential.Hash);
            createAdmin.Parameters.AddWithValue("@iterations", credential.Iterations);
            createAdmin.Parameters.AddWithValue("@createdAt", ToStorage(DateTimeOffset.UtcNow));
            if (await createAdmin.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                transaction.Rollback();
                return null;
            }
        }

        var rawToken = TokenGenerator.NewToken();
        var now = DateTimeOffset.UtcNow;
        var session = new SessionRecord(
            Guid.NewGuid().ToString("N"),
            NormalizeDeviceName(deviceName),
            now,
            now.Add(options.SessionLifetime),
            null,
            now);
        await InsertSessionAsync(connection, transaction, session, TokenGenerator.Hash(rawToken), cancellationToken);
        transaction.Commit();
        return new SessionToken(rawToken, session);
    }

    public async Task<PasswordCredential?> GetAdminCredentialAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT salt, password_hash, iterations FROM admin_credentials WHERE id = 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new PasswordCredential(reader.GetString(0), reader.GetString(1), reader.GetInt32(2))
            : null;
    }

    public async Task<SessionToken> CreateSessionAsync(string? deviceName, CancellationToken cancellationToken = default)
    {
        var rawToken = TokenGenerator.NewToken();
        var now = DateTimeOffset.UtcNow;
        var session = new SessionRecord(
            Guid.NewGuid().ToString("N"),
            NormalizeDeviceName(deviceName),
            now,
            now.Add(options.SessionLifetime),
            null,
            now);
        await using var connection = await OpenAsync(cancellationToken);
        await InsertSessionAsync(connection, null, session, TokenGenerator.Hash(rawToken), cancellationToken);
        return new SessionToken(rawToken, session);
    }

    /// <summary>
    /// Verifies the current administrator password and issues a session in one write transaction.
    /// Keeping both operations under the same lock prevents a password recovery from revoking
    /// sessions and then being followed by a session issued from the old password.
    /// </summary>
    public async Task<SessionToken?> AuthenticateAdminAsync(
        string password,
        string? deviceName,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        PasswordCredential? credential;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT salt, password_hash, iterations FROM admin_credentials WHERE id = 1;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            credential = await reader.ReadAsync(cancellationToken)
                ? new PasswordCredential(reader.GetString(0), reader.GetString(1), reader.GetInt32(2))
                : null;
        }

        if (credential is null || !PasswordHasher.Verify(password, credential))
        {
            transaction.Rollback();
            return null;
        }

        var rawToken = TokenGenerator.NewToken();
        var now = DateTimeOffset.UtcNow;
        var session = new SessionRecord(
            Guid.NewGuid().ToString("N"),
            NormalizeDeviceName(deviceName),
            now,
            now.Add(options.SessionLifetime),
            null,
            now);
        await InsertSessionAsync(connection, transaction, session, TokenGenerator.Hash(rawToken), cancellationToken);
        transaction.Commit();
        return new SessionToken(rawToken, session);
    }

    public async Task<SessionRecord?> FindActiveSessionAsync(string tokenHash, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, device_name, issued_at, expires_at, revoked_at, last_seen_at
            FROM auth_sessions
            WHERE token_hash = @tokenHash AND revoked_at IS NULL AND expires_at > @now;
            """;
        command.Parameters.AddWithValue("@tokenHash", tokenHash);
        command.Parameters.AddWithValue("@now", ToStorage(DateTimeOffset.UtcNow));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSession(reader) : null;
    }

    public async Task TouchSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE auth_sessions SET last_seen_at = @now
            WHERE id = @id AND revoked_at IS NULL AND (last_seen_at IS NULL OR last_seen_at < @threshold);
            """;
        var now = DateTimeOffset.UtcNow;
        command.Parameters.AddWithValue("@now", ToStorage(now));
        command.Parameters.AddWithValue("@threshold", ToStorage(now.AddMinutes(-1)));
        command.Parameters.AddWithValue("@id", sessionId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SessionRecord>> GetSessionsAsync(CancellationToken cancellationToken = default)
    {
        var sessions = new List<SessionRecord>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, device_name, issued_at, expires_at, revoked_at, last_seen_at
            FROM auth_sessions ORDER BY issued_at DESC;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            sessions.Add(ReadSession(reader));
        }

        return sessions;
    }

    public async Task<bool> RevokeSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE auth_sessions SET revoked_at = @now WHERE id = @id AND revoked_at IS NULL;";
        command.Parameters.AddWithValue("@now", ToStorage(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("@id", sessionId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<PairingCode> CreatePairingCodeAsync(CancellationToken cancellationToken = default)
    {
        var code = TokenGenerator.NewToken();
        var pairing = new PairingCode(Guid.NewGuid().ToString("N"), code, DateTimeOffset.UtcNow.Add(options.PairingLifetime));
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM pairing_codes WHERE claimed_at IS NOT NULL OR expires_at <= @now;
            INSERT INTO pairing_codes (id, code_hash, expires_at, claimed_at)
            VALUES (@id, @hash, @expiresAt, NULL);
            """;
        command.Parameters.AddWithValue("@now", ToStorage(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("@id", pairing.Id);
        command.Parameters.AddWithValue("@hash", TokenGenerator.Hash(code));
        command.Parameters.AddWithValue("@expiresAt", ToStorage(pairing.ExpiresAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return pairing;
    }

    public async Task<SessionToken?> RedeemPairingCodeAsync(string code, string? deviceName, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var rawToken = TokenGenerator.NewToken();
        var session = new SessionRecord(
            Guid.NewGuid().ToString("N"),
            NormalizeDeviceName(deviceName),
            now,
            now.Add(options.SessionLifetime),
            null,
            now);

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using var claim = connection.CreateCommand();
        claim.Transaction = transaction;
        claim.CommandText = """
            UPDATE pairing_codes SET claimed_at = @now
            WHERE code_hash = @hash AND claimed_at IS NULL AND expires_at > @now;
            """;
        claim.Parameters.AddWithValue("@now", ToStorage(now));
        claim.Parameters.AddWithValue("@hash", TokenGenerator.Hash(code));
        if (await claim.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            transaction.Rollback();
            return null;
        }

        await InsertSessionAsync(connection, transaction, session, TokenGenerator.Hash(rawToken), cancellationToken);
        transaction.Commit();
        return new SessionToken(rawToken, session);
    }

    public async Task<AdminRecoveryCode> CreateAdminRecoveryCodeAsync(CancellationToken cancellationToken = default)
    {
        if (!await IsBootstrappedAsync(cancellationToken))
        {
            throw new InvalidOperationException("An administrator password has not been configured.");
        }

        var code = TokenGenerator.NewToken();
        var now = DateTimeOffset.UtcNow;
        var recovery = new AdminRecoveryCode(code, now.Add(options.RecoveryCodeLifetime));
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO admin_recovery_codes (id, code_hash, created_at, expires_at, claimed_at)
            VALUES (1, @hash, @createdAt, @expiresAt, NULL)
            ON CONFLICT(id) DO UPDATE SET code_hash = excluded.code_hash, created_at = excluded.created_at,
                expires_at = excluded.expires_at, claimed_at = NULL;
            """;
        command.Parameters.AddWithValue("@hash", TokenGenerator.Hash(code));
        command.Parameters.AddWithValue("@createdAt", ToStorage(now));
        command.Parameters.AddWithValue("@expiresAt", ToStorage(recovery.ExpiresAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return recovery;
    }

    public async Task<SessionToken?> ResetAdminPasswordWithRecoveryCodeAsync(
        string code,
        PasswordCredential credential,
        string? deviceName,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var rawToken = TokenGenerator.NewToken();
        var session = new SessionRecord(
            Guid.NewGuid().ToString("N"),
            NormalizeDeviceName(deviceName),
            now,
            now.Add(options.SessionLifetime),
            null,
            now);

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using var claim = connection.CreateCommand();
        claim.Transaction = transaction;
        claim.CommandText = """
            UPDATE admin_recovery_codes SET claimed_at = @now
            WHERE id = 1 AND code_hash = @hash AND claimed_at IS NULL AND expires_at > @now;
            """;
        claim.Parameters.AddWithValue("@now", ToStorage(now));
        claim.Parameters.AddWithValue("@hash", TokenGenerator.Hash(code));
        if (await claim.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            transaction.Rollback();
            return null;
        }

        await using var updateCredential = connection.CreateCommand();
        updateCredential.Transaction = transaction;
        updateCredential.CommandText = """
            UPDATE admin_credentials
            SET salt = @salt, password_hash = @hash, iterations = @iterations, created_at = @createdAt
            WHERE id = 1;
            """;
        updateCredential.Parameters.AddWithValue("@salt", credential.Salt);
        updateCredential.Parameters.AddWithValue("@hash", credential.Hash);
        updateCredential.Parameters.AddWithValue("@iterations", credential.Iterations);
        updateCredential.Parameters.AddWithValue("@createdAt", ToStorage(now));
        if (await updateCredential.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            transaction.Rollback();
            return null;
        }

        await using var revokeSessions = connection.CreateCommand();
        revokeSessions.Transaction = transaction;
        revokeSessions.CommandText = "UPDATE auth_sessions SET revoked_at = @now WHERE revoked_at IS NULL;";
        revokeSessions.Parameters.AddWithValue("@now", ToStorage(now));
        await revokeSessions.ExecuteNonQueryAsync(cancellationToken);

        await using var revokePairingCodes = connection.CreateCommand();
        revokePairingCodes.Transaction = transaction;
        revokePairingCodes.CommandText = "UPDATE pairing_codes SET claimed_at = @now WHERE claimed_at IS NULL;";
        revokePairingCodes.Parameters.AddWithValue("@now", ToStorage(now));
        await revokePairingCodes.ExecuteNonQueryAsync(cancellationToken);

        await InsertSessionAsync(connection, transaction, session, TokenGenerator.Hash(rawToken), cancellationToken);
        transaction.Commit();
        return new SessionToken(rawToken, session);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        await pragma.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static async Task InsertSessionAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        SessionRecord session,
        string tokenHash,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO auth_sessions (id, token_hash, device_name, issued_at, expires_at, revoked_at, last_seen_at)
            VALUES (@id, @tokenHash, @deviceName, @issuedAt, @expiresAt, NULL, @lastSeenAt);
            """;
        command.Parameters.AddWithValue("@id", session.Id);
        command.Parameters.AddWithValue("@tokenHash", tokenHash);
        command.Parameters.AddWithValue("@deviceName", session.DeviceName);
        command.Parameters.AddWithValue("@issuedAt", ToStorage(session.IssuedAt));
        command.Parameters.AddWithValue("@expiresAt", ToStorage(session.ExpiresAt));
        command.Parameters.AddWithValue("@lastSeenAt", ToStorage(session.LastSeenAt!.Value));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static SessionRecord ReadSession(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        ReadDate(reader, 2),
        ReadDate(reader, 3),
        ReadNullableDate(reader, 4),
        ReadNullableDate(reader, 5));

    private static DateTimeOffset ReadDate(SqliteDataReader reader, int ordinal) =>
        ParseDate(reader.GetString(ordinal)) ?? DateTimeOffset.UnixEpoch;

    private static DateTimeOffset? ReadNullableDate(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ParseDate(reader.GetString(ordinal));

    private static DateTimeOffset? ParseDate(string? value) => DateTimeOffset.TryParse(
        value,
        CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
        out var date) ? date : null;

    private static string ToStorage(DateTimeOffset value) =>
        value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static string NormalizeDeviceName(string? value)
    {
        var normalized = (value ?? "Unnamed device").Trim();
        return normalized.Length switch
        {
            0 => "Unnamed device",
            > 80 => normalized[..80],
            _ => normalized
        };
    }

    private const string Schema = """
        PRAGMA journal_mode=WAL;
        PRAGMA foreign_keys=ON;

        CREATE TABLE IF NOT EXISTS admin_credentials (
            id INTEGER PRIMARY KEY CHECK(id = 1),
            salt TEXT NOT NULL,
            password_hash TEXT NOT NULL,
            iterations INTEGER NOT NULL,
            created_at TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS auth_sessions (
            id TEXT PRIMARY KEY,
            token_hash TEXT NOT NULL UNIQUE,
            device_name TEXT NOT NULL,
            issued_at TEXT NOT NULL,
            expires_at TEXT NOT NULL,
            revoked_at TEXT NULL,
            last_seen_at TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS auth_sessions_active_idx ON auth_sessions(expires_at, revoked_at);
        CREATE TABLE IF NOT EXISTS pairing_codes (
            id TEXT PRIMARY KEY,
            code_hash TEXT NOT NULL UNIQUE,
            expires_at TEXT NOT NULL,
            claimed_at TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS pairing_codes_expiry_idx ON pairing_codes(expires_at, claimed_at);
        CREATE TABLE IF NOT EXISTS admin_recovery_codes (
            id INTEGER PRIMARY KEY CHECK(id = 1),
            code_hash TEXT NOT NULL,
            created_at TEXT NOT NULL,
            expires_at TEXT NOT NULL,
            claimed_at TEXT NULL
        );
        """;
}
