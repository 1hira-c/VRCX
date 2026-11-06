using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace VRCX.Web.Server;

/// <summary>
/// Fixed read-only projections over the original VRCX SQLite schema. No caller-controlled SQL,
/// table name, URL, or header reaches this boundary. The hidden VRCX renderer remains the sole
/// writer and continues to own all VRChat synchronization behavior.
/// </summary>
public sealed partial class VrcxReadStore(VrcxReadModelOptions options)
{
    private const int MaximumPageOffset = 10_000;
    private const int MaximumPageSize = 200;
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    public bool DatabaseExists => File.Exists(options.DatabasePath);

    public async Task<ServerStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await TryOpenAsync(cancellationToken);
        if (connection is null)
        {
            return EmptyStatus("not_configured", true);
        }

        var prefix = await GetUserPrefixAsync(connection, cancellationToken);
        var configured = prefix is not null;
        var counts = configured
            ? await GetCountsAsync(connection, prefix!, cancellationToken)
            : new ServerCounts(0, 0, 0, 0, 0);

        if (await TableExistsAsync(connection, "vrcx_web_projection_state", cancellationToken))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT sync_state, last_success_at, last_attempt_at, next_retry_at, error_code,
                       pipeline_state, pipeline_connected_at, pipeline_last_event_at,
                       pipeline_last_attempt_at, pipeline_next_retry_at, pipeline_error_code,
                       current_user_id
                FROM vrcx_web_projection_state WHERE id = 1;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var lastSuccess = ReadNullableDate(reader, 1);
                var syncState = ReadNullableString(reader, 0) ?? (configured ? "starting" : "not_configured");
                return new ServerStatus(
                    configured,
                    !configured || syncState == "requires_authentication",
                    new SyncView(
                        syncState,
                        lastSuccess,
                        ReadNullableDate(reader, 2),
                        ReadNullableDate(reader, 3),
                        ReadNullableString(reader, 4),
                        lastSuccess is null || DateTimeOffset.UtcNow - lastSuccess > options.StaleAfter),
                    new PipelineView(
                        ReadNullableString(reader, 5) ?? "disconnected",
                        ReadNullableDate(reader, 6),
                        ReadNullableDate(reader, 7),
                        ReadNullableDate(reader, 8),
                        ReadNullableDate(reader, 9),
                        ReadNullableString(reader, 10)),
                    counts,
                    ReadNullableString(reader, 11));
            }
        }

        return new ServerStatus(
            configured,
            !configured,
            new SyncView(configured ? "starting" : "not_configured", null, null, null, null, true),
            new PipelineView("disconnected", null, null, null, null, null),
            counts,
            null);
    }

    public async Task<PageResult<FriendRecord>> GetFriendsAsync(
        int offset,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var (safeOffset, safeLimit) = NormalizePage(offset, limit);
        await using var connection = await TryOpenAsync(cancellationToken);
        if (connection is null)
        {
            return EmptyPage<FriendRecord>(safeOffset, safeLimit);
        }

        var prefix = await GetUserPrefixAsync(connection, cancellationToken);
        if (prefix is null)
        {
            return EmptyPage<FriendRecord>(safeOffset, safeLimit);
        }

        if (await TableHasRowsAsync(connection, "vrcx_web_user_snapshot", cancellationToken))
        {
            var items = new List<FriendRecord>();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT user_id, state, updated_at, payload_json
                FROM vrcx_web_user_snapshot
                WHERE is_friend = 1
                ORDER BY CASE state WHEN 'online' THEN 0 WHEN 'active' THEN 1 ELSE 2 END,
                         display_name COLLATE NOCASE, user_id
                LIMIT @limit OFFSET @offset;
                """;
            command.Parameters.AddWithValue("@limit", safeLimit);
            command.Parameters.AddWithValue("@offset", safeOffset);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(ReadFriendSnapshot(reader));
            }

            return new PageResult<FriendRecord>(
                items,
                await CountAsync(connection, "vrcx_web_user_snapshot", "is_friend = 1", cancellationToken),
                safeOffset,
                safeLimit);
        }

        var table = $"{prefix}_friend_log_current";
        if (!await TableExistsAsync(connection, table, cancellationToken))
        {
            return EmptyPage<FriendRecord>(safeOffset, safeLimit);
        }

        var fallback = new List<FriendRecord>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT user_id, display_name, trust_level, friend_number
                FROM {QuoteIdentifier(table)}
                ORDER BY display_name COLLATE NOCASE, user_id
                LIMIT @limit OFFSET @offset;
                """;
            command.Parameters.AddWithValue("@limit", safeLimit);
            command.Parameters.AddWithValue("@offset", safeOffset);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetString(0);
                var displayName = reader.GetString(1);
                var raw = JsonSerializer.SerializeToElement(new
                {
                    id,
                    displayName,
                    state = "offline",
                    status = "offline",
                    isFriend = true,
                    trustLevel = ReadNullableString(reader, 2),
                    friendNumber = reader.IsDBNull(3) ? (int?)null : reader.GetInt32(3)
                });
                fallback.Add(new FriendRecord(id, displayName, null, "offline", "offline", null, null, raw));
            }
        }

        return new PageResult<FriendRecord>(
            fallback,
            await CountAsync(connection, table, null, cancellationToken),
            safeOffset,
            safeLimit);
    }

    public async Task<FriendRecord?> GetFriendAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!IsSafeEntityId(id))
        {
            return null;
        }

        await using var connection = await TryOpenAsync(cancellationToken);
        if (connection is null)
        {
            return null;
        }

        if (await TableHasRowsAsync(connection, "vrcx_web_user_snapshot", cancellationToken))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT user_id, state, updated_at, payload_json
                FROM vrcx_web_user_snapshot WHERE user_id = @id AND is_friend = 1;
                """;
            command.Parameters.AddWithValue("@id", id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                return ReadFriendSnapshot(reader);
            }

            // A non-empty projection is an authoritative current snapshot. Falling back
            // here would resurrect a stale former friend from friend_log_current.
            return null;
        }

        var prefix = await GetUserPrefixAsync(connection, cancellationToken);
        if (prefix is null)
        {
            return null;
        }

        var table = $"{prefix}_friend_log_current";
        if (!await TableExistsAsync(connection, table, cancellationToken))
        {
            return null;
        }

        await using var fallback = connection.CreateCommand();
        fallback.CommandText = $"SELECT user_id, display_name, trust_level, friend_number FROM {QuoteIdentifier(table)} WHERE user_id = @id;";
        fallback.Parameters.AddWithValue("@id", id);
        await using var fallbackReader = await fallback.ExecuteReaderAsync(cancellationToken);
        if (!await fallbackReader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var displayName = fallbackReader.GetString(1);
        var raw = JsonSerializer.SerializeToElement(new
        {
            id,
            displayName,
            state = "offline",
            status = "offline",
            isFriend = true,
            trustLevel = ReadNullableString(fallbackReader, 2),
            friendNumber = fallbackReader.IsDBNull(3) ? (int?)null : fallbackReader.GetInt32(3)
        });
        return new FriendRecord(id, displayName, null, "offline", "offline", null, null, raw);
    }

    public async Task<PageResult<FriendHistoryRecord>> GetFriendHistoryAsync(
        string? friendId,
        int offset,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var (safeOffset, safeLimit) = NormalizePage(offset, limit);
        if (friendId is not null && !IsSafeEntityId(friendId))
        {
            return EmptyPage<FriendHistoryRecord>(safeOffset, safeLimit);
        }

        await using var connection = await TryOpenAsync(cancellationToken);
        if (connection is null)
        {
            return EmptyPage<FriendHistoryRecord>(safeOffset, safeLimit);
        }

        var prefix = await GetUserPrefixAsync(connection, cancellationToken);
        if (prefix is null)
        {
            return EmptyPage<FriendHistoryRecord>(safeOffset, safeLimit);
        }

        var table = $"{prefix}_friend_log_history";
        if (!await TableExistsAsync(connection, table, cancellationToken))
        {
            return EmptyPage<FriendHistoryRecord>(safeOffset, safeLimit);
        }

        var items = new List<FriendHistoryRecord>();
        var where = friendId is null ? string.Empty : " WHERE user_id = @friendId";
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT id, user_id, created_at, type, display_name, previous_display_name,
                       trust_level, previous_trust_level, friend_number
                FROM {QuoteIdentifier(table)}{where}
                ORDER BY created_at DESC, id DESC LIMIT @limit OFFSET @offset;
                """;
            if (friendId is not null)
            {
                command.Parameters.AddWithValue("@friendId", friendId);
            }
            command.Parameters.AddWithValue("@limit", safeLimit);
            command.Parameters.AddWithValue("@offset", safeOffset);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(new FriendHistoryRecord(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    ReadNullableDate(reader, 2),
                    ReadNullableString(reader, 3),
                    ReadNullableString(reader, 4),
                    ReadNullableString(reader, 5),
                    ReadNullableString(reader, 6),
                    ReadNullableString(reader, 7),
                    reader.IsDBNull(8) ? null : reader.GetInt32(8)));
            }
        }

        var total = await CountAsync(
            connection,
            table,
            friendId is null ? null : "user_id = @friendId",
            cancellationToken,
            friendId is null ? null : ("@friendId", (object)friendId));
        return new PageResult<FriendHistoryRecord>(items, total, safeOffset, safeLimit);
    }

    private async Task<SqliteConnection?> TryOpenAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(options.DatabasePath))
        {
            return null;
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 5
        }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA query_only=ON; PRAGMA busy_timeout=5000;";
            await pragma.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task<string?> GetUserPrefixAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "configs", cancellationToken))
        {
            return null;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM configs WHERE key = 'config:lastuserloggedin';";
        var userId = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (!IsSafeEntityId(userId) || !userId!.StartsWith("usr_", StringComparison.Ordinal))
        {
            return null;
        }

        var prefix = userId.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal);
        return prefix.Length > 0 && char.IsDigit(prefix[0]) ? $"_{prefix}" : prefix;
    }

    private static FriendRecord ReadFriendSnapshot(SqliteDataReader reader)
    {
        var id = reader.GetString(0);
        var state = ReadNullableString(reader, 1);
        var updatedAt = ReadNullableDate(reader, 2);
        var raw = ParseJson(reader.GetString(3));
        var root = raw.ValueKind == JsonValueKind.Object ? raw : EmptyObject;
        var displayName = JsonString(root, "displayName") ?? id;
        var location = JsonString(root, "location");
        var worldName = JsonString(root, "worldName") ?? JsonNestedString(root, "$location", "worldName");
        return new FriendRecord(
            id,
            displayName,
            JsonString(root, "username"),
            JsonString(root, "status") ?? state,
            location,
            worldName,
            updatedAt,
            root);
    }

    private static ServerStatus EmptyStatus(string state, bool authenticationRequired) => new(
        false,
        authenticationRequired,
        new SyncView(state, null, null, null, null, true),
        new PipelineView("disconnected", null, null, null, null, null),
        new ServerCounts(0, 0, 0, 0, 0),
        null);

    private static (int Offset, int Limit) NormalizePage(int offset, int limit) =>
        (Math.Clamp(offset, 0, MaximumPageOffset), Math.Clamp(limit <= 0 ? 50 : limit, 1, MaximumPageSize));

    private static PageResult<T> EmptyPage<T>(int offset, int limit) => new([], 0, offset, limit);

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

    private static async Task<bool> TableHasRowsAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, table, cancellationToken))
        {
            return false;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT 1 FROM {QuoteIdentifier(table)} LIMIT 1;";
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<int> CountAsync(
        SqliteConnection connection,
        string table,
        string? where,
        CancellationToken cancellationToken,
        (string Name, object Value)? parameter = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {QuoteIdentifier(table)}{(where is null ? string.Empty : $" WHERE {where}")};";
        if (parameter is { } value)
        {
            command.Parameters.AddWithValue(value.Name, value.Value);
        }
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static string QuoteIdentifier(string identifier)
    {
        if (identifier.Length is < 1 or > 160 || identifier.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
        {
            throw new InvalidOperationException("Unsafe internal SQLite identifier.");
        }
        return $"\"{identifier}\"";
    }

    private static bool IsSafeEntityId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
        {
            return false;
        }
        return value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    }

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset? ReadNullableDate(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ParseDate(reader.GetString(ordinal));

    private static DateTimeOffset? ParseDate(string? value) => DateTimeOffset.TryParse(
        value,
        CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
        out var date) ? date : null;

    private static JsonElement ParseJson(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return EmptyObject;
        }
        try
        {
            return JsonDocument.Parse(value).RootElement.Clone();
        }
        catch (JsonException)
        {
            return EmptyObject;
        }
    }

    private static string? JsonString(JsonElement value, string property)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(property, out var item))
        {
            return null;
        }
        return item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    }

    private static string? JsonNestedString(JsonElement value, string parent, string property) =>
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(parent, out var nested) &&
        nested.ValueKind == JsonValueKind.Object
            ? JsonString(nested, property)
            : null;

    private async Task<ServerCounts> GetCountsAsync(
        SqliteConnection connection,
        string prefix,
        CancellationToken cancellationToken)
    {
        var friendTable = await TableHasRowsAsync(connection, "vrcx_web_user_snapshot", cancellationToken)
            ? "vrcx_web_user_snapshot"
            : $"{prefix}_friend_log_current";
        var friends = await TableExistsAsync(connection, friendTable, cancellationToken)
            ? await CountAsync(
                connection,
                friendTable,
                friendTable == "vrcx_web_user_snapshot" ? "is_friend = 1" : null,
                cancellationToken)
            : 0;
        var feed = await SumTableCountsAsync(connection, FeedTables(prefix), cancellationToken);
        var notifications = await SumTableCountsAsync(
            connection,
            [$"{prefix}_notifications", $"{prefix}_notifications_v2"],
            cancellationToken);
        var favorites = await IsFavoriteProjectionReadyAsync(connection, cancellationToken)
            ? await CountAsync(connection, "vrcx_web_favorite_snapshot", null, cancellationToken)
            : await SumTableCountsAsync(
                connection,
                ["favorite_friend", "favorite_world", "favorite_avatar"],
                cancellationToken);
        var gameLog = await SumTableCountsAsync(connection, GameLogTables, cancellationToken);
        return new ServerCounts(friends, feed, notifications, favorites, gameLog);
    }

    private static async Task<int> SumTableCountsAsync(
        SqliteConnection connection,
        IEnumerable<string> tables,
        CancellationToken cancellationToken)
    {
        var total = 0;
        foreach (var table in tables)
        {
            if (await TableExistsAsync(connection, table, cancellationToken))
            {
                total += await CountAsync(connection, table, null, cancellationToken);
            }
        }
        return total;
    }

    private static string[] FeedTables(string prefix) =>
    [
        $"{prefix}_feed_gps",
        $"{prefix}_feed_status",
        $"{prefix}_feed_bio",
        $"{prefix}_feed_avatar",
        $"{prefix}_feed_online_offline"
    ];

    private static readonly string[] GameLogTables =
    [
        "gamelog_location",
        "gamelog_join_leave",
        "gamelog_portal_spawn",
        "gamelog_video_play",
        "gamelog_resource_load",
        "gamelog_event",
        "gamelog_external"
    ];
}
