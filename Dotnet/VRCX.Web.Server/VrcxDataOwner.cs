using System.Globalization;
using Microsoft.Data.Sqlite;

namespace VRCX.Web.Server;

/// <summary>
/// The durable, non-secret owner of original VRCX per-user history tables.
/// The importer creates this record from a validated snapshot; the hidden
/// renderer updates it after a successful native login. It is deliberately
/// separate from cookie/session state and has no browser-supplied selector.
/// </summary>
internal static class VrcxDataOwner
{
    internal const string TableName = "vrcx_web_data_owner";
    private const string LastUserLoggedInConfigKey = "config:lastuserloggedin";

    internal static bool IsSafeUserId(string? userId) =>
        userId is { Length: > 4 and <= 128 } &&
        userId.StartsWith("usr_", StringComparison.Ordinal) &&
        userId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    internal static string? ToUserPrefix(string? userId)
    {
        if (!IsSafeUserId(userId))
        {
            return null;
        }

        var prefix = userId!.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal);
        return prefix.Length > 0 && char.IsDigit(prefix[0]) ? $"_{prefix}" : prefix;
    }

    internal static async Task<string?> GetUserPrefixAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        return ToUserPrefix(await GetUserIdAsync(connection, cancellationToken));
    }

    internal static async Task<string?> GetUserIdAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var metadata = await ReadMetadataAsync(connection, cancellationToken);
        if (metadata.Exists)
        {
            return IsSafeUserId(metadata.UserId) ? metadata.UserId : null;
        }

        if (!await TableExistsAsync(connection, "configs", cancellationToken))
        {
            return null;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM configs WHERE key = @key;";
        command.Parameters.AddWithValue("@key", LastUserLoggedInConfigKey);
        var userId = await command.ExecuteScalarAsync(cancellationToken) as string;
        return IsSafeUserId(userId) ? userId : null;
    }

    internal static async Task SetImportedOwnerAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? userId,
        CancellationToken cancellationToken)
    {
        await EnsureTableAsync(connection, transaction, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (userId is null)
        {
            command.CommandText = $"DELETE FROM {QuoteIdentifier(TableName)} WHERE id = 1;";
        }
        else
        {
            if (!IsSafeUserId(userId))
            {
                throw new InvalidOperationException("An imported data owner must be a valid VRChat user ID.");
            }
            command.CommandText = $"""
                INSERT INTO {QuoteIdentifier(TableName)} (id, user_id, imported_at)
                VALUES (1, @userId, @importedAt)
                ON CONFLICT(id) DO UPDATE SET
                    user_id = excluded.user_id,
                    imported_at = excluded.imported_at;
                """;
            command.Parameters.AddWithValue("@userId", userId);
            command.Parameters.AddWithValue("@importedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        }
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<(bool Exists, string? UserId)> ReadMetadataAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, TableName, cancellationToken))
        {
            return (false, null);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT user_id FROM {QuoteIdentifier(TableName)} WHERE id = 1;";
        var userId = await command.ExecuteScalarAsync(cancellationToken) as string;
        return userId is null ? (false, (string?)null) : (true, userId);
    }

    private static async Task EnsureTableAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            CREATE TABLE IF NOT EXISTS {QuoteIdentifier(TableName)} (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                user_id TEXT NOT NULL,
                imported_at TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name LIMIT 1;";
        command.Parameters.AddWithValue("@name", table);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
