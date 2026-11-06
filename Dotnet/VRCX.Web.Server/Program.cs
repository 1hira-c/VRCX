using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using VRCX.Web.Auth;
using VRCX.Web.Server;

var issueAdminRecovery = args.Length == 1 &&
    string.Equals(args[0], "--issue-admin-recovery", StringComparison.Ordinal);
var builder = WebApplication.CreateBuilder(issueAdminRecovery ? Array.Empty<string>() : args);

var authOptions = builder.Configuration.GetSection("VrcxWeb:Auth").Get<ServerAuthOptions>() ?? new ServerAuthOptions();
var readModelOptions = builder.Configuration.GetSection("VrcxWeb:ReadModel").Get<VrcxReadModelOptions>() ?? new VrcxReadModelOptions();
var workerProcessOptions = builder.Configuration.GetSection("VrcxWeb:WorkerProcess").Get<VrcxWorkerProcessOptions>() ??
    new VrcxWorkerProcessOptions();
var snapshotUploadOptions = builder.Configuration.GetSection("VrcxWeb:SnapshotImport").Get<VrcxSnapshotUploadOptions>() ??
    new VrcxSnapshotUploadOptions();
var snapshotImporter = new VrcxSnapshotImporter(new VrcxSnapshotImportOptions(
    readModelOptions.DatabasePath,
    snapshotUploadOptions.StagingDirectory));
var encryptionKeyFile = Environment.GetEnvironmentVariable("VRCX_WEB_SECRET_KEY_FILE");
try
{
    HostSecretValidator.EnsureDistinct(
        authOptions.SetupTokenFile,
        string.IsNullOrWhiteSpace(encryptionKeyFile) ? "/run/secrets/vrcx_secret_key" : encryptionKeyFile);
}
catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine("VRCX Web cannot start: host secret validation failed.");
    return 1;
}
builder.Services.AddSingleton(authOptions);
builder.Services.AddSingleton(readModelOptions);
builder.Services.AddSingleton(workerProcessOptions);
builder.Services.AddSingleton(snapshotUploadOptions);
builder.Services.AddSingleton(snapshotImporter);
builder.Services.AddSingleton<IVrcxSnapshotImporter>(snapshotImporter);
builder.Services.AddSingleton<ISetupTokenProvider, FileSetupTokenProvider>();
builder.Services.AddSingleton<ServerAuthStore>();
builder.Services.AddSingleton<VrcxReadStore>();
builder.Services.AddSingleton<VrcxWorkerHostedService>();
builder.Services.AddSingleton<IVrcxWorkerLifecycle>(services =>
    services.GetRequiredService<VrcxWorkerHostedService>());
builder.Services.AddHostedService(services => services.GetRequiredService<VrcxWorkerHostedService>());
builder.Services.AddSingleton<VrcxSnapshotImportCoordinator>();
builder.Services.AddVrchatWorkerClient(builder.Configuration);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("auth", limiter =>
    {
        limiter.PermitLimit = 10;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
        limiter.AutoReplenishment = true;
    });
    options.AddFixedWindowLimiter(VrchatWorkerEndpoints.RateLimitPolicy, limiter =>
    {
        limiter.PermitLimit = 30;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
        limiter.AutoReplenishment = true;
    });
    options.AddFixedWindowLimiter(VrcxSnapshotUploadEndpoints.RateLimitPolicy, limiter =>
    {
        limiter.PermitLimit = 5;
        limiter.Window = TimeSpan.FromMinutes(10);
        limiter.QueueLimit = 0;
        limiter.AutoReplenishment = true;
    });
});

var app = builder.Build();
var authStore = app.Services.GetRequiredService<ServerAuthStore>();
await authStore.InitializeAsync();

if (issueAdminRecovery)
{
    var recovery = await authStore.CreateAdminRecoveryCodeAsync();
    Console.WriteLine($"VRCX Web administrator recovery code (valid until {recovery.ExpiresAt:O}):");
    Console.WriteLine(recovery.Code);
    Console.WriteLine("Open the VRCX Web sign-in page and use the lost-password form. This code works once.");
    return 0;
}

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.Headers.CacheControl = "no-store";
    }

    await next();
});
app.Use(async (context, next) =>
{
    if (IsUnsafeApiRequest(context.Request) && !HasTrustedBrowserOrigin(context.Request))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(
            new { error = "untrusted_request_origin" },
            context.RequestAborted);
        return;
    }

    await next();
});
app.UseRateLimiter();
app.UseMiddleware<SessionMiddleware>();

var defaultFiles = new DefaultFilesOptions();
defaultFiles.DefaultFileNames.Clear();
defaultFiles.DefaultFileNames.Add("web.html");
app.UseDefaultFiles(defaultFiles);
app.UseStaticFiles();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

var auth = app.MapGroup("/api/v1/auth");

auth.MapGet("/status", async (HttpContext context, CancellationToken cancellationToken) =>
{
    var session = context.GetSession();
    return Results.Ok(new
    {
        bootstrapped = await authStore.IsBootstrappedAsync(cancellationToken),
        authenticated = session is not null,
        device = session is null ? null : new { id = session.Id, name = session.DeviceName },
        expiresAt = session?.ExpiresAt
    });
});

auth.MapPost("/bootstrap", async (
    BootstrapRequest request,
    HttpContext context,
    ISetupTokenProvider setupToken,
    CancellationToken cancellationToken) =>
{
    if (!setupToken.Verify(request.SetupToken))
    {
        return AuthFailure("invalid_setup_token");
    }

    if (!IsValidPassword(request.Password))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["password"] = ["Password must be between 12 and 256 characters."]
        });
    }

    var token = await authStore.BootstrapAdminAsync(
        PasswordHasher.Hash(request.Password!),
        request.DeviceName,
        cancellationToken);
    if (token is null)
    {
        return Results.Conflict(new { error = "already_bootstrapped" });
    }

    SetSessionCookie(context, token);
    return Results.Created("/api/v1/auth/status", SessionResponse(token));
}).RequireRateLimiting("auth");

auth.MapPost("/login", async (
    AdminLoginRequest request,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    var token = await authStore.AuthenticateAdminAsync(
        request.Password ?? string.Empty,
        request.DeviceName,
        cancellationToken);
    if (token is null)
    {
        return AuthFailure("invalid_admin_credentials");
    }
    SetSessionCookie(context, token);
    return Results.Ok(SessionResponse(token));
}).RequireRateLimiting("auth");

auth.MapPost("/recovery/redeem", async (
    AdminRecoveryRequest request,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Code) || request.Code.Length > 128 || !IsValidPassword(request.Password))
    {
        return AuthFailure("invalid_recovery_code");
    }

    var token = await authStore.ResetAdminPasswordWithRecoveryCodeAsync(
        request.Code,
        PasswordHasher.Hash(request.Password!),
        request.DeviceName,
        cancellationToken);
    if (token is null)
    {
        return AuthFailure("invalid_recovery_code");
    }

    SetSessionCookie(context, token);
    return Results.Ok(SessionResponse(token));
}).RequireRateLimiting("auth");

auth.MapPost("/logout", async (HttpContext context, CancellationToken cancellationToken) =>
{
    if (context.GetSession() is { } session)
    {
        await authStore.RevokeSessionAsync(session.Id, cancellationToken);
    }

    ClearSessionCookie(context);
    return Results.NoContent();
});

auth.MapPost("/pairing", async (CancellationToken cancellationToken) =>
{
    var pairing = await authStore.CreatePairingCodeAsync(cancellationToken);
    return Results.Ok(new
    {
        pairingId = pairing.Id,
        code = pairing.Code,
        url = $"/#/?code={Uri.EscapeDataString(pairing.Code)}",
        expiresAt = pairing.ExpiresAt
    });
}).RequireRateLimiting("auth");

auth.MapPost("/pairing/redeem", async (
    PairingRedeemRequest request,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Code) || request.Code.Length > 128)
    {
        return Results.BadRequest(new { error = "invalid_pairing_code" });
    }

    var token = await authStore.RedeemPairingCodeAsync(request.Code, request.DeviceName, cancellationToken);
    if (token is null)
    {
        return AuthFailure("invalid_pairing_code");
    }

    SetSessionCookie(context, token);
    return Results.Ok(SessionResponse(token));
}).RequireRateLimiting("auth");

auth.MapGet("/devices", async (CancellationToken cancellationToken) =>
    Results.Ok(new { items = await authStore.GetSessionsAsync(cancellationToken) }));

auth.MapDelete("/devices/{sessionId}", async (
    string sessionId,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    if (sessionId.Length > 64 || !await authStore.RevokeSessionAsync(sessionId, cancellationToken))
    {
        return Results.NotFound();
    }

    if (string.Equals(context.GetSession()?.Id, sessionId, StringComparison.Ordinal))
    {
        ClearSessionCookie(context);
    }

    return Results.NoContent();
});

var api = app.MapGroup("/api/v1");
api.MapVrchatWorkerAuthApi();
api.MapEntityResourceApi();
api.MapVrcxHistoryReadApi();
api.MapVrcxSnapshotUploadApi();

api.MapGet("/status", (
    VrcxReadStore readStore,
    CancellationToken cancellationToken) => readStore.GetStatusAsync(cancellationToken));

api.MapGet("/friends", (
    [AsParameters] PageQuery query,
    VrcxReadStore readStore,
    CancellationToken cancellationToken) =>
{
    var (offset, limit) = NormalizePageQuery(query.Offset, query.Limit);
    return readStore.GetFriendsAsync(offset, limit, cancellationToken);
});

api.MapGet("/friends/{id}", async (
    string id,
    VrcxReadStore readStore,
    CancellationToken cancellationToken) =>
{
    var friend = await readStore.GetFriendAsync(id, cancellationToken);
    return friend is null ? Results.NotFound() : Results.Ok(friend);
});

api.MapGet("/friends/{id}/history", (
    string id,
    [AsParameters] PageQuery query,
    VrcxReadStore readStore,
    CancellationToken cancellationToken) =>
{
    var (offset, limit) = NormalizePageQuery(query.Offset, query.Limit);
    return readStore.GetFriendHistoryAsync(id, offset, limit, cancellationToken);
});

api.MapGet("/history/friends", (
    [AsParameters] PageQuery query,
    VrcxReadStore readStore,
    CancellationToken cancellationToken) =>
{
    var (offset, limit) = NormalizePageQuery(query.Offset, query.Limit);
    return readStore.GetFriendHistoryAsync(null, offset, limit, cancellationToken);
});

api.MapGet("/feed", (
    [AsParameters] PageQuery query,
    VrcxReadStore readStore,
    CancellationToken cancellationToken) =>
{
    var (offset, limit) = NormalizePageQuery(query.Offset, query.Limit);
    return readStore.GetFeedAsync(offset, limit, cancellationToken);
});

api.MapGet("/history/game-log", (
    [AsParameters] PageQuery query,
    VrcxReadStore readStore,
    CancellationToken cancellationToken) =>
{
    var (offset, limit) = NormalizePageQuery(query.Offset, query.Limit);
    return readStore.GetGameLogAsync(offset, limit, cancellationToken);
});

api.MapGet("/notifications", (
    [AsParameters] PageQuery query,
    VrcxReadStore readStore,
    CancellationToken cancellationToken) =>
{
    var (offset, limit) = NormalizePageQuery(query.Offset, query.Limit);
    return readStore.GetNotificationsAsync(offset, limit, cancellationToken);
});

api.MapGet("/favorites", (
    [AsParameters] PageQuery query,
    VrcxReadStore readStore,
    CancellationToken cancellationToken) =>
{
    var (offset, limit) = NormalizePageQuery(query.Offset, query.Limit);
    return readStore.GetFavoritesAsync(offset, limit, cancellationToken);
});

api.MapGet("/favorites/groups", (
    VrcxReadStore readStore,
    CancellationToken cancellationToken) => readStore.GetFavoriteGroupsAsync(cancellationToken));

api.MapGet("/analytics/summary", (
    VrcxReadStore readStore,
    CancellationToken cancellationToken) => readStore.GetAnalyticsAsync(cancellationToken));

api.MapGet("/analytics/instance-activity/dates", async (
    VrchatWorkerClient worker,
    CancellationToken cancellationToken) =>
{
    var result = await worker.GetDateOfInstanceActivityAsync(cancellationToken);
    return result is null ? AnalyticsWorkerUnavailable() : Results.Ok(result);
}).RequireRateLimiting(VrchatWorkerEndpoints.RateLimitPolicy);

api.MapGet("/analytics/instance-activity", async (
    [AsParameters] InstanceActivityQuery query,
    VrchatWorkerClient worker,
    CancellationToken cancellationToken) =>
{
    if (!VrchatWorkerClient.IsValidInstanceActivityRange(query.StartDate, query.EndDate))
    {
        return Results.BadRequest(new { error = "invalid_date_range" });
    }
    var result = await worker.GetInstanceActivityAsync(query.StartDate!, query.EndDate!, cancellationToken);
    return result is null ? AnalyticsWorkerUnavailable() : Results.Ok(result);
}).RequireRateLimiting(VrchatWorkerEndpoints.RateLimitPolicy);

api.MapGet("/analytics/hot-worlds", async (
    [AsParameters] HotWorldQuery query,
    VrchatWorkerClient worker,
    CancellationToken cancellationToken) =>
{
    if (!VrchatWorkerClient.IsValidAnalyticsDays(query.Days) || query.Limit is < 1 or > 100)
    {
        return Results.BadRequest(new { error = "invalid_analytics_query" });
    }
    var result = await worker.GetHotWorldsAsync(query.Days, query.Limit, cancellationToken);
    return result is null ? AnalyticsWorkerUnavailable() : Results.Ok(result);
}).RequireRateLimiting(VrchatWorkerEndpoints.RateLimitPolicy);

api.MapGet("/analytics/hot-worlds/{worldId}/friends", async (
    string worldId,
    [AsParameters] HotWorldFriendQuery query,
    VrchatWorkerClient worker,
    CancellationToken cancellationToken) =>
{
    if (!VrchatWorkerClient.IsValidEntityId("world", worldId) ||
        !VrchatWorkerClient.IsValidAnalyticsDays(query.Days))
    {
        return Results.BadRequest(new { error = "invalid_analytics_query" });
    }
    var result = await worker.GetHotWorldFriendDetailAsync(worldId, query.Days, cancellationToken);
    return result is null ? AnalyticsWorkerUnavailable() : Results.Ok(result);
}).RequireRateLimiting(VrchatWorkerEndpoints.RateLimitPolicy);

api.MapGet("/analytics/mutual-graph/snapshot", async (
    VrchatWorkerClient worker,
    CancellationToken cancellationToken) =>
{
    var result = await worker.GetMutualGraphSnapshotAsync(cancellationToken);
    return result is null ? AnalyticsWorkerUnavailable() : Results.Ok(result);
}).RequireRateLimiting(VrchatWorkerEndpoints.RateLimitPolicy);

api.MapGet("/analytics/mutual-graph/meta", async (
    VrchatWorkerClient worker,
    CancellationToken cancellationToken) =>
{
    var result = await worker.GetMutualGraphMetaAsync(cancellationToken);
    return result is null ? AnalyticsWorkerUnavailable() : Results.Ok(result);
}).RequireRateLimiting(VrchatWorkerEndpoints.RateLimitPolicy);

api.MapGet("/memos/{entityType}/{id}", async (
    string entityType,
    string id,
    VrcxReadStore readStore,
    CancellationToken cancellationToken) =>
{
    var memo = await readStore.GetMemoAsync(entityType, id, cancellationToken);
    return memo is null ? Results.NotFound() : Results.Ok(memo);
});

api.MapGet("/search", async (
    [AsParameters] SearchQuery query,
    VrchatWorkerClient worker,
    VrcxReadStore readStore,
    CancellationToken cancellationToken) =>
{
    var options = ToWorkerSearchOptions(query);
    if (!IsValidSearchQuery(query, options))
    {
        return Results.BadRequest(new { error = "invalid_search" });
    }

    var (offset, limit) = NormalizePageQuery(query.Offset, query.Limit);
    var searchText = (query.Q ?? string.Empty).Trim();
    var result = await worker.SearchAsync(query.Type!, searchText, offset, limit, options, cancellationToken) ??
        await readStore.SearchAsync(query.Type!, searchText, offset, limit, cancellationToken);
    return Results.Ok(result);
}).RequireRateLimiting(VrchatWorkerEndpoints.RateLimitPolicy);

api.MapGet("/users/{id}", (
    string id,
    VrchatWorkerClient worker,
    VrcxReadStore readStore,
    CancellationToken cancellationToken) => GetEntityResultAsync("user", id, worker, readStore, cancellationToken))
    .RequireRateLimiting(VrchatWorkerEndpoints.RateLimitPolicy);
api.MapGet("/worlds/{id}", (
    string id,
    VrchatWorkerClient worker,
    VrcxReadStore readStore,
    CancellationToken cancellationToken) => GetEntityResultAsync("world", id, worker, readStore, cancellationToken))
    .RequireRateLimiting(VrchatWorkerEndpoints.RateLimitPolicy);
api.MapGet("/avatars/{id}", (
    string id,
    VrchatWorkerClient worker,
    VrcxReadStore readStore,
    CancellationToken cancellationToken) => GetEntityResultAsync("avatar", id, worker, readStore, cancellationToken))
    .RequireRateLimiting(VrchatWorkerEndpoints.RateLimitPolicy);
api.MapGet("/groups/{id}", (
    string id,
    VrchatWorkerClient worker,
    VrcxReadStore readStore,
    CancellationToken cancellationToken) => GetEntityResultAsync("group", id, worker, readStore, cancellationToken))
    .RequireRateLimiting(VrchatWorkerEndpoints.RateLimitPolicy);

api.MapGet("/events", StreamEventsAsync);

app.MapFallback(async context =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    var webRoot = app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot");
    var webFile = Path.Combine(webRoot, "web.html");
    if (!File.Exists(webFile))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.SendFileAsync(webFile, context.RequestAborted);
});

await app.RunAsync();
return 0;

static bool IsValidPassword(string? password) => password is { Length: >= 12 and <= 256 };

static bool IsUnsafeApiRequest(HttpRequest request) =>
    request.Path.StartsWithSegments("/api/v1") &&
    !HttpMethods.IsGet(request.Method) &&
    !HttpMethods.IsHead(request.Method) &&
    !HttpMethods.IsOptions(request.Method);

static bool HasTrustedBrowserOrigin(HttpRequest request)
{
    var fetchSite = request.Headers["Sec-Fetch-Site"].ToString();
    if (string.Equals(fetchSite, "cross-site", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    var originHeaders = request.Headers.Origin;
    if (originHeaders.Count == 0)
    {
        // Non-browser clients do not normally send Origin. Browser POST/fetch requests do, and
        // are checked below so another same-site Tailscale host cannot rely on SameSite cookies.
        return true;
    }
    if (originHeaders.Count != 1 ||
        !Uri.TryCreate(originHeaders[0], UriKind.Absolute, out var origin) ||
        !string.IsNullOrEmpty(origin.UserInfo) ||
        !string.Equals(origin.AbsolutePath, "/", StringComparison.Ordinal) ||
        !string.IsNullOrEmpty(origin.Query) ||
        !string.IsNullOrEmpty(origin.Fragment))
    {
        return false;
    }

    var forwardedProto = request.Headers["X-Forwarded-Proto"];
    var expectedScheme = forwardedProto.Count == 1 ? forwardedProto[0] : request.Scheme;
    return expectedScheme is not null &&
        (string.Equals(expectedScheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(expectedScheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)) &&
        string.Equals(origin.Scheme, expectedScheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(origin.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);
}

static IResult AuthFailure(string error) => Results.Json(
    new { error },
    statusCode: StatusCodes.Status401Unauthorized);

static bool IsValidSearchQuery(SearchQuery query, VrchatWorkerSearchOptions options) =>
    query.Type is not null &&
    VrchatWorkerClient.IsValidSearchRequest(query.Type, query.Q, options);

static VrchatWorkerSearchOptions ToWorkerSearchOptions(SearchQuery query) => new(
    query.WorldOption,
    query.Sort,
    query.Order,
    query.Featured,
    query.Tag,
    query.User,
    query.ReleaseStatus,
    query.Fuzzy,
    query.CustomFields);

static (int Offset, int Limit) NormalizePageQuery(int offset, int limit) =>
    (Math.Clamp(offset, 0, 10_000), Math.Clamp(limit <= 0 ? 50 : limit, 1, 200));

static IResult AnalyticsWorkerUnavailable() => Results.Problem(
    statusCode: StatusCodes.Status503ServiceUnavailable,
    title: "VRCX analytics worker is unavailable.");

static async Task<IResult> GetEntityResultAsync(
    string type,
    string id,
    VrchatWorkerClient worker,
    VrcxReadStore readStore,
    CancellationToken cancellationToken)
{
    if (!VrchatWorkerClient.IsValidEntityId(type, id))
    {
        return Results.BadRequest(new { error = "invalid_entity_id" });
    }

    var entity = await worker.GetEntityAsync(type, id, cancellationToken) ??
        await readStore.GetEntityAsync(type, id, cancellationToken);
    return entity is null ? Results.NotFound() : Results.Ok(entity);
}

static async Task StreamEventsAsync(
    HttpContext context,
    VrcxReadStore readStore,
    ServerAuthStore authStore,
    CancellationToken cancellationToken)
{
    var session = await context.RevalidateSessionAsync(authStore, cancellationToken);
    if (session is null)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    SseWriter.Prepare(context.Response);
    var status = await readStore.GetStatusAsync(cancellationToken);
    await SseWriter.WriteEventAsync(context.Response, "status", status, cancellationToken);
    await SseWriter.WriteEventAsync(context.Response, "pipeline", status.Pipeline, cancellationToken);

    var lastEventIdHeader = context.Request.Headers["Last-Event-ID"].ToString();
    var cursor = long.TryParse(lastEventIdHeader, out var requestedCursor) && requestedCursor >= 0
        ? requestedCursor
        : await readStore.GetLatestEventSequenceAsync(cancellationToken);
    var nextSessionCheck = DateTimeOffset.UtcNow.AddSeconds(15);

    try
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var events = await readStore.GetEventsAfterAsync(cursor, 100, cancellationToken);
            foreach (var projectionEvent in events)
            {
                await SseWriter.WriteEventAsync(
                    context.Response,
                    projectionEvent.Type,
                    new
                    {
                        sequence = projectionEvent.Sequence,
                        occurredAt = projectionEvent.OccurredAt,
                        resource = projectionEvent.Resource,
                        payload = projectionEvent.Payload
                    },
                    projectionEvent.Sequence,
                    cancellationToken);
                cursor = projectionEvent.Sequence;
            }

            if (DateTimeOffset.UtcNow >= nextSessionCheck)
            {
                session = await context.RevalidateSessionAsync(authStore, cancellationToken);
                if (session is null)
                {
                    return;
                }

                await authStore.TouchSessionAsync(session.Id, cancellationToken);
                await SseWriter.WriteHeartbeatAsync(context.Response, cancellationToken);
                nextSessionCheck = DateTimeOffset.UtcNow.AddSeconds(15);
            }

            if (events.Count < 100)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        // Normal browser disconnect or server shutdown.
    }
    catch (IOException)
    {
        // A disconnected EventSource may surface as an I/O error before cancellation propagates.
    }
}

static object SessionResponse(SessionToken token) => new
{
    ok = true,
    device = new { id = token.Session.Id, name = token.Session.DeviceName },
    expiresAt = token.Session.ExpiresAt
};

static void SetSessionCookie(HttpContext context, SessionToken token)
{
    context.Response.Cookies.Append(SessionContext.CookieName, token.Token, new CookieOptions
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        IsEssential = true,
        Path = "/",
        Expires = token.Session.ExpiresAt,
        MaxAge = token.Session.ExpiresAt - DateTimeOffset.UtcNow
    });
}

static void ClearSessionCookie(HttpContext context)
{
    context.Response.Cookies.Delete(SessionContext.CookieName, new CookieOptions
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        IsEssential = true,
        Path = "/"
    });
}

public sealed record BootstrapRequest(string? Password, string? SetupToken, string? DeviceName);

public sealed record AdminLoginRequest(string? Password, string? DeviceName);

public sealed record AdminRecoveryRequest(string? Code, string? Password, string? DeviceName);

public sealed record PairingRedeemRequest(string? Code, string? DeviceName);

public sealed record PageQuery(int Offset = 0, int Limit = 50);

public sealed record SearchQuery(
    string? Type = null,
    string? Q = null,
    int Offset = 0,
    int Limit = 50,
    string? WorldOption = null,
    string? Sort = null,
    string? Order = null,
    bool? Featured = null,
    string? Tag = null,
    string? User = null,
    string? ReleaseStatus = null,
    bool? Fuzzy = null,
    string? CustomFields = null);

public sealed record InstanceActivityQuery(string? StartDate = null, string? EndDate = null);

public sealed record HotWorldQuery(int Days = 30, int Limit = 30);

public sealed record HotWorldFriendQuery(int Days = 30);

public partial class Program;
