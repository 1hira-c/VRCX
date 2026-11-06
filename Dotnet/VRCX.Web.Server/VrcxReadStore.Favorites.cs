using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace VRCX.Web.Server;

public sealed partial class VrcxReadStore
{
    private static readonly HashSet<string> FavoriteTypes =
        new(["friend", "world", "vrcPlusWorld", "avatar"], StringComparer.Ordinal);

    public async Task<FavoriteGroupsResult> GetFavoriteGroupsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await TryOpenAsync(cancellationToken);
        if (connection is null)
        {
            return new FavoriteGroupsResult(false, []);
        }

        if (await IsFavoriteProjectionReadyAsync(connection, cancellationToken))
        {
            var groups = new List<FavoriteGroupRecord>();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT group_key, type, name, display_name, capacity, count, visibility, sort_order
                FROM vrcx_web_favorite_group_snapshot
                ORDER BY sort_order, group_key;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var type = reader.GetString(1);
                if (!FavoriteTypes.Contains(type))
                {
                    continue;
                }
                groups.Add(new FavoriteGroupRecord(
                    reader.GetString(0),
                    type,
                    reader.GetString(2),
                    reader.GetString(3),
                    Math.Max(0, reader.GetInt32(4)),
                    Math.Max(0, reader.GetInt32(5)),
                    reader.GetString(6),
                    Math.Max(0, reader.GetInt32(7))));
            }
            return new FavoriteGroupsResult(true, groups);
        }

        return new FavoriteGroupsResult(false, await ReadFallbackFavoriteGroupsAsync(connection, cancellationToken));
    }

    private static async Task<bool> IsFavoriteProjectionReadyAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "vrcx_web_favorite_projection_state", cancellationToken) ||
            !await TableExistsAsync(connection, "vrcx_web_favorite_snapshot", cancellationToken) ||
            !await TableExistsAsync(connection, "vrcx_web_favorite_group_snapshot", cancellationToken))
        {
            return false;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ready FROM vrcx_web_favorite_projection_state WHERE id = 1;";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is long ready && ready == 1;
    }

    private static async Task<FavoritePageResult> ReadFavoriteProjectionAsync(
        SqliteConnection connection,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        var items = new List<FavoriteRecord>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, type, favorite_id, tags_json, group_key, sort_order,
                   target_name, target_payload_json
            FROM vrcx_web_favorite_snapshot
            ORDER BY sort_order, id
            LIMIT @limit OFFSET @offset;
            """;
        command.Parameters.AddWithValue("@limit", limit);
        command.Parameters.AddWithValue("@offset", offset);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var type = reader.GetString(1);
            if (!FavoriteTypes.Contains(type))
            {
                continue;
            }
            items.Add(new FavoriteRecord(
                reader.GetString(0),
                type,
                reader.GetString(2),
                ParseFavoriteTags(reader.GetString(3)),
                reader.GetString(4),
                Math.Max(0, reader.GetInt32(5)),
                null,
                reader.GetString(6),
                ParseJson(reader.GetString(7))));
        }

        return new FavoritePageResult(
            items,
            await CountAsync(connection, "vrcx_web_favorite_snapshot", null, cancellationToken),
            offset,
            limit,
            true);
    }

    private static IReadOnlyList<string> ParseFavoriteTags(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }
            return document.RootElement
                .EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Take(32)
                .Select(item => (item.GetString() ?? string.Empty)[..Math.Min(item.GetString()?.Length ?? 0, 256)])
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static async Task<IReadOnlyList<FavoriteGroupRecord>> ReadFallbackFavoriteGroupsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var groups = new List<FavoriteGroupRecord>();
        var mappings = new[]
        {
            (Table: "favorite_friend", Type: "friend", Capacity: 150),
            (Table: "favorite_world", Type: "world", Capacity: 100),
            (Table: "favorite_avatar", Type: "avatar", Capacity: 50)
        };
        var sortOrder = 0;
        foreach (var mapping in mappings)
        {
            if (!await TableExistsAsync(connection, mapping.Table, cancellationToken))
            {
                continue;
            }
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT COALESCE(group_name, ''), COUNT(*), MIN(id)
                FROM {QuoteIdentifier(mapping.Table)}
                GROUP BY COALESCE(group_name, '')
                ORDER BY MIN(id);
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var name = reader.GetString(0);
                groups.Add(new FavoriteGroupRecord(
                    $"{mapping.Type}:{name}",
                    mapping.Type,
                    name,
                    name,
                    mapping.Capacity,
                    reader.GetInt32(1),
                    "private",
                    sortOrder++));
            }
        }
        return groups;
    }
}
