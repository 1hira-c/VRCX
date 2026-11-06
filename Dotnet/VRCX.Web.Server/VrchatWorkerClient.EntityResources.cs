using System.Text.Json;

namespace VRCX.Web.Server;

internal sealed partial class VrchatWorkerClient
{
    private static readonly HashSet<string> EntityResourceNames = new(StringComparer.Ordinal)
    {
        "userPublicProfile",
        "userRepresentedGroup",
        "userGroups",
        "userWorlds",
        "userFavoriteGroups",
        "userFavoriteWorlds",
        "userOwnedAvatars",
        "userLicensedAvatars",
        "userRemoteAvatars",
        "groupPosts",
        "groupMember",
        "groupMembers",
        "groupMembersSearch",
        "groupGallery",
        "avatarGallery",
        "worldInstance"
    };

    internal async Task<VrchatEntityResourceResult?> GetEntityResourceAsync(
        string name,
        object arguments,
        CancellationToken cancellationToken)
    {
        if (!EntityResourceNames.Contains(name))
        {
            return null;
        }

        var response = await SendJsonAsync<VrchatWorkerEntityResourceResponse>(
            VrchatWorkerOperation.EntityResource,
            new VrchatWorkerEntityResourceRequest(name, arguments),
            MaximumDatabaseReadResponseBytes,
            cancellationToken);
        if (response is null || !IsValidErrorCode(response.ErrorCode) ||
            response.Ok == (response.ErrorCode is not null) ||
            response.Ok == (response.Result is null))
        {
            return null;
        }

        if (!response.Ok)
        {
            return new VrchatEntityResourceResult(false, null, response.ErrorCode);
        }

        var result = response.Result!.Value;
        var nodes = 0;
        if (result.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array) ||
            !IsValidJsonValue(result, 0, ref nodes) ||
            !IsValidEntityResourceRoot(name, result))
        {
            return null;
        }

        return new VrchatEntityResourceResult(true, result.Clone(), null);
    }

    private static bool IsValidEntityResourceRoot(string name, JsonElement result)
    {
        var expectsObject = name is "userPublicProfile" or "userRepresentedGroup" or
            "groupPosts" or "groupMember" or "groupMembersSearch" or "worldInstance";
        if (expectsObject != (result.ValueKind == JsonValueKind.Object))
        {
            return false;
        }

        if (result.ValueKind == JsonValueKind.Array && result.GetArrayLength() > 10_000)
        {
            return false;
        }
        if (name == "groupPosts" &&
            (!result.TryGetProperty("posts", out var posts) || posts.ValueKind != JsonValueKind.Array ||
             !result.TryGetProperty("total", out var total) || !total.TryGetInt32(out var totalCount) ||
             totalCount < posts.GetArrayLength()))
        {
            return false;
        }
        if (name == "groupMembersSearch" &&
            (!result.TryGetProperty("results", out var members) || members.ValueKind != JsonValueKind.Array ||
             !result.TryGetProperty("total", out var memberTotal) || !memberTotal.TryGetInt32(out var count) ||
             count < members.GetArrayLength()))
        {
            return false;
        }
        return true;
    }

    private static bool IsValidErrorCode(string? value) =>
        value is null || value.Length is >= 1 and <= 64 &&
        value.All(character => character is >= 'a' and <= 'z' || char.IsAsciiDigit(character) || character == '_');
}
