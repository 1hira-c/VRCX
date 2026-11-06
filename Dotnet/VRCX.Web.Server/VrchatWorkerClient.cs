using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VRCX.Web.Server;

public sealed class VrcxWorkerOptions
{
    public string BaseAddress { get; set; } = "http://127.0.0.1:18081";

    internal Uri GetValidatedBaseAddress()
    {
        if (!Uri.TryCreate(BaseAddress, UriKind.Absolute, out var address) ||
            !string.Equals(address.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(address.UserInfo) ||
            !string.IsNullOrEmpty(address.Query) ||
            !string.IsNullOrEmpty(address.Fragment) ||
            !string.Equals(address.AbsolutePath, "/", StringComparison.Ordinal) ||
            address.Port is < 1 or > 65535 ||
            !IPAddress.TryParse(address.Host.Trim('[', ']'), out var ipAddress) ||
            !IPAddress.IsLoopback(ipAddress))
        {
            throw new InvalidOperationException(
                "VrcxWeb:Worker:BaseAddress must be an HTTP loopback origin with no path, query, fragment, or credentials.");
        }

        return address;
    }
}

internal static class VrchatWorkerServiceCollectionExtensions
{
    public static IServiceCollection AddVrchatWorkerClient(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var configured = configuration.GetSection("VrcxWeb:Worker").Get<VrcxWorkerOptions>() ?? new VrcxWorkerOptions();
        var baseAddress = configured.GetValidatedBaseAddress();

        services.AddHttpClient<VrchatWorkerClient>(client =>
        {
            client.BaseAddress = baseAddress;
            client.Timeout = TimeSpan.FromSeconds(35);
            client.MaxResponseContentBufferSize = VrchatWorkerClient.MaximumDatabaseReadResponseBytes;
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(3),
            MaxResponseHeadersLength = 16,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            UseCookies = false,
            UseProxy = false
        });

        return services;
    }
}

internal sealed partial class VrchatWorkerClient(HttpClient client)
{
    internal const int MaximumAuthResponseBytes = 64 * 1024;
    internal const int MaximumReadResponseBytes = 2 * 1024 * 1024;
    internal const int MaximumDatabaseReadResponseBytes = 64 * 1024 * 1024;
    private const int MaximumEntityIdLength = 256;
    private const int MaximumEntityNameLength = 512;
    private const int MaximumDescriptionLength = 16 * 1024;
    private const int MaximumPayloadDepth = 24;
    private const int MaximumPayloadNodes = 50_000;
    private const int MaximumPayloadPropertyNameLength = 128;
    private const int MaximumPayloadStringLength = 64 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 32,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public Task<VrchatWorkerAuthResult> GetStatusAsync(CancellationToken cancellationToken) =>
        SendAuthAsync(VrchatWorkerOperation.Status, null, cancellationToken);

    public Task<VrchatWorkerAuthResult> LoginAsync(
        VrchatLoginRequest request,
        CancellationToken cancellationToken) =>
        SendAuthAsync(VrchatWorkerOperation.Login, request, cancellationToken);

    public Task<VrchatWorkerAuthResult> SubmitTwoFactorAsync(
        VrchatTwoFactorRequest request,
        CancellationToken cancellationToken) =>
        SendAuthAsync(VrchatWorkerOperation.TwoFactor, request, cancellationToken);

    public Task<VrchatWorkerAuthResult> ResendEmailOtpAsync(CancellationToken cancellationToken) =>
        SendAuthAsync(VrchatWorkerOperation.ResendEmailOtp, null, cancellationToken);

    public Task<VrchatWorkerAuthResult> LogoutAsync(CancellationToken cancellationToken) =>
        SendAuthAsync(VrchatWorkerOperation.Logout, null, cancellationToken);

    public async Task<PageResult<SearchDocument>?> SearchAsync(
        string type,
        string query,
        int offset,
        int limit,
        VrchatWorkerSearchOptions options,
        CancellationToken cancellationToken)
    {
        if (!IsValidSearchRequest(type, query, options) ||
            offset is < 0 or > 10_000 ||
            limit is < 1 or > 200)
        {
            return null;
        }

        var trimmedQuery = query.Trim();
        var response = await SendJsonAsync<VrchatWorkerSearchResponse>(
            VrchatWorkerOperation.Search,
            new VrchatWorkerSearchRequest(type, trimmedQuery, offset, limit, options),
            MaximumReadResponseBytes,
            cancellationToken);
        if (response is null ||
            !response.Ok ||
            response.ErrorCode is not null ||
            response.Items is null ||
            response.Offset != offset ||
            response.Limit != limit ||
            response.Total < response.Items.Count ||
            response.Items.Count > limit)
        {
            return null;
        }

        var items = new List<SearchDocument>(response.Items.Count);
        foreach (var item in response.Items)
        {
            var document = ToSearchDocument(item, type, expectedId: null);
            if (document is null)
            {
                return null;
            }
            items.Add(document);
        }

        return new PageResult<SearchDocument>(items, response.Total, response.Offset, response.Limit);
    }

    public async Task<SearchDocument?> GetEntityAsync(
        string type,
        string id,
        CancellationToken cancellationToken)
    {
        if (!IsValidEntityId(type, id))
        {
            return null;
        }

        var response = await SendJsonAsync<VrchatWorkerEntityResponse>(
            VrchatWorkerOperation.Entity,
            new VrchatWorkerEntityRequest(type, id),
            MaximumReadResponseBytes,
            cancellationToken);
        if (response is null || !response.Ok || response.ErrorCode is not null || response.Item is null)
        {
            return null;
        }

        return ToSearchDocument(response.Item, type, id);
    }

    public async Task<IReadOnlyList<string>?> GetDateOfInstanceActivityAsync(
        CancellationToken cancellationToken)
    {
        var result = await SendDatabaseReadAsync<IReadOnlyList<string>>(
            "getDateOfInstanceActivity",
            new { },
            cancellationToken);
        return result is { Count: <= 100_000 } &&
            result.All(value => IsBoundedText(value, 128, allowEmpty: false))
                ? result
                : null;
    }

    public async Task<InstanceActivityResult?> GetInstanceActivityAsync(
        string startDate,
        string endDate,
        CancellationToken cancellationToken)
    {
        if (!IsValidInstanceActivityRange(startDate, endDate))
        {
            return null;
        }

        var result = await SendDatabaseReadAsync<InstanceActivityResult>(
            "getInstanceActivity",
            new { startDate, endDate },
            cancellationToken);
        return result is not null && IsValidInstanceActivity(result) ? result : null;
    }

    public async Task<IReadOnlyList<HotWorldResult>?> GetHotWorldsAsync(
        int days,
        int limit,
        CancellationToken cancellationToken)
    {
        if (!IsValidAnalyticsDays(days) || limit is < 1 or > 100)
        {
            return null;
        }

        var result = await SendDatabaseReadAsync<IReadOnlyList<HotWorldResult>>(
            "getHotWorlds",
            new { days, limit },
            cancellationToken);
        return result is not null && result.Count <= limit && result.All(IsValidHotWorld)
            ? result
            : null;
    }

    public async Task<IReadOnlyList<HotWorldFriendResult>?> GetHotWorldFriendDetailAsync(
        string worldId,
        int days,
        CancellationToken cancellationToken)
    {
        if (!IsValidEntityId("world", worldId) || !IsValidAnalyticsDays(days))
        {
            return null;
        }

        var result = await SendDatabaseReadAsync<IReadOnlyList<HotWorldFriendResult>>(
            "getHotWorldFriendDetail",
            new { worldId, days },
            cancellationToken);
        return result is { Count: <= 50_000 } && result.All(IsValidHotWorldFriend)
            ? result
            : null;
    }

    public async Task<IReadOnlyList<MutualGraphSnapshotEntry>?> GetMutualGraphSnapshotAsync(
        CancellationToken cancellationToken)
    {
        var result = await SendDatabaseReadAsync<IReadOnlyList<MutualGraphSnapshotEntry>>(
            "getMutualGraphSnapshot",
            new { },
            cancellationToken);
        return result is not null && IsValidMutualGraphSnapshot(result) ? result : null;
    }

    public async Task<IReadOnlyList<MutualGraphMetaEntry>?> GetMutualGraphMetaAsync(
        CancellationToken cancellationToken)
    {
        var result = await SendDatabaseReadAsync<IReadOnlyList<MutualGraphMetaEntry>>(
            "getMutualGraphMeta",
            new { },
            cancellationToken);
        return result is not null && IsValidMutualGraphMeta(result) ? result : null;
    }

    private async Task<T?> SendDatabaseReadAsync<T>(
        string name,
        object arguments,
        CancellationToken cancellationToken)
        where T : class
    {
        var response = await SendJsonAsync<VrchatWorkerDatabaseReadResponse<T>>(
            VrchatWorkerOperation.DatabaseRead,
            new VrchatWorkerDatabaseReadRequest(name, arguments),
            MaximumDatabaseReadResponseBytes,
            cancellationToken);
        return response is { Ok: true, ErrorCode: null, Result: not null }
            ? response.Result
            : null;
    }

    private async Task<VrchatWorkerAuthResult> SendAuthAsync(
        VrchatWorkerOperation operation,
        object? payload,
        CancellationToken cancellationToken)
    {
        var result = await SendJsonResultAsync<VrchatWorkerResponse>(
            operation,
            payload,
            MaximumAuthResponseBytes,
            cancellationToken);
        return result.Response is not null && IsValid(result.Response)
            ? new VrchatWorkerAuthResult(ToPublicResponse(result.Response))
            : new VrchatWorkerAuthResult(null, result.Failure ?? VrchatWorkerAuthFailure.BadResponse, result.Reason);
    }

    private sealed record WorkerJsonResult<T>(
        T? Response,
        VrchatWorkerAuthFailure? Failure = null,
        string? Reason = null) where T : class;

    private async Task<TResponse?> SendJsonAsync<TResponse>(
        VrchatWorkerOperation operation,
        object? payload,
        int maximumResponseBytes,
        CancellationToken cancellationToken)
        where TResponse : class
    {
        var result = await SendJsonResultAsync<TResponse>(operation, payload, maximumResponseBytes, cancellationToken);
        return result.Response;
    }

    private async Task<WorkerJsonResult<TResponse>> SendJsonResultAsync<TResponse>(
        VrchatWorkerOperation operation,
        object? payload,
        int maximumResponseBytes,
        CancellationToken cancellationToken)
        where TResponse : class
    {
        var (method, path) = GetRequest(operation);
        using var request = new HttpRequestMessage(method, path);
        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload, payload.GetType(), options: JsonOptions);
        }

        try
        {
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (response.Headers.Contains("Set-Cookie"))
            {
                return new(null, VrchatWorkerAuthFailure.BadResponse);
            }
            if (!response.IsSuccessStatusCode)
            {
                var failure = await ReadControlFailureAsync(response, cancellationToken);
                return new(null, failure.Failure, failure.Reason);
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
            {
                return new(null, VrchatWorkerAuthFailure.BadResponse);
            }

            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength.HasValue && contentLength.Value > maximumResponseBytes)
            {
                return new(null, VrchatWorkerAuthFailure.BadResponse);
            }

            await response.Content.LoadIntoBufferAsync(maximumResponseBytes, cancellationToken);
            var body = await response.Content.ReadFromJsonAsync<TResponse>(
                JsonOptions,
                cancellationToken);
            return new(body, body is null ? VrchatWorkerAuthFailure.BadResponse : null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(null, VrchatWorkerAuthFailure.Timeout);
        }
        catch (HttpRequestException exception) when (
            exception.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused })
        {
            return new(null, VrchatWorkerAuthFailure.Unavailable);
        }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return new(null, VrchatWorkerAuthFailure.Unavailable);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or SocketException)
        {
            return new(null, VrchatWorkerAuthFailure.BadResponse);
        }
    }

    private static async Task<VrchatWorkerAuthResult> ReadControlFailureAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        // A control timeout or malformed response is not evidence that the
        // renderer is unavailable. Only validated fixed failures get a 503.
        if (response.StatusCode is not (HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout) ||
            !string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
            response.Content.Headers.ContentLength is > MaximumAuthResponseBytes)
        {
            return new(null, VrchatWorkerAuthFailure.BadResponse);
        }

        await response.Content.LoadIntoBufferAsync(MaximumAuthResponseBytes, cancellationToken);
        var failure = await response.Content.ReadFromJsonAsync<VrchatWorkerResponse>(JsonOptions, cancellationToken);
        if (failure is null || failure.Ok || !IsValid(failure))
        {
            return new(null, VrchatWorkerAuthFailure.BadResponse);
        }

        return (response.StatusCode, failure.ErrorCode) switch
        {
            (HttpStatusCode.ServiceUnavailable, "renderer_unavailable") => new(null, VrchatWorkerAuthFailure.Unavailable),
            (HttpStatusCode.ServiceUnavailable, "database_initialization_failed") =>
                new(null, VrchatWorkerAuthFailure.Unavailable, "database_initialization_failed"),
            (HttpStatusCode.GatewayTimeout, "renderer_timeout") => new(null, VrchatWorkerAuthFailure.Timeout),
            _ => new(null, VrchatWorkerAuthFailure.BadResponse)
        };
    }

    private static (HttpMethod Method, string Path) GetRequest(VrchatWorkerOperation operation) => operation switch
    {
        VrchatWorkerOperation.Status => (HttpMethod.Get, "/control/v1/status"),
        VrchatWorkerOperation.Login => (HttpMethod.Post, "/control/v1/login"),
        VrchatWorkerOperation.TwoFactor => (HttpMethod.Post, "/control/v1/two-factor"),
        VrchatWorkerOperation.ResendEmailOtp => (HttpMethod.Post, "/control/v1/email/resend"),
        VrchatWorkerOperation.Logout => (HttpMethod.Post, "/control/v1/logout"),
        VrchatWorkerOperation.Search => (HttpMethod.Post, "/control/v1/search"),
        VrchatWorkerOperation.Entity => (HttpMethod.Post, "/control/v1/entity"),
        VrchatWorkerOperation.EntityResource => (HttpMethod.Post, "/control/v1/entity-resource"),
        VrchatWorkerOperation.DatabaseRead => (HttpMethod.Post, "/control/v1/database-read"),
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
    };

    private static bool IsValid(VrchatWorkerResponse response)
    {
        if ((response.Ok ? response.ErrorCode is not null : string.IsNullOrEmpty(response.ErrorCode)) ||
            response.ErrorCode is { Length: > 64 } ||
            response.ErrorCode is not null && !response.ErrorCode.All(character =>
                character is >= 'a' and <= 'z' || char.IsAsciiDigit(character) || character == '_'))
        {
            return false;
        }

        return response.Status switch
        {
            VrchatAuthState.Authenticated => response.Challenge is null && IsValidUserId(response.CurrentUserId),
            VrchatAuthState.TwoFactorRequired => response.CurrentUserId is null && IsValid(response.Challenge),
            _ => response.Challenge is null && response.CurrentUserId is null
        };
    }

    private static bool IsValidUserId(string? userId) =>
        userId is { Length: > 0 and <= 128 } &&
        userId.StartsWith("usr_", StringComparison.Ordinal) &&
        userId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static bool IsValid(VrchatAuthChallenge? challenge) =>
        challenge is not null &&
        challenge.Kinds is { Count: > 0 and <= 3 } &&
        challenge.Kinds.Distinct().Count() == challenge.Kinds.Count;

    private static SearchDocument? ToSearchDocument(
        VrchatWorkerEntityDocument document,
        string expectedType,
        string? expectedId)
    {
        if (!string.Equals(document.Type, expectedType, StringComparison.Ordinal) ||
            !IsValidEntityId(expectedType, document.Id) ||
            expectedId is not null && !string.Equals(document.Id, expectedId, StringComparison.Ordinal) ||
            !IsBoundedText(document.Name, MaximumEntityNameLength, allowEmpty: false) ||
            document.OwnerId is not null && !IsBoundedText(document.OwnerId, MaximumEntityIdLength, allowEmpty: false) ||
            document.Description is not null && !IsBoundedText(document.Description, MaximumDescriptionLength, allowEmpty: true) ||
            !TryParseUpdatedAt(document.UpdatedAt, out var updatedAt) ||
            !IsValidPayload(document.Payload))
        {
            return null;
        }

        return new SearchDocument(
            expectedType,
            document.Id!,
            document.Name!,
            document.OwnerId,
            document.Description,
            updatedAt,
            document.Payload.Clone());
    }

    private static bool TryParseUpdatedAt(string? value, out DateTimeOffset? updatedAt)
    {
        if (value is null)
        {
            updatedAt = null;
            return true;
        }

        if (!IsBoundedText(value, 128, allowEmpty: false) ||
            !DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            updatedAt = null;
            return false;
        }

        updatedAt = parsed;
        return true;
    }

    private static bool IsValidPayload(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var nodes = 0;
        return IsValidJsonValue(payload, 0, ref nodes);
    }

    private static bool IsValidJsonValue(JsonElement value, int depth, ref int nodes)
    {
        if (depth > MaximumPayloadDepth || ++nodes > MaximumPayloadNodes)
        {
            return false;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    if (property.Name.Length is < 1 or > MaximumPayloadPropertyNameLength ||
                        property.Name.Contains('\0') ||
                        IsSensitivePayloadKey(property.Name) ||
                        !IsValidJsonValue(property.Value, depth + 1, ref nodes))
                    {
                        return false;
                    }
                }
                return true;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    if (!IsValidJsonValue(item, depth + 1, ref nodes))
                    {
                        return false;
                    }
                }
                return true;
            case JsonValueKind.String:
                return value.GetString() is { Length: <= MaximumPayloadStringLength } text &&
                    !text.Contains('\0');
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                return true;
            default:
                return false;
        }
    }

    private static bool IsSensitivePayloadKey(string key)
    {
        var normalized = new string(key
            .Where(character => char.IsAsciiLetterOrDigit(character))
            .Select(char.ToLowerInvariant)
            .ToArray());
        return normalized is "auth" or "authorization" or "authentication" or "headers" or "loginparams" or
                "proxy" or "proxyserver" or "token" ||
            normalized.Contains("friendkey", StringComparison.Ordinal) ||
            normalized.Contains("password", StringComparison.Ordinal) ||
            normalized.Contains("passwd", StringComparison.Ordinal) ||
            normalized.Contains("cookie", StringComparison.Ordinal) ||
            normalized.Contains("credential", StringComparison.Ordinal) ||
            normalized.Contains("secret", StringComparison.Ordinal) ||
            normalized.Contains("apikey", StringComparison.Ordinal) ||
            normalized.Contains("privatekey", StringComparison.Ordinal) ||
            normalized.Contains("sessionkey", StringComparison.Ordinal) ||
            normalized.EndsWith("token", StringComparison.Ordinal);
    }

    internal static bool IsValidEntityId(string type, string? id)
    {
        var prefix = type switch
        {
            "user" => "usr_",
            "world" => "wrld_",
            "avatar" => "avtr_",
            "group" => "grp_",
            _ => null
        };
        return prefix is not null &&
            id is { Length: > 0 and <= MaximumEntityIdLength } &&
            id.StartsWith(prefix, StringComparison.Ordinal) &&
            id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    }

    private static bool IsEntityType(string type) =>
        type is "user" or "world" or "avatar" or "group";

    internal static bool IsValidSearchRequest(
        string type,
        string? query,
        VrchatWorkerSearchOptions options) =>
        IsEntityType(type) &&
        query is { Length: <= 128 } &&
        !query.Contains('\0') &&
        IsValidSearchOptions(type, options) &&
        (!string.IsNullOrWhiteSpace(query) || type == "world" && HasWorldSearchOptions(options));

    private static bool IsValidSearchOptions(string type, VrchatWorkerSearchOptions options)
    {
        if (options is null)
        {
            return false;
        }

        var validOrder = options.Order is null or "ascending" or "descending";
        return type switch
        {
            "world" =>
                options.WorldOption is null or "active" or "recent" or "favorites" &&
                options.Sort is null or "order" or "popularity" or "updated" or "created" or
                    "publicationDate" or "shuffle" or "relevance" or "labsPublicationDate" or "heat" &&
                validOrder &&
                options.User is null or "me" &&
                options.ReleaseStatus is null or "all" &&
                IsValidWorldTag(options.Tag) &&
                options.Fuzzy is null &&
                options.CustomFields is null,
            "user" =>
                options.WorldOption is null &&
                options.Sort is null or "relevance" or "last_login" &&
                validOrder &&
                options.Featured is null &&
                options.Tag is null &&
                options.User is null &&
                options.ReleaseStatus is null &&
                options.CustomFields is null or "displayName",
            _ =>
                options.WorldOption is null &&
                options.Sort is null &&
                options.Order is null &&
                options.Featured is null &&
                options.Tag is null &&
                options.User is null &&
                options.ReleaseStatus is null &&
                options.Fuzzy is null &&
                options.CustomFields is null
        };
    }

    private static bool HasWorldSearchOptions(VrchatWorkerSearchOptions options) =>
        options.WorldOption is not null ||
        options.Sort is not null ||
        options.Order is not null ||
        options.Featured is not null ||
        options.Tag is not null ||
        options.User is not null ||
        options.ReleaseStatus is not null;

    private static bool IsValidWorldTag(string? value) =>
        value is null ||
        value.Length is > 0 and <= 256 &&
        value.Split(',').All(part =>
            part.Length is > 0 and <= 128 &&
            part.All(character => !char.IsControl(character)));

    internal static bool IsValidInstanceActivityRange(string? startDate, string? endDate)
    {
        if (!TryParseAnalyticsDate(startDate, out var start) || !TryParseAnalyticsDate(endDate, out var end))
        {
            return false;
        }
        return start <= end && end - start <= TimeSpan.FromDays(32);
    }

    internal static bool IsValidAnalyticsDays(int days) => days is >= 1 and <= 365;

    private static bool TryParseAnalyticsDate(string? value, out DateTimeOffset result)
    {
        result = default;
        return IsBoundedText(value, 128, allowEmpty: false) &&
            DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out result);
    }

    private static bool IsValidInstanceActivity(InstanceActivityResult result)
    {
        if (result.CurrentUserData is not { Count: <= 100_000 } ||
            result.DetailData is not { Count: <= 20_000 } ||
            !result.CurrentUserData.All(IsValidInstanceActivityRow))
        {
            return false;
        }

        long rowCount = result.CurrentUserData.Count;
        var locations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var detail in result.DetailData)
        {
            if (!IsBoundedText(detail.Location, 2048, allowEmpty: false) ||
                !locations.Add(detail.Location!) ||
                detail.Rows is null ||
                !detail.Rows.All(IsValidInstanceActivityRow))
            {
                return false;
            }
            rowCount += detail.Rows.Count;
            if (rowCount > 200_000)
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsValidInstanceActivityRow(InstanceActivityRow row) =>
        row.Id >= 0 &&
        IsBoundedText(row.CreatedAt, 128, allowEmpty: false) &&
        IsBoundedText(row.Type, 64, allowEmpty: false) &&
        IsBoundedText(row.DisplayName, 512, allowEmpty: true) &&
        IsBoundedText(row.Location, 2048, allowEmpty: false) &&
        IsBoundedText(row.UserId, 256, allowEmpty: true) &&
        double.IsFinite(row.Time) &&
        Math.Abs(row.Time) <= TimeSpan.FromDays(366).TotalMilliseconds;

    private static bool IsValidHotWorld(HotWorldResult world) =>
        IsValidEntityId("world", world.WorldId) &&
        IsBoundedText(world.WorldName, 512, allowEmpty: false) &&
        world.VisitCount >= 0 &&
        world.UniqueFriends >= 0 &&
        IsBoundedText(world.LastVisited, 128, allowEmpty: false) &&
        world.Trend is "rising" or "cooling" or "stable";

    private static bool IsValidHotWorldFriend(HotWorldFriendResult friend) =>
        IsValidEntityId("user", friend.UserId) &&
        IsBoundedText(friend.DisplayName, 512, allowEmpty: true) &&
        friend.VisitCount >= 0 &&
        IsBoundedText(friend.LastVisit, 128, allowEmpty: false);

    private static bool IsValidMutualGraphSnapshot(IReadOnlyList<MutualGraphSnapshotEntry> entries)
    {
        if (entries.Count > 50_000) return false;
        long edgeCount = 0;
        var friendIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (!IsValidEntityId("user", entry.FriendId) ||
                !friendIds.Add(entry.FriendId!) ||
                entry.MutualIds is null ||
                entry.MutualIds.Any(id => !IsValidEntityId("user", id)))
            {
                return false;
            }
            edgeCount += entry.MutualIds.Count;
            if (edgeCount > 250_000) return false;
        }
        return true;
    }

    private static bool IsValidMutualGraphMeta(IReadOnlyList<MutualGraphMetaEntry> entries)
    {
        if (entries.Count > 50_000) return false;
        var friendIds = new HashSet<string>(StringComparer.Ordinal);
        return entries.All(entry =>
            IsValidEntityId("user", entry.FriendId) &&
            friendIds.Add(entry.FriendId!) &&
            (entry.LastFetchedAt is null || IsBoundedText(entry.LastFetchedAt, 128, allowEmpty: false)));
    }

    private static bool IsBoundedText(string? value, int maximumLength, bool allowEmpty) =>
        value is not null &&
        value.Length <= maximumLength &&
        (allowEmpty || value.Length > 0) &&
        !value.Contains('\0');

    private static VrchatAuthStatusResponse ToPublicResponse(VrchatWorkerResponse response) => new(
        response.Ok,
        response.Status,
        response.Challenge,
        response.CurrentUserId is null ? null : new VrchatCurrentUser(response.CurrentUserId),
        response.ErrorCode);

    private enum VrchatWorkerOperation
    {
        Status,
        Login,
        TwoFactor,
        ResendEmailOtp,
        Logout,
        Search,
        Entity,
        EntityResource,
        DatabaseRead
    }
}
