using System.Text.Json;
using System.Text.Json.Serialization;

namespace VRCX.Web.Server;

[JsonConverter(typeof(VrchatTwoFactorKindJsonConverter))]
public enum VrchatTwoFactorKind
{
    Totp,
    Otp,
    EmailOtp
}

[JsonConverter(typeof(VrchatAuthStateJsonConverter))]
public enum VrchatAuthState
{
    RequiresAuthentication,
    Authenticating,
    TwoFactorRequired,
    Authenticated
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VrchatLoginRequest(string? Username, string? Password);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VrchatTwoFactorRequest(VrchatTwoFactorKind? Kind, string? Code);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VrchatAuthChallenge([property: JsonRequired] IReadOnlyList<VrchatTwoFactorKind> Kinds);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VrchatCurrentUser(string Id);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VrchatAuthStatusResponse(
    bool Ok,
    VrchatAuthState Status,
    VrchatAuthChallenge? Challenge,
    VrchatCurrentUser? CurrentUser,
    string? ErrorCode);

internal enum VrchatWorkerAuthFailure
{
    Unavailable,
    Timeout,
    BadResponse
}

internal sealed record VrchatWorkerAuthResult(
    VrchatAuthStatusResponse? Response,
    VrchatWorkerAuthFailure? Failure = null,
    string? Reason = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record VrchatWorkerResponse(
    [property: JsonRequired] bool Ok,
    [property: JsonRequired] VrchatAuthState Status,
    [property: JsonRequired] string? CurrentUserId,
    [property: JsonRequired] VrchatAuthChallenge? Challenge,
    [property: JsonRequired] string? ErrorCode);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record VrchatWorkerSearchRequest(
    string Type,
    string Query,
    int Offset,
    int Limit,
    VrchatWorkerSearchOptions Options);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record VrchatWorkerSearchOptions(
    string? WorldOption = null,
    string? Sort = null,
    string? Order = null,
    bool? Featured = null,
    string? Tag = null,
    string? User = null,
    string? ReleaseStatus = null,
    bool? Fuzzy = null,
    string? CustomFields = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record VrchatWorkerEntityRequest(string Type, string Id);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record VrchatWorkerEntityDocument(
    [property: JsonRequired] string? Type,
    [property: JsonRequired] string? Id,
    [property: JsonRequired] string? Name,
    [property: JsonRequired] string? OwnerId,
    [property: JsonRequired] string? Description,
    [property: JsonRequired] string? UpdatedAt,
    [property: JsonRequired] JsonElement Payload);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record VrchatWorkerSearchResponse(
    [property: JsonRequired] bool Ok,
    [property: JsonRequired] IReadOnlyList<VrchatWorkerEntityDocument>? Items,
    [property: JsonRequired] int Total,
    [property: JsonRequired] int Offset,
    [property: JsonRequired] int Limit,
    [property: JsonRequired] string? ErrorCode);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record VrchatWorkerEntityResponse(
    [property: JsonRequired] bool Ok,
    [property: JsonRequired] VrchatWorkerEntityDocument? Item,
    [property: JsonRequired] string? ErrorCode);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record VrchatWorkerDatabaseReadRequest(string Name, object Arguments);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record VrchatWorkerDatabaseReadResponse<T>(
    [property: JsonRequired] bool Ok,
    [property: JsonRequired] T? Result,
    [property: JsonRequired] string? ErrorCode);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record InstanceActivityRow(
    [property: JsonRequired, JsonPropertyName("id")] long Id,
    [property: JsonRequired, JsonPropertyName("created_at")] string? CreatedAt,
    [property: JsonRequired, JsonPropertyName("type")] string? Type,
    [property: JsonRequired, JsonPropertyName("display_name")] string? DisplayName,
    [property: JsonRequired, JsonPropertyName("location")] string? Location,
    [property: JsonRequired, JsonPropertyName("user_id")] string? UserId,
    [property: JsonRequired, JsonPropertyName("time")] double Time);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record InstanceActivityDetail(
    [property: JsonRequired] string? Location,
    [property: JsonRequired] IReadOnlyList<InstanceActivityRow>? Rows);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record InstanceActivityResult(
    [property: JsonRequired] IReadOnlyList<InstanceActivityRow>? CurrentUserData,
    [property: JsonRequired] IReadOnlyList<InstanceActivityDetail>? DetailData);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record HotWorldResult(
    [property: JsonRequired] string? WorldId,
    [property: JsonRequired] string? WorldName,
    [property: JsonRequired] long VisitCount,
    [property: JsonRequired] long UniqueFriends,
    [property: JsonRequired] string? LastVisited,
    [property: JsonRequired] string? Trend);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record HotWorldFriendResult(
    [property: JsonRequired] string? UserId,
    [property: JsonRequired] string? DisplayName,
    [property: JsonRequired] long VisitCount,
    [property: JsonRequired] string? LastVisit);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record MutualGraphSnapshotEntry(
    [property: JsonRequired] string? FriendId,
    [property: JsonRequired] IReadOnlyList<string>? MutualIds);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record MutualGraphMetaEntry(
    [property: JsonRequired] string? FriendId,
    [property: JsonRequired] string? LastFetchedAt,
    [property: JsonRequired] bool OptedOut);

public sealed class VrchatTwoFactorKindJsonConverter : JsonConverter<VrchatTwoFactorKind>
{
    public override VrchatTwoFactorKind Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("VRChat two-factor kind must be a string.");
        }

        return reader.GetString() switch
        {
            "totp" => VrchatTwoFactorKind.Totp,
            "otp" => VrchatTwoFactorKind.Otp,
            "emailOtp" => VrchatTwoFactorKind.EmailOtp,
            _ => throw new JsonException("Unsupported VRChat two-factor kind.")
        };
    }

    public override void Write(
        Utf8JsonWriter writer,
        VrchatTwoFactorKind value,
        JsonSerializerOptions options) => writer.WriteStringValue(value switch
        {
            VrchatTwoFactorKind.Totp => "totp",
            VrchatTwoFactorKind.Otp => "otp",
            VrchatTwoFactorKind.EmailOtp => "emailOtp",
            _ => throw new JsonException("Unsupported VRChat two-factor kind.")
        });
}

public sealed class VrchatAuthStateJsonConverter : JsonConverter<VrchatAuthState>
{
    public override VrchatAuthState Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("VRChat authentication status must be a string.");
        }

        return reader.GetString() switch
        {
            "requires_authentication" => VrchatAuthState.RequiresAuthentication,
            "authenticating" => VrchatAuthState.Authenticating,
            "two_factor_required" => VrchatAuthState.TwoFactorRequired,
            "authenticated" => VrchatAuthState.Authenticated,
            _ => throw new JsonException("Unsupported VRChat authentication status.")
        };
    }

    public override void Write(
        Utf8JsonWriter writer,
        VrchatAuthState value,
        JsonSerializerOptions options) => writer.WriteStringValue(value switch
        {
            VrchatAuthState.RequiresAuthentication => "requires_authentication",
            VrchatAuthState.Authenticating => "authenticating",
            VrchatAuthState.TwoFactorRequired => "two_factor_required",
            VrchatAuthState.Authenticated => "authenticated",
            _ => throw new JsonException("Unsupported VRChat authentication status.")
        });
}
