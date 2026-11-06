using System.Text.Json;

namespace VRCX.Web.Server;

public sealed class VrcxReadModelOptions
{
    public string DatabasePath { get; set; } = "/data/runtime/VRCX.sqlite3";

    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromMinutes(15);
}

public sealed record PageResult<T>(IReadOnlyList<T> Items, int Total, int Offset, int Limit);

public sealed record FriendRecord(
    string Id,
    string DisplayName,
    string? Username,
    string? Status,
    string? Location,
    string? WorldName,
    DateTimeOffset? UpdatedAt,
    JsonElement Raw);

public sealed record FriendHistoryRecord(
    long Id,
    string FriendId,
    DateTimeOffset? CreatedAt,
    string? Type,
    string? DisplayName,
    string? PreviousDisplayName,
    string? TrustLevel,
    string? PreviousTrustLevel,
    int? FriendNumber);

public sealed record FeedRecord(
    long Id,
    string Kind,
    string? UserId,
    string? DisplayName,
    DateTimeOffset? CreatedAt,
    JsonElement Payload);

public sealed record NotificationRecord(
    string Id,
    string? Type,
    DateTimeOffset? CreatedAt,
    string? Title,
    string? Message,
    bool? Seen,
    JsonElement Payload);

public sealed record FavoriteRecord(
    string Id,
    string Type,
    string FavoriteId,
    IReadOnlyList<string> Tags,
    string GroupKey,
    int SortOrder,
    DateTimeOffset? CreatedAt,
    string? TargetName,
    JsonElement TargetPayload);

public sealed record FavoritePageResult(
    IReadOnlyList<FavoriteRecord> Items,
    int Total,
    int Offset,
    int Limit,
    bool Ready);

public sealed record FavoriteGroupRecord(
    string Key,
    string Type,
    string Name,
    string DisplayName,
    int Capacity,
    int Count,
    string Visibility,
    int SortOrder);

public sealed record FavoriteGroupsResult(
    bool Ready,
    IReadOnlyList<FavoriteGroupRecord> Items);

public sealed record GameLogRecord(
    long Id,
    string Kind,
    DateTimeOffset? CreatedAt,
    JsonElement Payload);

public sealed record MemoRecord(
    string EntityType,
    string EntityId,
    DateTimeOffset? EditedAt,
    string Content);

public sealed record AnalyticsSummary(
    int FriendCount,
    int FeedCount,
    int NotificationCount,
    int FavoriteCount,
    int GameLogCount,
    int ActivitySessionCount,
    int ActivityUserCount,
    long ActivityMinutes,
    DateTimeOffset? OldestGameLogAt,
    DateTimeOffset? NewestGameLogAt);

public sealed record ServerCounts(int Friends, int Feed, int Notifications, int Favorites, int GameLog);

public sealed record SyncView(
    string State,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? NextRetryAt,
    string? ErrorCode,
    bool Stale);

public sealed record PipelineView(
    string State,
    DateTimeOffset? ConnectedAt,
    DateTimeOffset? LastEventAt,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? NextRetryAt,
    string? ErrorCode);

public sealed record ServerStatus(
    bool Configured,
    bool AuthenticationRequired,
    SyncView Sync,
    PipelineView Pipeline,
    ServerCounts Counts,
    string? CurrentUserId);

public sealed record SearchDocument(
    string Type,
    string Id,
    string Name,
    string? OwnerId,
    string? Description,
    DateTimeOffset? UpdatedAt,
    JsonElement Payload);
