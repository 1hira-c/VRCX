using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace VRCX.Web.Server;

public sealed partial class VrcxReadStore
{
    public async Task<PageResult<FeedRecord>> GetFeedAsync(
        int offset,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var (safeOffset, safeLimit) = NormalizePage(offset, limit);
        await using var connection = await TryOpenAsync(cancellationToken);
        if (connection is null)
        {
            return EmptyPage<FeedRecord>(safeOffset, safeLimit);
        }

        var prefix = await GetUserPrefixAsync(connection, cancellationToken);
        if (prefix is null)
        {
            return EmptyPage<FeedRecord>(safeOffset, safeLimit);
        }

        var readLimit = checked(safeOffset + safeLimit);
        var rows = new List<FeedRecord>();
        await ReadFeedTableAsync(
            connection,
            $"{prefix}_feed_gps",
            readLimit,
            reader => new FeedRecord(
                reader.GetInt64(0),
                "gps",
                ReadNullableString(reader, 2),
                ReadNullableString(reader, 3),
                ReadNullableDate(reader, 1),
                JsonSerializer.SerializeToElement(new
                {
                    type = "GPS",
                    location = ReadNullableString(reader, 4),
                    worldName = ReadNullableString(reader, 5),
                    previousLocation = ReadNullableString(reader, 6),
                    time = reader.IsDBNull(7) ? (long?)null : reader.GetInt64(7),
                    groupName = ReadNullableString(reader, 8)
                })),
            rows,
            cancellationToken,
            "id, created_at, user_id, display_name, location, world_name, previous_location, time, group_name");
        await ReadFeedTableAsync(
            connection,
            $"{prefix}_feed_status",
            readLimit,
            reader => new FeedRecord(
                reader.GetInt64(0),
                "status",
                ReadNullableString(reader, 2),
                ReadNullableString(reader, 3),
                ReadNullableDate(reader, 1),
                JsonSerializer.SerializeToElement(new
                {
                    type = "Status",
                    status = ReadNullableString(reader, 4),
                    statusDescription = ReadNullableString(reader, 5),
                    previousStatus = ReadNullableString(reader, 6),
                    previousStatusDescription = ReadNullableString(reader, 7)
                })),
            rows,
            cancellationToken,
            "id, created_at, user_id, display_name, status, status_description, previous_status, previous_status_description");
        await ReadFeedTableAsync(
            connection,
            $"{prefix}_feed_bio",
            readLimit,
            reader => new FeedRecord(
                reader.GetInt64(0),
                "bio",
                ReadNullableString(reader, 2),
                ReadNullableString(reader, 3),
                ReadNullableDate(reader, 1),
                JsonSerializer.SerializeToElement(new
                {
                    type = "Bio",
                    bio = ReadNullableString(reader, 4),
                    previousBio = ReadNullableString(reader, 5),
                    bioLinks = ParseJsonArray(reader, 6),
                    previousBioLinks = ParseJsonArray(reader, 7)
                })),
            rows,
            cancellationToken,
            "id, created_at, user_id, display_name, bio, previous_bio, bio_links, previous_bio_links");
        await ReadFeedTableAsync(
            connection,
            $"{prefix}_feed_avatar",
            readLimit,
            reader => new FeedRecord(
                reader.GetInt64(0),
                "avatar",
                ReadNullableString(reader, 2),
                ReadNullableString(reader, 3),
                ReadNullableDate(reader, 1),
                JsonSerializer.SerializeToElement(new
                {
                    type = "Avatar",
                    ownerId = ReadNullableString(reader, 4),
                    avatarName = ReadNullableString(reader, 5),
                    currentAvatarImageUrl = ReadNullableString(reader, 6),
                    currentAvatarThumbnailImageUrl = ReadNullableString(reader, 7),
                    previousCurrentAvatarImageUrl = ReadNullableString(reader, 8),
                    previousCurrentAvatarThumbnailImageUrl = ReadNullableString(reader, 9)
                })),
            rows,
            cancellationToken,
            "id, created_at, user_id, display_name, owner_id, avatar_name, current_avatar_image_url, current_avatar_thumbnail_image_url, previous_current_avatar_image_url, previous_current_avatar_thumbnail_image_url");
        await ReadFeedTableAsync(
            connection,
            $"{prefix}_feed_online_offline",
            readLimit,
            reader => new FeedRecord(
                reader.GetInt64(0),
                "online_offline",
                ReadNullableString(reader, 2),
                ReadNullableString(reader, 3),
                ReadNullableDate(reader, 1),
                JsonSerializer.SerializeToElement(new
                {
                    type = ReadNullableString(reader, 4),
                    location = ReadNullableString(reader, 5),
                    worldName = ReadNullableString(reader, 6),
                    time = reader.IsDBNull(7) ? (long?)null : reader.GetInt64(7),
                    groupName = ReadNullableString(reader, 8)
                })),
            rows,
            cancellationToken,
            "id, created_at, user_id, display_name, type, location, world_name, time, group_name");

        var items = rows
            .OrderByDescending(row => row.CreatedAt ?? DateTimeOffset.UnixEpoch)
            .ThenByDescending(row => row.Id)
            .Skip(safeOffset)
            .Take(safeLimit)
            .ToArray();
        var total = await SumTableCountsAsync(connection, FeedTables(prefix), cancellationToken);
        return new PageResult<FeedRecord>(items, total, safeOffset, safeLimit);
    }

    public async Task<PageResult<GameLogRecord>> GetGameLogAsync(
        int offset,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var (safeOffset, safeLimit) = NormalizePage(offset, limit);
        await using var connection = await TryOpenAsync(cancellationToken);
        if (connection is null)
        {
            return EmptyPage<GameLogRecord>(safeOffset, safeLimit);
        }

        var readLimit = checked(safeOffset + safeLimit);
        var rows = new List<GameLogRecord>();
        await ReadGameLogTableAsync(
            connection,
            "gamelog_location",
            readLimit,
            reader => new GameLogRecord(
                reader.GetInt64(0),
                "location",
                ReadNullableDate(reader, 1),
                JsonSerializer.SerializeToElement(new
                {
                    type = "Location",
                    location = ReadNullableString(reader, 2),
                    worldId = ReadNullableString(reader, 3),
                    worldName = ReadNullableString(reader, 4),
                    time = reader.IsDBNull(5) ? (long?)null : reader.GetInt64(5),
                    groupName = ReadNullableString(reader, 6)
                })),
            rows,
            cancellationToken,
            "id, created_at, location, world_id, world_name, time, group_name");
        await ReadGameLogTableAsync(
            connection,
            "gamelog_join_leave",
            readLimit,
            reader => new GameLogRecord(
                reader.GetInt64(0),
                "join_leave",
                ReadNullableDate(reader, 1),
                JsonSerializer.SerializeToElement(new
                {
                    type = ReadNullableString(reader, 2),
                    displayName = ReadNullableString(reader, 3),
                    location = ReadNullableString(reader, 4),
                    userId = ReadNullableString(reader, 5),
                    time = reader.IsDBNull(6) ? (long?)null : reader.GetInt64(6)
                })),
            rows,
            cancellationToken,
            "id, created_at, type, display_name, location, user_id, time");
        await ReadGameLogTableAsync(
            connection,
            "gamelog_portal_spawn",
            readLimit,
            reader => new GameLogRecord(
                reader.GetInt64(0),
                "portal_spawn",
                ReadNullableDate(reader, 1),
                JsonSerializer.SerializeToElement(new
                {
                    type = "PortalSpawn",
                    displayName = ReadNullableString(reader, 2),
                    location = ReadNullableString(reader, 3),
                    userId = ReadNullableString(reader, 4),
                    instanceId = ReadNullableString(reader, 5),
                    worldName = ReadNullableString(reader, 6)
                })),
            rows,
            cancellationToken,
            "id, created_at, display_name, location, user_id, instance_id, world_name");
        await ReadGameLogTableAsync(
            connection,
            "gamelog_video_play",
            readLimit,
            reader => new GameLogRecord(
                reader.GetInt64(0),
                "video_play",
                ReadNullableDate(reader, 1),
                JsonSerializer.SerializeToElement(new
                {
                    type = "VideoPlay",
                    videoUrl = ReadNullableString(reader, 2),
                    videoName = ReadNullableString(reader, 3),
                    videoId = ReadNullableString(reader, 4),
                    location = ReadNullableString(reader, 5),
                    displayName = ReadNullableString(reader, 6),
                    userId = ReadNullableString(reader, 7)
                })),
            rows,
            cancellationToken,
            "id, created_at, video_url, video_name, video_id, location, display_name, user_id");
        await ReadGameLogTableAsync(
            connection,
            "gamelog_resource_load",
            readLimit,
            reader => new GameLogRecord(
                reader.GetInt64(0),
                "resource_load",
                ReadNullableDate(reader, 1),
                JsonSerializer.SerializeToElement(new
                {
                    type = "ResourceLoad",
                    resourceUrl = ReadNullableString(reader, 2),
                    resourceType = ReadNullableString(reader, 3),
                    location = ReadNullableString(reader, 4)
                })),
            rows,
            cancellationToken,
            "id, created_at, resource_url, resource_type, location");
        await ReadGameLogTableAsync(
            connection,
            "gamelog_event",
            readLimit,
            reader => new GameLogRecord(
                reader.GetInt64(0),
                "event",
                ReadNullableDate(reader, 1),
                JsonSerializer.SerializeToElement(new { type = "Event", data = ReadNullableString(reader, 2) })),
            rows,
            cancellationToken,
            "id, created_at, data");
        await ReadGameLogTableAsync(
            connection,
            "gamelog_external",
            readLimit,
            reader => new GameLogRecord(
                reader.GetInt64(0),
                "external",
                ReadNullableDate(reader, 1),
                JsonSerializer.SerializeToElement(new
                {
                    type = "External",
                    message = ReadNullableString(reader, 2),
                    displayName = ReadNullableString(reader, 3),
                    userId = ReadNullableString(reader, 4),
                    location = ReadNullableString(reader, 5)
                })),
            rows,
            cancellationToken,
            "id, created_at, message, display_name, user_id, location");

        var items = rows
            .OrderByDescending(row => row.CreatedAt ?? DateTimeOffset.UnixEpoch)
            .ThenByDescending(row => row.Id)
            .Skip(safeOffset)
            .Take(safeLimit)
            .ToArray();
        var total = await SumTableCountsAsync(connection, GameLogTables, cancellationToken);
        return new PageResult<GameLogRecord>(items, total, safeOffset, safeLimit);
    }

    private static async Task ReadFeedTableAsync(
        SqliteConnection connection,
        string table,
        int limit,
        Func<SqliteDataReader, FeedRecord> map,
        ICollection<FeedRecord> destination,
        CancellationToken cancellationToken,
        string columns)
    {
        if (!await TableExistsAsync(connection, table, cancellationToken))
        {
            return;
        }
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {columns} FROM {QuoteIdentifier(table)} ORDER BY created_at DESC, id DESC LIMIT @limit;";
        command.Parameters.AddWithValue("@limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            destination.Add(map(reader));
        }
    }

    private static async Task ReadGameLogTableAsync(
        SqliteConnection connection,
        string table,
        int limit,
        Func<SqliteDataReader, GameLogRecord> map,
        ICollection<GameLogRecord> destination,
        CancellationToken cancellationToken,
        string columns)
    {
        if (!await TableExistsAsync(connection, table, cancellationToken))
        {
            return;
        }
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {columns} FROM {QuoteIdentifier(table)} ORDER BY created_at DESC, id DESC LIMIT @limit;";
        command.Parameters.AddWithValue("@limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            destination.Add(map(reader));
        }
    }

    private static JsonElement ParseJsonArray(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return JsonSerializer.SerializeToElement(Array.Empty<string>());
        }
        var value = ParseJson(reader.GetString(ordinal));
        return value.ValueKind == JsonValueKind.Array
            ? value
            : JsonSerializer.SerializeToElement(Array.Empty<string>());
    }
}
