namespace VRCX.Web.Auth;

public sealed class ServerAuthOptions
{
    public string DataDirectory { get; set; } = "/data";

    /// <summary>
    /// Docker secret used only to prove possession of the host during first-time setup.
    /// This is deliberately not the key used to encrypt the VRChat session.
    /// </summary>
    public string SetupTokenFile { get; set; } = "/run/secrets/vrcx_setup_token";

    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromDays(30);

    public TimeSpan PairingLifetime { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan RecoveryCodeLifetime { get; set; } = TimeSpan.FromMinutes(10);
}

public sealed record PasswordCredential(string Salt, string Hash, int Iterations)
{
    public const int CurrentIterations = 310_000;
}

public sealed record SessionRecord(
    string Id,
    string DeviceName,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset? LastSeenAt);

public sealed record SessionToken(string Token, SessionRecord Session);

public sealed record PairingCode(string Id, string Code, DateTimeOffset ExpiresAt);

public sealed record AdminRecoveryCode(string Code, DateTimeOffset ExpiresAt);
