using System.Text.Json;
using System.Text.Json.Serialization;

namespace VRCX.Web.Server;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record VrchatWorkerEntityResourceRequest(string Name, object Arguments);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record VrchatWorkerEntityResourceResponse(
    [property: JsonRequired] bool Ok,
    [property: JsonRequired] JsonElement? Result,
    [property: JsonRequired] string? ErrorCode);

internal sealed record VrchatEntityResourceResult(
    bool Ok,
    JsonElement? Result,
    string? ErrorCode);

public sealed record EntityPageQuery(int Offset = 0, int Limit = 50);

public sealed record UserWorldsQuery(
    int Offset = 0,
    int Limit = 50,
    string Sort = "updated",
    string Order = "descending",
    string ReleaseStatus = "public",
    string User = "");

public sealed record FavoriteWorldsQuery(
    string? Tag = null,
    int Offset = 0,
    int Limit = 50);

public sealed record OwnedAvatarsQuery(
    int Offset = 0,
    int Limit = 50,
    string Sort = "updated",
    string Order = "descending",
    string ReleaseStatus = "all");

public sealed record GroupMembersQuery(
    int Offset = 0,
    int Limit = 100,
    string Sort = "joinedAt:desc",
    string RoleId = "");

public sealed record GroupMemberSearchQuery(
    string? Q = null,
    int Offset = 0,
    int Limit = 100);
