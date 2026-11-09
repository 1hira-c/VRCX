using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace VRCX.Web.Server;

public sealed partial class VrcxReadStore
{
    // NotificationV2.data is a closed union in the vrchat package. Keep the
    // browser contract equally explicit: these fields provide the badge,
    // boop, event, and group context rendered by VRCX without forwarding an
    // arbitrary upstream object.
    private static readonly string[] NotificationDataFields =
    [
        "badgeDescription",
        "badgeId",
        "badgeName",
        "boopingUserDisplayName",
        "ownerId",
        "ownerName",
        "title",
        "announcementTitle",
        "groupId",
        "groupName",
        "transferTargetDisplayName",
        "ownerUserDisplayName"
    ];

    // Keep the meaningful scalar context used by VRCX's legacy and v2
    // notification renderers. Some snapshots predate the current generated
    // package union, so include the established invite/request/user/location
    // names as well as the current boop fields. Unknown and nested values are
    // still rejected rather than forwarding an arbitrary object.
    private static readonly string[] NotificationDetailFields =
    [
        "worldId",
        "worldName",
        "groupId",
        "groupName",
        "location",
        "instanceId",
        "instanceName",
        "inviteMessage",
        "requestMessage",
        "responseMessage",
        "senderUserId",
        "senderUsername",
        "senderDisplayName",
        "recipientUserId",
        "recipientUsername",
        "recipientDisplayName",
        "receiverUserId",
        "receiverUsername",
        "receiverDisplayName",
        "targetUserId",
        "targetUsername",
        "targetDisplayName",
        "imageUrl",
        "thumbnailImageUrl",
        "senderUserIcon",
        "emojiId",
        "emojiVersion",
        "inventoryItemId"
    ];

    public async Task<PageResult<NotificationRecord>> GetNotificationsAsync(
        int offset,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var (safeOffset, safeLimit) = NormalizePage(offset, limit);
        await using var connection = await TryOpenAsync(cancellationToken);
        if (connection is null)
        {
            return EmptyPage<NotificationRecord>(safeOffset, safeLimit);
        }

        var prefix = await GetUserPrefixAsync(connection, cancellationToken);
        if (prefix is null)
        {
            return EmptyPage<NotificationRecord>(safeOffset, safeLimit);
        }

        // VRCX's v2 notification table does not persist receiverUserId even
        // though NotificationV2 includes it. For this single-user server the
        // selected VRCX account is the unambiguous receiver and restores the
        // otherwise missing "who received it" context.
        var currentUserId = await GetNotificationCurrentUserIdAsync(connection, cancellationToken);

        var readLimit = checked(safeOffset + safeLimit);
        var rows = new List<NotificationRecord>();
        var legacy = $"{prefix}_notifications";
        if (await TableExistsAsync(connection, legacy, cancellationToken))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT id, created_at, type, sender_user_id, sender_username, receiver_user_id,
                       message, world_id, world_name, image_url, invite_message, request_message,
                       response_message, expired
                FROM {QuoteIdentifier(legacy)} ORDER BY created_at DESC LIMIT @limit;
                """;
            command.Parameters.AddWithValue("@limit", readLimit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new NotificationRecord(
                    reader.GetString(0),
                    ReadNullableString(reader, 2),
                    ReadNullableDate(reader, 1),
                    null,
                    ReadNullableString(reader, 6),
                    null,
                    JsonSerializer.SerializeToElement(new
                    {
                        senderUserId = ReadNullableString(reader, 3),
                        senderUsername = ReadNullableString(reader, 4),
                        receiverUserId = ReadNullableString(reader, 5) ?? currentUserId,
                        message = ReadNullableString(reader, 6),
                        worldId = ReadNullableString(reader, 7),
                        worldName = ReadNullableString(reader, 8),
                        imageUrl = ReadNullableString(reader, 9),
                        inviteMessage = ReadNullableString(reader, 10),
                        requestMessage = ReadNullableString(reader, 11),
                        responseMessage = ReadNullableString(reader, 12),
                        expired = !reader.IsDBNull(13) && reader.GetInt64(13) != 0
                    })));
            }
        }

        var v2 = $"{prefix}_notifications_v2";
        if (await TableExistsAsync(connection, v2, cancellationToken))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT id, created_at, updated_at, expires_at, type, link, link_text, message,
                       title, image_url, seen, sender_user_id, sender_username, data, responses, details
                FROM {QuoteIdentifier(v2)} ORDER BY created_at DESC LIMIT @limit;
                """;
            command.Parameters.AddWithValue("@limit", readLimit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new NotificationRecord(
                    reader.GetString(0),
                    ReadNullableString(reader, 4),
                    ReadNullableDate(reader, 1),
                    ReadNullableString(reader, 8),
                    ReadNullableString(reader, 7),
                    reader.IsDBNull(10) ? null : reader.GetInt64(10) != 0,
                    JsonSerializer.SerializeToElement(new
                    {
                        updatedAt = ReadNullableString(reader, 2),
                        expiresAt = ReadNullableString(reader, 3),
                        link = ReadNullableString(reader, 5),
                        linkText = ReadNullableString(reader, 6),
                        message = ReadNullableString(reader, 7),
                        title = ReadNullableString(reader, 8),
                        imageUrl = ReadNullableString(reader, 9),
                        seen = !reader.IsDBNull(10) && reader.GetInt64(10) != 0,
                        senderUserId = ReadNullableString(reader, 11),
                        senderUsername = ReadNullableString(reader, 12),
                        receiverUserId = currentUserId,
                        version = 2,
                        data = SelectNotificationScalars(reader, 13, NotificationDataFields),
                        // The web deployment is read-only, so response actions
                        // are deliberately not transferred to the browser.
                        responses = Array.Empty<object>(),
                        details = SelectNotificationScalars(reader, 15, NotificationDetailFields)
                    })));
            }
        }

        var items = rows
            .OrderByDescending(row => row.CreatedAt ?? DateTimeOffset.UnixEpoch)
            .ThenByDescending(row => row.Id, StringComparer.Ordinal)
            .Skip(safeOffset)
            .Take(safeLimit)
            .ToArray();
        var total = await SumTableCountsAsync(connection, [legacy, v2], cancellationToken);
        return new PageResult<NotificationRecord>(items, total, safeOffset, safeLimit);
    }

    public async Task<FavoritePageResult> GetFavoritesAsync(
        int offset,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var (safeOffset, safeLimit) = NormalizePage(offset, limit);
        await using var connection = await TryOpenAsync(cancellationToken);
        if (connection is null)
        {
            return new FavoritePageResult([], 0, safeOffset, safeLimit, false);
        }

        if (await IsFavoriteProjectionReadyAsync(connection, cancellationToken))
        {
            return await ReadFavoriteProjectionAsync(connection, safeOffset, safeLimit, cancellationToken);
        }

        var rows = new List<FavoriteRecord>();
        var prefix = await GetUserPrefixAsync(connection, cancellationToken);
        if (await TableExistsAsync(connection, "favorite_friend", cancellationToken))
        {
            await ReadFriendFavoritesAsync(connection, prefix, rows, cancellationToken);
        }
        if (await TableExistsAsync(connection, "favorite_world", cancellationToken))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT f.id, f.created_at, f.world_id, f.group_name,
                       c.author_id, c.author_name, c.created_at, c.description, c.image_url,
                       c.name, c.release_status, c.thumbnail_image_url, c.updated_at, c.version
                FROM favorite_world f LEFT JOIN cache_world c ON c.id = f.world_id;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var target = JsonSerializer.SerializeToElement(new
                {
                    id = reader.GetString(2),
                    authorId = ReadNullableString(reader, 4),
                    authorName = ReadNullableString(reader, 5),
                    created_at = ReadNullableString(reader, 6),
                    description = ReadNullableString(reader, 7),
                    imageUrl = ReadNullableString(reader, 8),
                    name = ReadNullableString(reader, 9),
                    releaseStatus = ReadNullableString(reader, 10),
                    thumbnailImageUrl = ReadNullableString(reader, 11),
                    updated_at = ReadNullableString(reader, 12),
                    version = reader.IsDBNull(13) ? (long?)null : reader.GetInt64(13)
                });
                rows.Add(CreateFavorite(reader, "world", reader.GetString(2), ReadNullableString(reader, 9), target));
            }
        }
        if (await TableExistsAsync(connection, "favorite_avatar", cancellationToken))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT f.id, f.created_at, f.avatar_id, f.group_name,
                       c.author_id, c.author_name, c.created_at, c.description, c.image_url,
                       c.name, c.release_status, c.thumbnail_image_url, c.updated_at, c.version
                FROM favorite_avatar f LEFT JOIN cache_avatar c ON c.id = f.avatar_id;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var target = JsonSerializer.SerializeToElement(new
                {
                    id = reader.GetString(2),
                    authorId = ReadNullableString(reader, 4),
                    authorName = ReadNullableString(reader, 5),
                    created_at = ReadNullableString(reader, 6),
                    description = ReadNullableString(reader, 7),
                    imageUrl = ReadNullableString(reader, 8),
                    name = ReadNullableString(reader, 9),
                    releaseStatus = ReadNullableString(reader, 10),
                    thumbnailImageUrl = ReadNullableString(reader, 11),
                    updated_at = ReadNullableString(reader, 12),
                    version = reader.IsDBNull(13) ? (long?)null : reader.GetInt64(13)
                });
                rows.Add(CreateFavorite(reader, "avatar", reader.GetString(2), ReadNullableString(reader, 9), target));
            }
        }

        var ordered = rows
            .OrderByDescending(row => row.CreatedAt ?? DateTimeOffset.UnixEpoch)
            .ThenByDescending(row => row.Id, StringComparer.Ordinal)
            .Select((row, index) => row with { SortOrder = index })
            .ToArray();
        var items = ordered
            .Skip(safeOffset)
            .Take(safeLimit)
            .ToArray();
        return new FavoritePageResult(items, ordered.Length, safeOffset, safeLimit, false);
    }

    public async Task<MemoRecord?> GetMemoAsync(
        string entityType,
        string id,
        CancellationToken cancellationToken = default)
    {
        if (!IsSafeEntityId(id))
        {
            return null;
        }
        var mapping = entityType switch
        {
            "user" => (Table: "memos", Id: "user_id"),
            "world" => (Table: "world_memos", Id: "world_id"),
            "avatar" => (Table: "avatar_memos", Id: "avatar_id"),
            _ => default
        };
        if (mapping.Table is null)
        {
            return null;
        }

        await using var connection = await TryOpenAsync(cancellationToken);
        if (connection is null || !await TableExistsAsync(connection, mapping.Table, cancellationToken))
        {
            return null;
        }
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {QuoteIdentifier(mapping.Id)}, edited_at, memo FROM {QuoteIdentifier(mapping.Table)} WHERE {QuoteIdentifier(mapping.Id)} = @id;";
        command.Parameters.AddWithValue("@id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new MemoRecord(entityType, reader.GetString(0), ReadNullableDate(reader, 1), reader.GetString(2))
            : null;
    }

    public async Task<AnalyticsSummary> GetAnalyticsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await TryOpenAsync(cancellationToken);
        if (connection is null)
        {
            return new AnalyticsSummary(0, 0, 0, 0, 0, 0, 0, 0, null, null);
        }
        var prefix = await GetUserPrefixAsync(connection, cancellationToken);
        if (prefix is null)
        {
            return new AnalyticsSummary(0, 0, 0, 0, 0, 0, 0, 0, null, null);
        }
        var counts = await GetCountsAsync(connection, prefix, cancellationToken);
        var activityTable = $"{prefix}_activity_sessions_v2";
        var activitySessions = 0;
        var activityUsers = 0;
        long activityMinutes = 0;
        if (await TableExistsAsync(connection, activityTable, cancellationToken))
        {
            await using var activity = connection.CreateCommand();
            activity.CommandText = $"""
                SELECT COUNT(*), COUNT(DISTINCT user_id),
                       COALESCE(SUM(MAX(0, COALESCE(end_at, start_at) - start_at)) / 60000, 0)
                FROM {QuoteIdentifier(activityTable)};
                """;
            await using var reader = await activity.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                activitySessions = reader.GetInt32(0);
                activityUsers = reader.GetInt32(1);
                activityMinutes = reader.GetInt64(2);
            }
        }

        DateTimeOffset? oldest = null;
        DateTimeOffset? newest = null;
        foreach (var table in GameLogTables)
        {
            if (!await TableExistsAsync(connection, table, cancellationToken))
            {
                continue;
            }
            await using var bounds = connection.CreateCommand();
            bounds.CommandText = $"SELECT MIN(created_at), MAX(created_at) FROM {QuoteIdentifier(table)};";
            await using var reader = await bounds.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                continue;
            }
            var tableOldest = ReadNullableDate(reader, 0);
            var tableNewest = ReadNullableDate(reader, 1);
            if (tableOldest is not null && (oldest is null || tableOldest < oldest)) oldest = tableOldest;
            if (tableNewest is not null && (newest is null || tableNewest > newest)) newest = tableNewest;
        }

        return new AnalyticsSummary(
            counts.Friends,
            counts.Feed,
            counts.Notifications,
            counts.Favorites,
            counts.GameLog,
            activitySessions,
            activityUsers,
            activityMinutes,
            oldest,
            newest);
    }

    private static async Task ReadFriendFavoritesAsync(
        SqliteConnection connection,
        string? prefix,
        ICollection<FavoriteRecord> rows,
        CancellationToken cancellationToken)
    {
        var hasSnapshots = await TableExistsAsync(connection, "vrcx_web_user_snapshot", cancellationToken);
        var historyTable = prefix is null ? null : $"{prefix}_friend_log_current";
        var hasHistory = historyTable is not null && await TableExistsAsync(connection, historyTable, cancellationToken);
        var join = hasSnapshots
            ? "LEFT JOIN vrcx_web_user_snapshot u ON u.user_id = f.user_id"
            : hasHistory
                ? $"LEFT JOIN {QuoteIdentifier(historyTable!)} u ON u.user_id = f.user_id"
                : string.Empty;
        var display = join.Length == 0 ? "NULL" : "u.display_name";
        var payload = hasSnapshots ? "u.payload_json" : "NULL";
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT f.id, f.created_at, f.user_id, f.group_name, {display}, {payload}
            FROM favorite_friend f {join};
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetString(2);
            var name = ReadNullableString(reader, 4) ?? id;
            var target = reader.IsDBNull(5)
                ? JsonSerializer.SerializeToElement(new { id, displayName = name, isFriend = true })
                : ParseJson(reader.GetString(5));
            rows.Add(CreateFavorite(reader, "friend", id, name, target));
        }
    }

    private static FavoriteRecord CreateFavorite(
        SqliteDataReader reader,
        string kind,
        string targetId,
        string? targetName,
        JsonElement targetPayload)
    {
        var id = reader.GetInt64(0).ToString(CultureInfo.InvariantCulture);
        var createdAt = ReadNullableDate(reader, 1);
        var groupName = ReadNullableString(reader, 3) ?? string.Empty;
        return new FavoriteRecord(
            id,
            kind,
            targetId,
            groupName.Length == 0 ? [] : [groupName],
            $"{kind}:{groupName}",
            0,
            createdAt,
            targetName,
            targetPayload);
    }

    private static async Task<string?> GetNotificationCurrentUserIdAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        return await VrcxDataOwner.GetUserIdAsync(connection, cancellationToken);
    }

    private static JsonElement SelectNotificationScalars(
        SqliteDataReader reader,
        int ordinal,
        IReadOnlyList<string> allowedFields)
    {
        if (reader.IsDBNull(ordinal))
        {
            return EmptyObject;
        }

        var parsed = ParseJson(reader.GetString(ordinal));
        if (parsed.ValueKind != JsonValueKind.Object)
        {
            return EmptyObject;
        }

        var selected = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in allowedFields)
        {
            if (!parsed.TryGetProperty(field, out var value) || value.ValueKind is not (
                    JsonValueKind.String or
                    JsonValueKind.Number or
                    JsonValueKind.True or
                    JsonValueKind.False))
            {
                continue;
            }
            selected[field] = value.Clone();
        }
        return JsonSerializer.SerializeToElement(selected);
    }
}
