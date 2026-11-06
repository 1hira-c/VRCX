using System.Text.Json;
using VRCX.Web.Auth;

namespace VRCX.Web.Server;

public static class SessionContext
{
    public const string CookieName = "vrcx_session";
    private const string SessionItemName = "vrcx.web.auth.session";
    private const string SessionTokenHashItemName = "vrcx.web.auth.session-token-hash";

    public static SessionRecord? GetSession(this HttpContext context) =>
        context.Items.TryGetValue(SessionItemName, out var value) ? value as SessionRecord : null;

    public static string? GetSessionTokenHash(this HttpContext context) =>
        context.Items.TryGetValue(SessionTokenHashItemName, out var value) ? value as string : null;

    public static void SetSession(this HttpContext context, SessionRecord session, string tokenHash)
    {
        context.Items[SessionItemName] = session;
        context.Items[SessionTokenHashItemName] = tokenHash;
    }

    public static Task<SessionRecord?> RevalidateSessionAsync(
        this HttpContext context,
        ServerAuthStore store,
        CancellationToken cancellationToken = default)
    {
        var tokenHash = context.GetSessionTokenHash();
        return string.IsNullOrWhiteSpace(tokenHash)
            ? Task.FromResult<SessionRecord?>(null)
            : store.FindActiveSessionAsync(tokenHash, cancellationToken);
    }
}

public sealed class SessionMiddleware(RequestDelegate next)
{
    private static readonly PathString ApiPrefix = new("/api/v1");

    public async Task InvokeAsync(HttpContext context, ServerAuthStore store)
    {
        if (context.Request.Cookies.TryGetValue(SessionContext.CookieName, out var token) &&
            !string.IsNullOrWhiteSpace(token) && token.Length <= 128)
        {
            var tokenHash = TokenGenerator.Hash(token);
            var session = await store.FindActiveSessionAsync(tokenHash, context.RequestAborted);
            if (session is not null)
            {
                context.SetSession(session, tokenHash);
                await store.TouchSessionAsync(session.Id, context.RequestAborted);
            }
        }

        if (context.Request.Path.StartsWithSegments(ApiPrefix) &&
            !IsAnonymousEndpoint(context.Request.Path) &&
            context.GetSession() is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(
                context.Response.Body,
                new { error = "authentication_required" },
                cancellationToken: context.RequestAborted);
            return;
        }

        await next(context);
    }

    private static bool IsAnonymousEndpoint(PathString path) =>
        path.Equals("/api/v1/auth/status") ||
        path.Equals("/api/v1/auth/bootstrap") ||
        path.Equals("/api/v1/auth/login") ||
        path.Equals("/api/v1/auth/recovery/redeem") ||
        path.Equals("/api/v1/auth/pairing/redeem");
}
