using System.Text.Json;

namespace VRCX.Web.Server;

internal static class SseWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void Prepare(HttpResponse response)
    {
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache, no-store";
        response.Headers["X-Accel-Buffering"] = "no";
    }

    public static async Task WriteEventAsync<T>(
        HttpResponse response,
        string eventName,
        T payload,
        CancellationToken cancellationToken) =>
        await WriteEventAsync(response, eventName, payload, null, cancellationToken);

    public static async Task WriteEventAsync<T>(
        HttpResponse response,
        string eventName,
        T payload,
        long? eventId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(eventName) ||
            eventName.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException("SSE event names must contain only ASCII letters, digits, '-' or '_'.", nameof(eventName));
        }

        if (eventId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(eventId));
        }

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var idLine = eventId is null ? string.Empty : $"id: {eventId.Value}\n";
        await response.WriteAsync($"{idLine}event: {eventName}\ndata: {json}\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    public static async Task WriteHeartbeatAsync(
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        await response.WriteAsync(": heartbeat\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }
}
