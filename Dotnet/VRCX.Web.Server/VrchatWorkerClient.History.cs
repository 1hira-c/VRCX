using System.Globalization;

namespace VRCX.Web.Server;

internal sealed partial class VrchatWorkerClient
{
    private static readonly HashSet<string> FeedHistoryFilters =
        ["GPS", "Status", "Bio", "Avatar", "Online", "Offline"];

    private static readonly HashSet<string> GameLogHistoryFilters =
    [
        "Location", "OnPlayerJoined", "OnPlayerLeft", "PortalSpawn", "Event", "External", "VideoPlay",
        "StringLoad", "ImageLoad"
    ];

    private static readonly HashSet<string> FriendHistoryTypes =
        ["Friend", "Unfriend", "DisplayName", "TrustLevel"];

    internal async Task<IReadOnlyList<HistoryFeedRow>?> LookupFeedHistoryAsync(
        IReadOnlyList<string> filters,
        bool favoritesOnly,
        int maxEntries,
        CancellationToken cancellationToken)
    {
        if (!IsValidFilters(filters, FeedHistoryFilters) || !IsValidHistoryLimit(maxEntries)) return null;
        var result = await SendDatabaseReadAsync<IReadOnlyList<HistoryFeedRow>>(
            "lookupFeedDatabase",
            new { filters, favoritesOnly, maxEntries },
            cancellationToken);
        return IsValidFeedRows(result, maxEntries) ? result : null;
    }

    internal async Task<IReadOnlyList<HistoryFeedRow>?> SearchFeedHistoryAsync(
        string search,
        IReadOnlyList<string> filters,
        bool favoritesOnly,
        int maxEntries,
        string dateFrom,
        string dateTo,
        CancellationToken cancellationToken)
    {
        if (!IsBoundedText(search, 512, allowEmpty: true) ||
            !IsValidFilters(filters, FeedHistoryFilters) ||
            !IsValidHistoryLimit(maxEntries) ||
            !IsValidOptionalHistoryDate(dateFrom) ||
            !IsValidOptionalHistoryDate(dateTo) ||
            dateFrom.Length > 0 && dateTo.Length > 0 && ParseHistoryDate(dateFrom) > ParseHistoryDate(dateTo))
        {
            return null;
        }
        var result = await SendDatabaseReadAsync<IReadOnlyList<HistoryFeedRow>>(
            "searchFeedDatabase",
            new { search, filters, favoritesOnly, maxEntries, dateFrom, dateTo },
            cancellationToken);
        return IsValidFeedRows(result, maxEntries) ? result : null;
    }

    internal async Task<IReadOnlyList<HistoryGameLogRow>?> LookupGameLogHistoryAsync(
        IReadOnlyList<string> filters,
        bool favoritesOnly,
        int maxEntries,
        CancellationToken cancellationToken)
    {
        if (!IsValidFilters(filters, GameLogHistoryFilters) || !IsValidHistoryLimit(maxEntries)) return null;
        var result = await SendDatabaseReadAsync<IReadOnlyList<HistoryGameLogRow>>(
            "lookupGameLogDatabase",
            new { filters, favoritesOnly, maxEntries },
            cancellationToken);
        return IsValidGameLogRows(result, maxEntries) ? result : null;
    }

    internal async Task<IReadOnlyList<HistoryGameLogRow>?> SearchGameLogHistoryAsync(
        string search,
        IReadOnlyList<string> filters,
        bool favoritesOnly,
        int maxEntries,
        CancellationToken cancellationToken)
    {
        if (!IsBoundedText(search, 512, allowEmpty: false) ||
            !IsValidFilters(filters, GameLogHistoryFilters) ||
            !IsValidHistoryLimit(maxEntries))
        {
            return null;
        }
        var result = await SendDatabaseReadAsync<IReadOnlyList<HistoryGameLogRow>>(
            "searchGameLogDatabase",
            new { search, filters, favoritesOnly, maxEntries },
            cancellationToken);
        return IsValidGameLogRows(result, maxEntries) ? result : null;
    }

    internal async Task<IReadOnlyList<HistorySessionLocation>?> GetSessionLocationsAsync(
        long? beforeId,
        int limit,
        CancellationToken cancellationToken)
    {
        if (beforeId is <= 0 || limit is < 1 or > 10_000) return null;
        var result = await SendDatabaseReadAsync<IReadOnlyList<HistorySessionLocation>>(
            "getSessionsLocationSegments",
            new { beforeId, limit },
            cancellationToken);
        return result is not null && result.Count <= limit && result.All(IsValidSessionLocation) ? result : null;
    }

    internal async Task<IReadOnlyList<HistorySessionLocation>?> GetSessionLocationsByAnchorAsync(
        string sinceDate,
        int limit,
        CancellationToken cancellationToken)
    {
        if (!TryParseHistoryDate(sinceDate, out _) || limit is < 1 or > 10_000) return null;
        var result = await SendDatabaseReadAsync<IReadOnlyList<HistorySessionLocation>>(
            "getSessionsLocationSegmentsByAnchor",
            new { sinceDate, limit },
            cancellationToken);
        return result is not null && result.Count <= limit && result.All(IsValidSessionLocation) ? result : null;
    }

    internal async Task<IReadOnlyList<HistorySessionEvent>?> GetSessionEventsAsync(
        IReadOnlyList<string> locationTags,
        string afterDate,
        string beforeDate,
        CancellationToken cancellationToken)
    {
        if (locationTags.Count is < 1 or > 1_000 ||
            locationTags.Distinct(StringComparer.Ordinal).Count() != locationTags.Count ||
            locationTags.Any(value => !IsBoundedText(value, 2_048, allowEmpty: false)) ||
            !TryParseHistoryDate(afterDate, out var after) ||
            !TryParseHistoryDate(beforeDate, out var before) ||
            after > before)
        {
            return null;
        }
        var result = await SendDatabaseReadAsync<IReadOnlyList<HistorySessionEvent>>(
            "getSessionsEventsForSegments",
            new { locationTags, afterDate, beforeDate },
            cancellationToken);
        return result is { Count: <= 100_000 } && result.All(IsValidSessionEvent) ? result : null;
    }

    internal async Task<HistoryUserMemo?> GetUserMemoHistoryAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        if (!IsValidEntityId("user", userId)) return null;
        var result = await SendDatabaseReadAsync<HistoryUserMemo>(
            "getUserMemo", new { userId }, cancellationToken);
        return result is not null && IsValidMemo(result.UserId, result.EditedAt, result.Memo, "user", userId)
            ? result
            : null;
    }

    internal async Task<IReadOnlyList<HistoryUserMemoSummary>?> GetAllUserMemosHistoryAsync(
        CancellationToken cancellationToken)
    {
        var result = await SendDatabaseReadAsync<IReadOnlyList<HistoryUserMemoSummary>>(
            "getAllUserMemos", new { }, cancellationToken);
        return result is { Count: <= 100_000 } && result.All(value =>
            IsValidEntityId("user", value.UserId) && IsNullableBoundedText(value.Memo, 262_144))
                ? result
                : null;
    }

    internal async Task<HistoryWorldMemo?> GetWorldMemoHistoryAsync(
        string worldId,
        CancellationToken cancellationToken)
    {
        if (!IsValidEntityId("world", worldId)) return null;
        var result = await SendDatabaseReadAsync<HistoryWorldMemo>(
            "getWorldMemo", new { worldId }, cancellationToken);
        return result is not null && IsValidMemo(result.WorldId, result.EditedAt, result.Memo, "world", worldId)
            ? result
            : null;
    }

    internal async Task<HistoryAvatarMemo?> GetAvatarMemoHistoryAsync(
        string avatarId,
        CancellationToken cancellationToken)
    {
        if (!IsValidEntityId("avatar", avatarId)) return null;
        var result = await SendDatabaseReadAsync<HistoryAvatarMemo>(
            "getAvatarMemoDB", new { avatarId }, cancellationToken);
        return result is not null && IsValidMemo(result.AvatarId, result.EditedAt, result.Memo, "avatar", avatarId)
            ? result
            : null;
    }

    internal async Task<HistoryUserStats?> GetUserStatsHistoryAsync(
        string userId,
        string displayName,
        bool inCurrentWorld,
        CancellationToken cancellationToken)
    {
        if (!IsValidEntityId("user", userId) || !IsBoundedText(displayName, 512, allowEmpty: true)) return null;
        var result = await SendDatabaseReadAsync<HistoryUserStats>(
            "getUserStats",
            new { userId, displayName, inCurrentWorld },
            cancellationToken);
        return result is not null && IsValidUserStats(result, userId) ? result : null;
    }

    internal async Task<IReadOnlyList<HistoryAllUserStatsRow>?> GetAllUserStatsHistoryAsync(
        CancellationToken cancellationToken)
    {
        var result = await SendDatabaseReadAsync<IReadOnlyList<HistoryAllUserStatsRow>>(
            "getAllUserStats", new { }, cancellationToken);
        return result is { Count: <= 100_000 } && result.All(IsValidAllUserStatsRow) ? result : null;
    }

    internal async Task<IReadOnlyList<HistoryFriendLogRow>?> GetFriendLogHistoryAsync(
        CancellationToken cancellationToken)
    {
        var result = await SendDatabaseReadAsync<IReadOnlyList<HistoryFriendLogRow>>(
            "getFriendLogHistory", new { }, cancellationToken);
        return IsValidFriendHistory(result) ? result : null;
    }

    internal async Task<IReadOnlyList<HistoryFriendLogRow>?> GetFriendLogHistoryForUserAsync(
        string userId,
        IReadOnlyList<string> types,
        CancellationToken cancellationToken)
    {
        if (!IsValidEntityId("user", userId) || !IsValidFilters(types, FriendHistoryTypes)) return null;
        var result = await SendDatabaseReadAsync<IReadOnlyList<HistoryFriendLogRow>>(
            "getFriendLogHistoryForUserId", new { userId, types }, cancellationToken);
        return IsValidFriendHistory(result) && result!.All(row => row.UserId == userId) ? result : null;
    }

    internal static bool IsValidHistoryLimit(int value) => value is >= 1 and <= 100_000;

    internal static bool IsValidHistoryFilters(string? value, string kind, out IReadOnlyList<string> filters)
    {
        filters = string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var allowed = kind switch
        {
            "feed" => FeedHistoryFilters,
            "game-log" => GameLogHistoryFilters,
            "friend-history" => FriendHistoryTypes,
            _ => []
        };
        return IsValidFilters(filters, allowed);
    }

    internal static bool IsValidHistoryDate(string? value, bool optional)
    {
        if (optional && string.IsNullOrEmpty(value)) return true;
        return TryParseHistoryDate(value, out _);
    }

    internal static bool IsValidHistoryDateRange(string? dateFrom, string? dateTo) =>
        string.IsNullOrEmpty(dateFrom) ||
        string.IsNullOrEmpty(dateTo) ||
        TryParseHistoryDate(dateFrom, out var from) && TryParseHistoryDate(dateTo, out var to) && from <= to;

    private static bool IsValidFilters(IReadOnlyList<string> filters, HashSet<string> allowed) =>
        filters.Count <= allowed.Count &&
        filters.Distinct(StringComparer.Ordinal).Count() == filters.Count &&
        filters.All(allowed.Contains);

    private static bool IsValidFeedRows(IReadOnlyList<HistoryFeedRow>? rows, int maximum) =>
        rows is not null && rows.Count <= maximum && rows.All(IsValidFeedRow);

    private static bool IsValidFeedRow(HistoryFeedRow row) =>
        row.RowId >= 0 &&
        IsBoundedText(row.CreatedAt, 128, allowEmpty: false) &&
        row.Type is "GPS" or "Status" or "Bio" or "Avatar" or "Online" or "Offline" &&
        IsNullableBoundedText(row.UserId, 256) &&
        IsNullableBoundedText(row.DisplayName, 512) &&
        IsNullableBoundedText(row.Location, 2_048) &&
        IsNullableBoundedText(row.WorldName, 2_048) &&
        IsNullableBoundedText(row.PreviousLocation, 2_048) &&
        IsNullableBoundedText(row.GroupName, 2_048) &&
        IsNullableBoundedText(row.Status, 2_048) &&
        IsNullableBoundedText(row.StatusDescription, 2_048) &&
        IsNullableBoundedText(row.PreviousStatus, 2_048) &&
        IsNullableBoundedText(row.PreviousStatusDescription, 2_048) &&
        IsNullableBoundedText(row.Bio, 65_536) &&
        IsNullableBoundedText(row.PreviousBio, 65_536) &&
        IsNullableBoundedText(row.OwnerId, 2_048) &&
        IsNullableBoundedText(row.AvatarName, 2_048) &&
        IsNullableBoundedText(row.CurrentAvatarImageUrl, 16_384) &&
        IsNullableBoundedText(row.CurrentAvatarThumbnailImageUrl, 16_384) &&
        IsNullableBoundedText(row.PreviousCurrentAvatarImageUrl, 16_384) &&
        IsNullableBoundedText(row.PreviousCurrentAvatarThumbnailImageUrl, 16_384) &&
        IsNullableFinite(row.Time) &&
        IsValidStringList(row.BioLinks, 64, 2_048) &&
        IsValidStringList(row.PreviousBioLinks, 64, 2_048);

    private static bool IsValidGameLogRows(IReadOnlyList<HistoryGameLogRow>? rows, int maximum) =>
        rows is not null && rows.Count <= maximum && rows.All(IsValidGameLogRow);

    private static bool IsValidGameLogRow(HistoryGameLogRow row) =>
        row.RowId >= 0 &&
        IsBoundedText(row.CreatedAt, 128, allowEmpty: false) &&
        row.Type is "Location" or "OnPlayerJoined" or "OnPlayerLeft" or "PortalSpawn" or "Event" or "External" or
            "VideoPlay" or "StringLoad" or "ImageLoad" &&
        IsNullableBoundedText(row.DisplayName, 2_048) &&
        IsNullableBoundedText(row.Location, 2_048) &&
        IsNullableBoundedText(row.UserId, 2_048) &&
        IsNullableFinite(row.Time) &&
        IsNullableBoundedText(row.WorldId, 2_048) &&
        IsNullableBoundedText(row.WorldName, 2_048) &&
        IsNullableBoundedText(row.GroupName, 2_048) &&
        IsNullableBoundedText(row.InstanceId, 2_048) &&
        IsNullableBoundedText(row.VideoUrl, 65_536) &&
        IsNullableBoundedText(row.VideoName, 2_048) &&
        IsNullableBoundedText(row.VideoId, 2_048) &&
        IsNullableBoundedText(row.ResourceUrl, 65_536) &&
        IsNullableBoundedText(row.ResourceType, 2_048) &&
        IsNullableBoundedText(row.Data, 65_536) &&
        IsNullableBoundedText(row.Message, 65_536);

    private static bool IsValidSessionLocation(HistorySessionLocation row) =>
        row.Id >= 0 &&
        IsBoundedText(row.CreatedAt, 128, allowEmpty: false) &&
        IsBoundedText(row.Location, 2_048, allowEmpty: false) &&
        IsNullableBoundedText(row.WorldId, 256) &&
        IsNullableBoundedText(row.WorldName, 2_048) &&
        IsNullableFinite(row.Time) &&
        IsNullableBoundedText(row.GroupName, 2_048);

    private static bool IsValidSessionEvent(HistorySessionEvent row) =>
        row.Type is "OnPlayerJoined" or "OnPlayerLeft" or "VideoPlay" &&
        IsBoundedText(row.CreatedAt, 128, allowEmpty: false) &&
        IsNullableBoundedText(row.DisplayName, 512) &&
        IsNullableBoundedText(row.UserId, 256) &&
        IsBoundedText(row.Location, 2_048, allowEmpty: false) &&
        IsNullableBoundedText(row.VideoUrl, 65_536) &&
        IsNullableBoundedText(row.VideoName, 2_048) &&
        IsNullableBoundedText(row.VideoId, 2_048);

    private static bool IsValidMemo(
        string? resultId,
        string? editedAt,
        string? memo,
        string entityType,
        string expectedId) =>
        (resultId is "" || resultId == expectedId && IsValidEntityId(entityType, resultId)) &&
        IsNullableBoundedText(editedAt, 128) &&
        IsNullableBoundedText(memo, 262_144);

    private static bool IsValidUserStats(HistoryUserStats value, string expectedUserId) =>
        value.UserId == expectedUserId &&
        double.IsFinite(value.TimeSpent) &&
        value.TimeSpent is >= 0 and <= 9_007_199_254_740_991 &&
        IsNullableBoundedText(value.LastSeen, 128) &&
        value.JoinCount >= 0 &&
        value.PreviousDisplayNames is { Count: <= 10_000 } &&
        value.PreviousDisplayNames.All(entry =>
            IsBoundedText(entry.DisplayName, 512, allowEmpty: true) &&
            IsBoundedText(entry.UpdatedAt, 128, allowEmpty: false));

    private static bool IsValidAllUserStatsRow(HistoryAllUserStatsRow value) =>
        IsNullableBoundedText(value.LastSeen, 128) &&
        (value.UserId is null || IsValidEntityId("user", value.UserId)) &&
        double.IsFinite(value.TimeSpent) &&
        value.TimeSpent is >= 0 and <= 9_007_199_254_740_991 &&
        value.JoinCount >= 0 &&
        IsNullableBoundedText(value.DisplayName, 512);

    private static bool IsValidFriendHistory(IReadOnlyList<HistoryFriendLogRow>? values) =>
        values is { Count: <= 100_000 } && values.All(value =>
            value.RowId >= 0 &&
            IsBoundedText(value.CreatedAt, 128, allowEmpty: false) &&
            FriendHistoryTypes.Contains(value.Type ?? "") &&
            IsValidEntityId("user", value.UserId) &&
            IsNullableBoundedText(value.DisplayName, 512) &&
            IsNullableBoundedText(value.PreviousDisplayName, 512) &&
            IsNullableBoundedText(value.TrustLevel, 128) &&
            IsNullableBoundedText(value.PreviousTrustLevel, 128) &&
            value.FriendNumber is null or >= 0);

    private static bool IsValidStringList(IReadOnlyList<string>? values, int maximumCount, int maximumLength) =>
        values is not null && values.Count <= maximumCount &&
        values.All(value => IsBoundedText(value, maximumLength, allowEmpty: true));

    private static bool IsNullableBoundedText(string? value, int maximumLength) =>
        value is null || IsBoundedText(value, maximumLength, allowEmpty: true);

    private static bool IsNullableFinite(double? value) =>
        value is null || double.IsFinite(value.Value) && Math.Abs(value.Value) <= TimeSpan.FromDays(366).TotalMilliseconds;

    private static bool IsValidOptionalHistoryDate(string value) => value.Length == 0 || TryParseHistoryDate(value, out _);

    private static DateTimeOffset ParseHistoryDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal);

    private static bool TryParseHistoryDate(string? value, out DateTimeOffset result)
    {
        result = default;
        return IsBoundedText(value, 128, allowEmpty: false) &&
            DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out result);
    }
}
