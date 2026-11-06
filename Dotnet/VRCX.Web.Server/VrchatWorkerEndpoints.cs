namespace VRCX.Web.Server;

internal static class VrchatWorkerEndpoints
{
    public const string RateLimitPolicy = "vrchat-worker-auth";
    public const string StatusRateLimitPolicy = "vrchat-worker-status";
    public const string ReadRateLimitPolicy = "vrchat-worker-read";

    private const int MaximumUsernameLength = 256;
    private const int MaximumPasswordLength = 1024;
    private const int MaximumCodeLength = 128;

    public static RouteGroupBuilder MapVrchatWorkerAuthApi(this RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/vrchat/auth");

        auth.MapGet("/status", async (
            VrchatWorkerClient worker,
            CancellationToken cancellationToken) => ToResult(await worker.GetStatusAsync(cancellationToken)))
            .RequireRateLimiting(StatusRateLimitPolicy);

        auth.MapPost("/login", async (
            VrchatLoginRequest request,
            VrchatWorkerClient worker,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Username) || request.Username.Length > MaximumUsernameLength)
            {
                return Invalid("username", $"Username must be between 1 and {MaximumUsernameLength} characters.");
            }
            if (string.IsNullOrEmpty(request.Password) || request.Password.Length > MaximumPasswordLength)
            {
                return Invalid("password", $"Password must be between 1 and {MaximumPasswordLength} characters.");
            }

            return ToResult(await worker.LoginAsync(request, cancellationToken));
        }).RequireRateLimiting(RateLimitPolicy);

        auth.MapPost("/two-factor", async (
            VrchatTwoFactorRequest request,
            VrchatWorkerClient worker,
            CancellationToken cancellationToken) =>
        {
            if (request.Kind is null)
            {
                return Invalid("kind", "A supported two-factor kind is required.");
            }
            if (string.IsNullOrWhiteSpace(request.Code) || request.Code.Length > MaximumCodeLength)
            {
                return Invalid("code", $"Code must be between 1 and {MaximumCodeLength} characters.");
            }

            var command = request with { Code = request.Code.Trim() };
            return ToResult(await worker.SubmitTwoFactorAsync(command, cancellationToken));
        }).RequireRateLimiting(RateLimitPolicy);

        auth.MapPost("/email-otp/resend", async (
            VrchatWorkerClient worker,
            CancellationToken cancellationToken) => ToResult(await worker.ResendEmailOtpAsync(cancellationToken)))
            .RequireRateLimiting(RateLimitPolicy);

        auth.MapPost("/logout", async (
            VrchatWorkerClient worker,
            CancellationToken cancellationToken) => ToResult(await worker.LogoutAsync(cancellationToken)))
            .RequireRateLimiting(RateLimitPolicy);

        return api;
    }

    private static IResult ToResult(VrchatWorkerAuthResult result)
    {
        if (result.Response is not null) return Results.Ok(result.Response);
        return result.Failure switch
        {
            VrchatWorkerAuthFailure.Unavailable when result.Reason is "database_initialization_failed" =>
                Results.Json(new { error = "vrchat_worker_unavailable", reason = "database_initialization_failed" },
                    statusCode: StatusCodes.Status503ServiceUnavailable),
            VrchatWorkerAuthFailure.Unavailable => Results.Json(new { error = "vrchat_worker_unavailable" },
                statusCode: StatusCodes.Status503ServiceUnavailable),
            VrchatWorkerAuthFailure.Timeout => Results.Json(new { error = "vrchat_worker_timeout" },
                statusCode: StatusCodes.Status504GatewayTimeout),
            _ => Results.Json(new { error = "vrchat_worker_bad_response" }, statusCode: StatusCodes.Status502BadGateway)
        };
    }

    private static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
