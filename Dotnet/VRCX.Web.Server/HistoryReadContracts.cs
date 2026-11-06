using System.Text.Json.Serialization;

namespace VRCX.Web.Server;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record HistoryFeedRow(
    [property: JsonRequired] long RowId,
    [property: JsonRequired, JsonPropertyName("created_at")] string? CreatedAt,
    [property: JsonRequired] string? UserId,
    [property: JsonRequired] string? DisplayName,
    [property: JsonRequired] string? Type,
    [property: JsonRequired] string? Location,
    [property: JsonRequired] string? WorldName,
    [property: JsonRequired] string? PreviousLocation,
    [property: JsonRequired] double? Time,
    [property: JsonRequired] string? GroupName,
    [property: JsonRequired] string? Status,
    [property: JsonRequired] string? StatusDescription,
    [property: JsonRequired] string? PreviousStatus,
    [property: JsonRequired] string? PreviousStatusDescription,
    [property: JsonRequired] string? Bio,
    [property: JsonRequired] string? PreviousBio,
    [property: JsonRequired] IReadOnlyList<string>? BioLinks,
    [property: JsonRequired] IReadOnlyList<string>? PreviousBioLinks,
    [property: JsonRequired] string? OwnerId,
    [property: JsonRequired] string? AvatarName,
    [property: JsonRequired] string? CurrentAvatarImageUrl,
    [property: JsonRequired] string? CurrentAvatarThumbnailImageUrl,
    [property: JsonRequired] string? PreviousCurrentAvatarImageUrl,
    [property: JsonRequired] string? PreviousCurrentAvatarThumbnailImageUrl);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record HistoryGameLogRow(
    [property: JsonRequired] long RowId,
    [property: JsonRequired, JsonPropertyName("created_at")] string? CreatedAt,
    [property: JsonRequired] string? Type,
    [property: JsonRequired] string? DisplayName,
    [property: JsonRequired] string? Location,
    [property: JsonRequired] string? UserId,
    [property: JsonRequired] double? Time,
    [property: JsonRequired] string? WorldId,
    [property: JsonRequired] string? WorldName,
    [property: JsonRequired] string? GroupName,
    [property: JsonRequired] string? InstanceId,
    [property: JsonRequired] string? VideoUrl,
    [property: JsonRequired] string? VideoName,
    [property: JsonRequired] string? VideoId,
    [property: JsonRequired] string? ResourceUrl,
    [property: JsonRequired] string? ResourceType,
    [property: JsonRequired] string? Data,
    [property: JsonRequired] string? Message);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record HistorySessionLocation(
    [property: JsonRequired] long Id,
    [property: JsonRequired, JsonPropertyName("created_at")] string? CreatedAt,
    [property: JsonRequired] string? Location,
    [property: JsonRequired] string? WorldId,
    [property: JsonRequired] string? WorldName,
    [property: JsonRequired] double? Time,
    [property: JsonRequired] string? GroupName);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record HistorySessionEvent(
    [property: JsonRequired] string? Type,
    [property: JsonRequired, JsonPropertyName("created_at")] string? CreatedAt,
    [property: JsonRequired] string? DisplayName,
    [property: JsonRequired] string? UserId,
    [property: JsonRequired] string? Location,
    [property: JsonRequired] string? VideoUrl,
    [property: JsonRequired] string? VideoName,
    [property: JsonRequired] string? VideoId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record HistoryUserMemo(
    [property: JsonRequired] string? UserId,
    [property: JsonRequired] string? EditedAt,
    [property: JsonRequired] string? Memo);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record HistoryWorldMemo(
    [property: JsonRequired] string? WorldId,
    [property: JsonRequired] string? EditedAt,
    [property: JsonRequired] string? Memo);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record HistoryAvatarMemo(
    [property: JsonRequired] string? AvatarId,
    [property: JsonRequired] string? EditedAt,
    [property: JsonRequired] string? Memo);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record HistoryUserMemoSummary(
    [property: JsonRequired] string? UserId,
    [property: JsonRequired] string? Memo);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record HistoryPreviousDisplayName(
    [property: JsonRequired] string? DisplayName,
    [property: JsonRequired] string? UpdatedAt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record HistoryUserStats(
    [property: JsonRequired] double TimeSpent,
    [property: JsonRequired] string? LastSeen,
    [property: JsonRequired] long JoinCount,
    [property: JsonRequired] string? UserId,
    [property: JsonRequired] IReadOnlyList<HistoryPreviousDisplayName>? PreviousDisplayNames);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record HistoryAllUserStatsRow(
    [property: JsonRequired] string? LastSeen,
    [property: JsonRequired] string? UserId,
    [property: JsonRequired] double TimeSpent,
    [property: JsonRequired] long JoinCount,
    [property: JsonRequired] string? DisplayName);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record HistoryFriendLogRow(
    [property: JsonRequired] long RowId,
    [property: JsonRequired, JsonPropertyName("created_at")] string? CreatedAt,
    [property: JsonRequired] string? Type,
    [property: JsonRequired] string? UserId,
    [property: JsonRequired] string? DisplayName,
    [property: JsonRequired] string? PreviousDisplayName,
    [property: JsonRequired] string? TrustLevel,
    [property: JsonRequired] string? PreviousTrustLevel,
    [property: JsonRequired] int? FriendNumber);

public sealed record NativeFeedQuery(
    string? Search = null,
    string? Filters = null,
    bool FavoritesOnly = false,
    int MaxEntries = 500,
    string? DateFrom = null,
    string? DateTo = null);

public sealed record NativeGameLogQuery(
    string? Search = null,
    string? Filters = null,
    bool FavoritesOnly = false,
    int MaxEntries = 500);

public sealed record NativeSessionLocationsQuery(long? BeforeId = null, int Limit = 20);

public sealed record NativeSessionAnchorQuery(string? SinceDate = null, int Limit = 20);

public sealed record NativeUserStatsQuery(string? DisplayName = null, bool InCurrentWorld = false);
