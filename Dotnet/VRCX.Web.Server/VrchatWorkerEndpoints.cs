namespace VRCX.Web.Server;

internal static class VrchatWorkerEndpoints
{
    public const string RateLimitPolicy = "vrchat-worker-auth";

    private const int MaximumUsernameLength = 256;
    private const int MaximumPasswordLength = 1024;
    private const int MaximumCodeLength = 128;

    public static RouteGroupBuilder MapVrchatWorkerAuthApi(this RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/vrchat/auth").RequireRateLimiting(RateLimitPolicy);

        auth.MapGet("/status", async (
            VrchatWorkerClient worker,
            CancellationToken cancellationToken) => ToResult(await worker.GetStatusAsync(cancellationToken)));

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
        });

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
        });

        auth.MapPost("/email-otp/resend", async (
            VrchatWorkerClient worker,
            CancellationToken cancellationToken) => ToResult(await worker.ResendEmailOtpAsync(cancellationToken)));

        auth.MapPost("/logout", async (
            VrchatWorkerClient worker,
            CancellationToken cancellationToken) => ToResult(await worker.LogoutAsync(cancellationToken)));

        return api;
    }

    private static IResult ToResult(VrchatAuthStatusResponse? response) => response is null
        ? Results.Json(new { error = "vrchat_worker_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable)
        : Results.Ok(response);

    private static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
