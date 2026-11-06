using System.Text.Json;

namespace VRCX.Web.Server;

public sealed partial class VrcxReadStore
{
    public async Task<long> GetLatestEventSequenceAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await TryOpenAsync(cancellationToken);
        if (connection is null ||
            !await TableExistsAsync(connection, "vrcx_web_event_outbox", cancellationToken))
        {
            return 0;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(sequence), 0) FROM vrcx_web_event_outbox;";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<IReadOnlyList<ProjectionEvent>> GetEventsAfterAsync(
        long sequence,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await TryOpenAsync(cancellationToken);
        if (connection is null ||
            !await TableExistsAsync(connection, "vrcx_web_event_outbox", cancellationToken))
        {
            return [];
        }

        var events = new List<ProjectionEvent>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence, occurred_at, type, resource, payload_json
            FROM vrcx_web_event_outbox
            WHERE sequence > @sequence
            ORDER BY sequence
            LIMIT @limit;
            """;
        command.Parameters.AddWithValue("@sequence", Math.Max(0, sequence));
        command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 200));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var type = reader.GetString(2);
            if (type is not ("status" or "pipeline" or "data"))
            {
                continue;
            }

            events.Add(new ProjectionEvent(
                reader.GetInt64(0),
                ReadNullableDate(reader, 1),
                type,
                ReadNullableString(reader, 3),
                ParseJson(reader.GetString(4))));
        }

        return events;
    }
}

public sealed record ProjectionEvent(
    long Sequence,
    DateTimeOffset? OccurredAt,
    string Type,
    string? Resource,
    JsonElement Payload);
