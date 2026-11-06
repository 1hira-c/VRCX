using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace VRCX.Web.Server;

public sealed partial class VrcxReadStore
{
    private static readonly HashSet<string> SearchTypes = new(StringComparer.Ordinal)
    {
        "user",
        "world",
        "avatar",
        "group"
    };

    public async Task<PageResult<SearchDocument>> SearchAsync(
        string type,
        string query,
        int offset,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var (safeOffset, safeLimit) = NormalizePage(offset, limit);
        if (!SearchTypes.Contains(type) || string.IsNullOrWhiteSpace(query) || query.Length > 128)
        {
            return EmptyPage<SearchDocument>(safeOffset, safeLimit);
        }

        await using var connection = await TryOpenAsync(cancellationToken);
        if (connection is null)
        {
            return EmptyPage<SearchDocument>(safeOffset, safeLimit);
        }

        if (await TableExistsAsync(connection, "vrcx_web_entity_snapshot", cancellationToken))
        {
            var projected = await SearchProjectionAsync(
                connection,
                type,
                query,
                safeOffset,
                safeLimit,
                cancellationToken);
            if (projected.Total > 0 || type == "group")
            {
                return projected;
            }
        }

        return type switch
        {
            "user" => await SearchUsersAsync(connection, query, safeOffset, safeLimit, cancellationToken),
            "world" => await SearchCacheAsync(connection, "world", "cache_world", query, safeOffset, safeLimit, cancellationToken),
            "avatar" => await SearchCacheAsync(connection, "avatar", "cache_avatar", query, safeOffset, safeLimit, cancellationToken),
            _ => EmptyPage<SearchDocument>(safeOffset, safeLimit)
        };
    }

    public async Task<SearchDocument?> GetEntityAsync(
        string type,
        string id,
        CancellationToken cancellationToken = default)
    {
        if (!SearchTypes.Contains(type) || !IsSafeEntityId(id))
        {
            return null;
        }

        await using var connection = await TryOpenAsync(cancellationToken);
        if (connection is null)
        {
            return null;
        }

        if (await TableExistsAsync(connection, "vrcx_web_entity_snapshot", cancellationToken))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT entity_type, entity_id, display_name, owner_id, description, updated_at, payload_json
                FROM vrcx_web_entity_snapshot WHERE entity_type = @type AND entity_id = @id;
                """;
            command.Parameters.AddWithValue("@type", type);
            command.Parameters.AddWithValue("@id", id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                return ReadSearchDocument(reader);
            }
        }

        if (type == "user")
        {
            return await GetUserEntityAsync(connection, id, cancellationToken);
        }
        if (type is "world" or "avatar")
        {
            return await GetCacheEntityAsync(connection, type, $"cache_{type}", id, cancellationToken);
        }
        return null;
    }

    private static async Task<PageResult<SearchDocument>> SearchProjectionAsync(
        SqliteConnection connection,
        string type,
        string query,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        var items = new List<SearchDocument>();
        var pattern = $"%{EscapeLike(query)}%";
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT entity_type, entity_id, display_name, owner_id, description, updated_at, payload_json
                FROM vrcx_web_entity_snapshot
                WHERE entity_type = @type AND
                      (display_name LIKE @pattern ESCAPE '\' OR description LIKE @pattern ESCAPE '\')
                ORDER BY display_name COLLATE NOCASE, entity_id LIMIT @limit OFFSET @offset;
                """;
            command.Parameters.AddWithValue("@type", type);
            command.Parameters.AddWithValue("@pattern", pattern);
            command.Parameters.AddWithValue("@limit", limit);
            command.Parameters.AddWithValue("@offset", offset);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(ReadSearchDocument(reader));
            }
        }

        await using var count = connection.CreateCommand();
        count.CommandText = """
            SELECT COUNT(*) FROM vrcx_web_entity_snapshot
            WHERE entity_type = @type AND
                  (display_name LIKE @pattern ESCAPE '\' OR description LIKE @pattern ESCAPE '\');
            """;
        count.Parameters.AddWithValue("@type", type);
        count.Parameters.AddWithValue("@pattern", pattern);
        var total = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken));
        return new PageResult<SearchDocument>(items, total, offset, limit);
    }

    private static async Task<PageResult<SearchDocument>> SearchUsersAsync(
        SqliteConnection connection,
        string query,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "vrcx_web_user_snapshot", cancellationToken))
        {
            return EmptyPage<SearchDocument>(offset, limit);
        }
        var items = new List<SearchDocument>();
        var pattern = $"%{EscapeLike(query)}%";
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT user_id, display_name, updated_at, payload_json
                FROM vrcx_web_user_snapshot
                WHERE display_name LIKE @pattern ESCAPE '\'
                ORDER BY display_name COLLATE NOCASE, user_id LIMIT @limit OFFSET @offset;
                """;
            command.Parameters.AddWithValue("@pattern", pattern);
            command.Parameters.AddWithValue("@limit", limit);
            command.Parameters.AddWithValue("@offset", offset);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var payload = ParseJson(reader.GetString(3));
                items.Add(new SearchDocument(
                    "user",
                    reader.GetString(0),
                    reader.GetString(1),
                    null,
                    JsonString(payload, "bio"),
                    ReadNullableDate(reader, 2),
                    payload));
            }
        }

        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM vrcx_web_user_snapshot WHERE display_name LIKE @pattern ESCAPE '\';";
        count.Parameters.AddWithValue("@pattern", pattern);
        var total = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken));
        return new PageResult<SearchDocument>(items, total, offset, limit);
    }

    private static async Task<PageResult<SearchDocument>> SearchCacheAsync(
        SqliteConnection connection,
        string type,
        string table,
        string query,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, table, cancellationToken))
        {
            return EmptyPage<SearchDocument>(offset, limit);
        }
        var items = new List<SearchDocument>();
        var pattern = $"%{EscapeLike(query)}%";
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT id, author_id, author_name, created_at, description, image_url, name,
                       release_status, thumbnail_image_url, updated_at, version
                FROM {QuoteIdentifier(table)}
                WHERE name LIKE @pattern ESCAPE '\' OR author_name LIKE @pattern ESCAPE '\'
                      OR description LIKE @pattern ESCAPE '\'
                ORDER BY name COLLATE NOCASE, id LIMIT @limit OFFSET @offset;
                """;
            command.Parameters.AddWithValue("@pattern", pattern);
            command.Parameters.AddWithValue("@limit", limit);
            command.Parameters.AddWithValue("@offset", offset);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(ReadCacheDocument(reader, type));
            }
        }

        await using var count = connection.CreateCommand();
        count.CommandText = $"""
            SELECT COUNT(*) FROM {QuoteIdentifier(table)}
            WHERE name LIKE @pattern ESCAPE '\' OR author_name LIKE @pattern ESCAPE '\'
                  OR description LIKE @pattern ESCAPE '\';
            """;
        count.Parameters.AddWithValue("@pattern", pattern);
        var total = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken));
        return new PageResult<SearchDocument>(items, total, offset, limit);
    }

    private static async Task<SearchDocument?> GetUserEntityAsync(
        SqliteConnection connection,
        string id,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "vrcx_web_user_snapshot", cancellationToken))
        {
            return null;
        }
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT user_id, display_name, updated_at, payload_json
            FROM vrcx_web_user_snapshot WHERE user_id = @id;
            """;
        command.Parameters.AddWithValue("@id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        var payload = ParseJson(reader.GetString(3));
        return new SearchDocument(
            "user",
            reader.GetString(0),
            reader.GetString(1),
            null,
            JsonString(payload, "bio"),
            ReadNullableDate(reader, 2),
            payload);
    }

    private static async Task<SearchDocument?> GetCacheEntityAsync(
        SqliteConnection connection,
        string type,
        string table,
        string id,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, table, cancellationToken))
        {
            return null;
        }
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, author_id, author_name, created_at, description, image_url, name,
                   release_status, thumbnail_image_url, updated_at, version
            FROM {QuoteIdentifier(table)} WHERE id = @id;
            """;
        command.Parameters.AddWithValue("@id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadCacheDocument(reader, type) : null;
    }

    private static SearchDocument ReadSearchDocument(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        ReadNullableString(reader, 3),
        ReadNullableString(reader, 4),
        ReadNullableDate(reader, 5),
        ParseJson(reader.GetString(6)));

    private static SearchDocument ReadCacheDocument(SqliteDataReader reader, string type)
    {
        var id = reader.GetString(0);
        var authorId = ReadNullableString(reader, 1);
        var authorName = ReadNullableString(reader, 2);
        var description = ReadNullableString(reader, 4);
        var name = ReadNullableString(reader, 6) ?? id;
        var payload = JsonSerializer.SerializeToElement(new
        {
            id,
            authorId,
            authorName,
            created_at = ReadNullableString(reader, 3),
            description,
            imageUrl = ReadNullableString(reader, 5),
            name,
            releaseStatus = ReadNullableString(reader, 7),
            thumbnailImageUrl = ReadNullableString(reader, 8),
            updated_at = ReadNullableString(reader, 9),
            version = reader.IsDBNull(10) ? (long?)null : reader.GetInt64(10)
        });
        return new SearchDocument(type, id, name, authorId, description, ReadNullableDate(reader, 9), payload);
    }

    private static string EscapeLike(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);
}
