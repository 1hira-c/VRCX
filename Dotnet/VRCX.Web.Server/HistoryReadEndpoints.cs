namespace VRCX.Web.Server;

internal static class HistoryReadEndpoints
{
    internal static RouteGroupBuilder MapVrcxHistoryReadApi(this RouteGroupBuilder api)
    {
        var history = api.MapGroup("/history-native")
            .RequireRateLimiting(VrchatWorkerEndpoints.ReadRateLimitPolicy);

        history.MapGet("/feed", GetFeedAsync);
        history.MapGet("/game-log", GetGameLogAsync);
        history.MapGet("/game-log/sessions/locations", GetSessionLocationsAsync);
        history.MapGet("/game-log/sessions/anchor", GetSessionLocationsByAnchorAsync);
        history.MapGet("/game-log/sessions/events", GetSessionEventsAsync);
        history.MapGet("/memos/users", GetAllUserMemosAsync);
        history.MapGet("/memos/user/{id}", GetUserMemoAsync);
        history.MapGet("/memos/world/{id}", GetWorldMemoAsync);
        history.MapGet("/memos/avatar/{id}", GetAvatarMemoAsync);
        history.MapGet("/users/stats", GetAllUserStatsAsync);
        history.MapGet("/users/{id}/stats", GetUserStatsAsync);
        history.MapGet("/friends", GetFriendHistoryAsync);
        history.MapGet("/friends/{id}", GetFriendHistoryForUserAsync);

        return api;
    }

    private static async Task<IResult> GetFeedAsync(
        [AsParameters] NativeFeedQuery query,
        VrchatWorkerClient worker,
        CancellationToken cancellationToken)
    {
        if (!VrchatWorkerClient.IsValidHistoryLimit(query.MaxEntries) ||
            !VrchatWorkerClient.IsValidHistoryFilters(query.Filters, "feed", out var filters) ||
            query.Search is { Length: > 512 } || query.Search?.Contains('\0') == true ||
            !VrchatWorkerClient.IsValidHistoryDate(query.DateFrom, optional: true) ||
            !VrchatWorkerClient.IsValidHistoryDate(query.DateTo, optional: true) ||
            !VrchatWorkerClient.IsValidHistoryDateRange(query.DateFrom, query.DateTo))
        {
            return InvalidHistoryQuery();
        }

        var search = query.Search?.Trim() ?? "";
        var dateFrom = query.DateFrom ?? "";
        var dateTo = query.DateTo ?? "";
        IReadOnlyList<HistoryFeedRow>? result;
        if (search.Length > 0 || dateFrom.Length > 0 || dateTo.Length > 0)
        {
            result = await worker.SearchFeedHistoryAsync(
                search, filters, query.FavoritesOnly, query.MaxEntries, dateFrom, dateTo, cancellationToken);
        }
        else
        {
            result = await worker.LookupFeedHistoryAsync(
                filters, query.FavoritesOnly, query.MaxEntries, cancellationToken);
        }
        return result is null ? HistoryWorkerUnavailable() : Results.Ok(result);
    }

    private static async Task<IResult> GetGameLogAsync(
        [AsParameters] NativeGameLogQuery query,
        VrchatWorkerClient worker,
        CancellationToken cancellationToken)
    {
        if (!VrchatWorkerClient.IsValidHistoryLimit(query.MaxEntries) ||
            !VrchatWorkerClient.IsValidHistoryFilters(query.Filters, "game-log", out var filters) ||
            query.Search is { Length: > 512 } || query.Search?.Contains('\0') == true)
        {
            return InvalidHistoryQuery();
        }

        var search = query.Search?.Trim() ?? "";
        var result = search.Length > 0
            ? await worker.SearchGameLogHistoryAsync(
                search, filters, query.FavoritesOnly, query.MaxEntries, cancellationToken)
            : await worker.LookupGameLogHistoryAsync(
                filters, query.FavoritesOnly, query.MaxEntries, cancellationToken);
        return result is null ? HistoryWorkerUnavailable() : Results.Ok(result);
    }

    private static async Task<IResult> GetSessionLocationsAsync(
        [AsParameters] NativeSessionLocationsQuery query,
        VrchatWorkerClient worker,
        CancellationToken cancellationToken)
    {
        if (query.BeforeId is <= 0 || query.Limit is < 1 or > 10_000) return InvalidHistoryQuery();
        var result = await worker.GetSessionLocationsAsync(query.BeforeId, query.Limit, cancellationToken);
        return result is null ? HistoryWorkerUnavailable() : Results.Ok(result);
    }

    private static async Task<IResult> GetSessionLocationsByAnchorAsync(
        [AsParameters] NativeSessionAnchorQuery query,
        VrchatWorkerClient worker,
        CancellationToken cancellationToken)
    {
        if (!VrchatWorkerClient.IsValidHistoryDate(query.SinceDate, optional: false) ||
            query.Limit is < 1 or > 10_000)
        {
            return InvalidHistoryQuery();
        }
        var result = await worker.GetSessionLocationsByAnchorAsync(
            query.SinceDate!, query.Limit, cancellationToken);
        return result is null ? HistoryWorkerUnavailable() : Results.Ok(result);
    }

    private static async Task<IResult> GetSessionEventsAsync(
        HttpRequest request,
        VrchatWorkerClient worker,
        CancellationToken cancellationToken)
    {
        var locations = request.Query["location"].Select(value => value ?? "").ToArray();
        var afterDate = request.Query["afterDate"].ToString();
        var beforeDate = request.Query["beforeDate"].ToString();
        if (locations.Length is < 1 or > 1_000 ||
            locations.Distinct(StringComparer.Ordinal).Count() != locations.Length ||
            locations.Any(value => value.Length is < 1 or > 2_048 || value.Contains('\0')) ||
            !VrchatWorkerClient.IsValidHistoryDate(afterDate, optional: false) ||
            !VrchatWorkerClient.IsValidHistoryDate(beforeDate, optional: false) ||
            !VrchatWorkerClient.IsValidHistoryDateRange(afterDate, beforeDate))
        {
            return InvalidHistoryQuery();
        }
        var result = await worker.GetSessionEventsAsync(locations, afterDate, beforeDate, cancellationToken);
        return result is null ? HistoryWorkerUnavailable() : Results.Ok(result);
    }

    private static async Task<IResult> GetUserMemoAsync(
        string id,
        VrchatWorkerClient worker,
        CancellationToken cancellationToken)
    {
        if (!VrchatWorkerClient.IsValidEntityId("user", id)) return InvalidHistoryQuery();
        var result = await worker.GetUserMemoHistoryAsync(id, cancellationToken);
        return result is null ? HistoryWorkerUnavailable() : Results.Ok(result);
    }

    private static async Task<IResult> GetAllUserMemosAsync(
        VrchatWorkerClient worker,
        CancellationToken cancellationToken)
    {
        var result = await worker.GetAllUserMemosHistoryAsync(cancellationToken);
        return result is null ? HistoryWorkerUnavailable() : Results.Ok(result);
    }

    private static async Task<IResult> GetWorldMemoAsync(
        string id,
        VrchatWorkerClient worker,
        CancellationToken cancellationToken)
    {
        if (!VrchatWorkerClient.IsValidEntityId("world", id)) return InvalidHistoryQuery();
        var result = await worker.GetWorldMemoHistoryAsync(id, cancellationToken);
        return result is null ? HistoryWorkerUnavailable() : Results.Ok(result);
    }

    private static async Task<IResult> GetAvatarMemoAsync(
        string id,
        VrchatWorkerClient worker,
        CancellationToken cancellationToken)
    {
        if (!VrchatWorkerClient.IsValidEntityId("avatar", id)) return InvalidHistoryQuery();
        var result = await worker.GetAvatarMemoHistoryAsync(id, cancellationToken);
        return result is null ? HistoryWorkerUnavailable() : Results.Ok(result);
    }

    private static async Task<IResult> GetUserStatsAsync(
        string id,
        [AsParameters] NativeUserStatsQuery query,
        VrchatWorkerClient worker,
        CancellationToken cancellationToken)
    {
        if (!VrchatWorkerClient.IsValidEntityId("user", id) ||
            query.DisplayName is { Length: > 512 } || query.DisplayName?.Contains('\0') == true)
        {
            return InvalidHistoryQuery();
        }
        var result = await worker.GetUserStatsHistoryAsync(
            id, query.DisplayName ?? "", query.InCurrentWorld, cancellationToken);
        return result is null ? HistoryWorkerUnavailable() : Results.Ok(result);
    }

    private static async Task<IResult> GetAllUserStatsAsync(
        VrchatWorkerClient worker,
        CancellationToken cancellationToken)
    {
        var result = await worker.GetAllUserStatsHistoryAsync(cancellationToken);
        return result is null ? HistoryWorkerUnavailable() : Results.Ok(result);
    }

    private static async Task<IResult> GetFriendHistoryAsync(
        VrchatWorkerClient worker,
        CancellationToken cancellationToken)
    {
        var result = await worker.GetFriendLogHistoryAsync(cancellationToken);
        return result is null ? HistoryWorkerUnavailable() : Results.Ok(result);
    }

    private static async Task<IResult> GetFriendHistoryForUserAsync(
        string id,
        HttpRequest request,
        VrchatWorkerClient worker,
        CancellationToken cancellationToken)
    {
        var types = request.Query["type"].ToArray();
        if (!VrchatWorkerClient.IsValidEntityId("user", id) ||
            !VrchatWorkerClient.IsValidHistoryFilters(string.Join(',', types), "friend-history", out var validatedTypes))
        {
            return InvalidHistoryQuery();
        }
        var result = await worker.GetFriendLogHistoryForUserAsync(id, validatedTypes, cancellationToken);
        return result is null ? HistoryWorkerUnavailable() : Results.Ok(result);
    }

    private static IResult InvalidHistoryQuery() => Results.BadRequest(new { error = "invalid_history_query" });

    private static IResult HistoryWorkerUnavailable() => Results.Problem(
        statusCode: StatusCodes.Status503ServiceUnavailable,
        title: "VRCX history worker is unavailable.");
}
