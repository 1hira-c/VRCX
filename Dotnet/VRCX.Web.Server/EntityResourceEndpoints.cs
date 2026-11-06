namespace VRCX.Web.Server;

internal static class EntityResourceEndpoints
{
    public static RouteGroupBuilder MapEntityResourceApi(this RouteGroupBuilder api)
    {
        var resources = api.MapGroup(string.Empty)
            .RequireRateLimiting(VrchatWorkerEndpoints.ReadRateLimitPolicy);

        resources.MapGet("/users/{userId}/public-profile", (
            string userId,
            VrchatWorkerClient worker,
            CancellationToken cancellationToken) =>
            UserResourceAsync(worker, "userPublicProfile", userId, cancellationToken));

        resources.MapGet("/users/{userId}/represented-group", (
            string userId,
            VrchatWorkerClient worker,
            CancellationToken cancellationToken) =>
            UserResourceAsync(worker, "userRepresentedGroup", userId, cancellationToken));

        resources.MapGet("/users/{userId}/groups", (
            string userId,
            VrchatWorkerClient worker,
            CancellationToken cancellationToken) =>
            UserResourceAsync(worker, "userGroups", userId, cancellationToken));

        resources.MapGet("/users/{userId}/worlds", async (
            string userId,
            [AsParameters] UserWorldsQuery query,
            VrchatWorkerClient worker,
            CancellationToken cancellationToken) =>
        {
            if (!IsUserId(userId) || !IsPage(query.Offset, query.Limit) ||
                query.Sort is not ("name" or "updated" or "created" or "favorites" or "popularity") ||
                query.Order is not ("ascending" or "descending") ||
                query.ReleaseStatus is not ("public" or "all") ||
                query.User is not ("" or "me"))
            {
                return Invalid();
            }
            return await ForwardAsync(worker, "userWorlds", new
            {
                userId,
                n = query.Limit,
                offset = query.Offset,
                sort = query.Sort,
                order = query.Order,
                releaseStatus = query.ReleaseStatus,
                user = query.User
            }, cancellationToken);
        });

        resources.MapGet("/users/{userId}/favorite-groups", async (
            string userId,
            [AsParameters] EntityPageQuery query,
            VrchatWorkerClient worker,
            CancellationToken cancellationToken) =>
        {
            if (!IsUserId(userId) || !IsPage(query.Offset, query.Limit)) return Invalid();
            return await ForwardAsync(worker, "userFavoriteGroups", new
            {
                userId,
                n = query.Limit,
                offset = query.Offset
            }, cancellationToken);
        });

        resources.MapGet("/users/{userId}/favorite-worlds", async (
            string userId,
            [AsParameters] FavoriteWorldsQuery query,
            VrchatWorkerClient worker,
            CancellationToken cancellationToken) =>
        {
            if (!IsUserId(userId) || !IsPage(query.Offset, query.Limit) || !IsText(query.Tag, 256)) return Invalid();
            return await ForwardAsync(worker, "userFavoriteWorlds", new
            {
                userId,
                tag = query.Tag!,
                n = query.Limit,
                offset = query.Offset
            }, cancellationToken);
        });

        resources.MapGet("/users/{userId}/avatars/owned", async (
            string userId,
            [AsParameters] OwnedAvatarsQuery query,
            VrchatWorkerClient worker,
            CancellationToken cancellationToken) =>
        {
            if (!IsUserId(userId) || !IsPage(query.Offset, query.Limit) || query.Sort != "updated" ||
                query.Order is not ("ascending" or "descending") ||
                query.ReleaseStatus is not ("public" or "private" or "all"))
            {
                return Invalid();
            }
            return await ForwardAsync(worker, "userOwnedAvatars", new
            {
                userId,
                n = query.Limit,
                offset = query.Offset,
                sort = query.Sort,
                order = query.Order,
                releaseStatus = query.ReleaseStatus
            }, cancellationToken);
        });

        resources.MapGet("/users/{userId}/avatars/licensed", (
            string userId,
            [AsParameters] EntityPageQuery query,
            VrchatWorkerClient worker,
            CancellationToken cancellationToken) =>
            PagedUserResourceAsync(worker, "userLicensedAvatars", userId, query, cancellationToken));

        resources.MapGet("/users/{userId}/avatars/remote", (
            string userId,
            VrchatWorkerClient worker,
            CancellationToken cancellationToken) =>
            UserResourceAsync(worker, "userRemoteAvatars", userId, cancellationToken));

        resources.MapGet("/groups/{groupId}/posts", async (
            string groupId,
            [AsParameters] EntityPageQuery query,
            VrchatWorkerClient worker,
            CancellationToken cancellationToken) =>
        {
            if (!IsGroupId(groupId) || !IsPage(query.Offset, query.Limit)) return Invalid();
            return await ForwardAsync(worker, "groupPosts", new
            {
                groupId,
                n = query.Limit,
                offset = query.Offset
            }, cancellationToken);
        });

        resources.MapGet("/groups/{groupId}/members/{userId}", async (
            string groupId,
            string userId,
            VrchatWorkerClient worker,
            CancellationToken cancellationToken) =>
        {
            if (!IsGroupId(groupId) || !IsUserId(userId)) return Invalid();
            return await ForwardAsync(worker, "groupMember", new { groupId, userId }, cancellationToken);
        });

        resources.MapGet("/groups/{groupId}/members", async (
            string groupId,
            [AsParameters] GroupMembersQuery query,
            VrchatWorkerClient worker,
            CancellationToken cancellationToken) =>
        {
            if (!IsGroupId(groupId) || !IsPage(query.Offset, query.Limit) ||
                query.Sort is not ("joinedAt:asc" or "joinedAt:desc") ||
                query.RoleId != "" && !IsId(query.RoleId, "grol_"))
            {
                return Invalid();
            }
            return await ForwardAsync(worker, "groupMembers", new
            {
                groupId,
                n = query.Limit,
                offset = query.Offset,
                sort = query.Sort,
                roleId = query.RoleId
            }, cancellationToken);
        });

        resources.MapGet("/groups/{groupId}/members/search", async (
            string groupId,
            [AsParameters] GroupMemberSearchQuery query,
            VrchatWorkerClient worker,
            CancellationToken cancellationToken) =>
        {
            var search = query.Q?.Trim();
            if (!IsGroupId(groupId) || !IsPage(query.Offset, query.Limit) ||
                !IsText(search, 128) || search!.Length < 3)
            {
                return Invalid();
            }
            return await ForwardAsync(worker, "groupMembersSearch", new
            {
                groupId,
                query = search,
                n = query.Limit,
                offset = query.Offset
            }, cancellationToken);
        });

        resources.MapGet("/groups/{groupId}/galleries/{galleryId}", async (
            string groupId,
            string galleryId,
            [AsParameters] EntityPageQuery query,
            VrchatWorkerClient worker,
            CancellationToken cancellationToken) =>
        {
            if (!IsGroupId(groupId) || !IsId(galleryId, "ggal_") || !IsPage(query.Offset, query.Limit))
            {
                return Invalid();
            }
            return await ForwardAsync(worker, "groupGallery", new
            {
                groupId,
                galleryId,
                n = query.Limit,
                offset = query.Offset
            }, cancellationToken);
        });

        resources.MapGet("/avatars/{avatarId}/gallery", async (
            string avatarId,
            VrchatWorkerClient worker,
            CancellationToken cancellationToken) =>
        {
            if (!IsId(avatarId, "avtr_")) return Invalid();
            return await ForwardAsync(worker, "avatarGallery", new { avatarId }, cancellationToken);
        });

        resources.MapGet("/worlds/{worldId}/instances/{instanceId}", async (
            string worldId,
            string instanceId,
            VrchatWorkerClient worker,
            CancellationToken cancellationToken) =>
        {
            if (!IsId(worldId, "wrld_") || !IsPublicInstanceId(instanceId)) return Invalid();
            return await ForwardAsync(worker, "worldInstance", new { worldId, instanceId }, cancellationToken);
        });

        return resources;
    }

    private static async Task<IResult> UserResourceAsync(
        VrchatWorkerClient worker,
        string name,
        string userId,
        CancellationToken cancellationToken)
    {
        if (!IsUserId(userId)) return Invalid();
        return await ForwardAsync(worker, name, new { userId }, cancellationToken);
    }

    private static async Task<IResult> PagedUserResourceAsync(
        VrchatWorkerClient worker,
        string name,
        string userId,
        EntityPageQuery query,
        CancellationToken cancellationToken)
    {
        if (!IsUserId(userId) || !IsPage(query.Offset, query.Limit)) return Invalid();
        return await ForwardAsync(worker, name, new
        {
            userId,
            n = query.Limit,
            offset = query.Offset
        }, cancellationToken);
    }

    private static async Task<IResult> ForwardAsync(
        VrchatWorkerClient worker,
        string name,
        object arguments,
        CancellationToken cancellationToken)
    {
        var response = await worker.GetEntityResourceAsync(name, arguments, cancellationToken);
        if (response is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "VRCX entity worker is unavailable.");
        }
        if (!response.Ok)
        {
            var status = response.ErrorCode == "not_found"
                ? StatusCodes.Status404NotFound
                : StatusCodes.Status503ServiceUnavailable;
            return Results.Json(new { error = response.ErrorCode }, statusCode: status);
        }
        return Results.Ok(response.Result!.Value);
    }

    private static IResult Invalid() => Results.BadRequest(new { error = "invalid_entity_resource" });

    private static bool IsPage(int offset, int limit) =>
        offset is >= 0 and <= 10_000 && limit is >= 1 and <= 100;

    private static bool IsUserId(string value) => IsId(value, "usr_");

    private static bool IsGroupId(string value) => IsId(value, "grp_");

    private static bool IsId(string? value, string prefix) =>
        value is { Length: > 0 and <= 256 } &&
        value.StartsWith(prefix, StringComparison.Ordinal) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static bool IsText(string? value, int maximumLength) =>
        value is { Length: > 0 } && value.Length <= maximumLength && value.All(character => !char.IsControl(character));

    private static bool IsPublicInstanceId(string? value) =>
        value is { Length: > 0 and <= 2048 } &&
        !value.Contains("://", StringComparison.Ordinal) &&
        !value.Contains("~private(", StringComparison.OrdinalIgnoreCase) &&
        !value.Contains("~friends(", StringComparison.OrdinalIgnoreCase) &&
        !value.Contains("~hidden(", StringComparison.OrdinalIgnoreCase) &&
        !value.Contains("~nonce(", StringComparison.OrdinalIgnoreCase) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '~' or '(' or ')' or
            '.' or ',' or ':' or '+' or '-');
}
