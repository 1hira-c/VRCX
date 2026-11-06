using System.Buffers;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace VRCX.Web.Auth;

public static class PasswordHasher
{
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int MaximumAcceptedIterations = 2_000_000;
    private const int MaximumPasswordCharacters = 256;

    public static PasswordCredential Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(password.Length, MaximumPasswordCharacters);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            PasswordCredential.CurrentIterations,
            HashAlgorithmName.SHA512,
            HashBytes);
        try
        {
            return new PasswordCredential(
                Convert.ToBase64String(salt),
                Convert.ToBase64String(hash),
                PasswordCredential.CurrentIterations);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    public static bool Verify(string password, PasswordCredential credential)
    {
        if (string.IsNullOrEmpty(password) || password.Length > MaximumPasswordCharacters ||
            credential.Iterations is <= 0 or > MaximumAcceptedIterations)
        {
            return false;
        }

        byte[]? salt = null;
        byte[]? expected = null;
        byte[]? actual = null;
        try
        {
            salt = Convert.FromBase64String(credential.Salt);
            expected = Convert.FromBase64String(credential.Hash);
            if (salt.Length != SaltBytes || expected.Length != HashBytes)
            {
                return false;
            }

            actual = Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                credential.Iterations,
                HashAlgorithmName.SHA512,
                HashBytes);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentOutOfRangeException)
        {
            return false;
        }
        finally
        {
            if (salt is not null)
            {
                CryptographicOperations.ZeroMemory(salt);
            }

            if (expected is not null)
            {
                CryptographicOperations.ZeroMemory(expected);
            }

            if (actual is not null)
            {
                CryptographicOperations.ZeroMemory(actual);
            }
        }
    }
}

public interface ISetupTokenProvider
{
    bool Verify(string? submittedToken);
}

/// <summary>
/// Verifies the one-time host-possession secret required for initial WebUI setup.
/// The value is never returned through an HTTP endpoint.
/// </summary>
public sealed class FileSetupTokenProvider(ServerAuthOptions options) : ISetupTokenProvider
{
    private readonly Lazy<byte[]> token = new(() => Load(options.SetupTokenFile));

    public bool Verify(string? submittedToken)
    {
        var expected = token.Value;
        var supplied = new byte[expected.Length];
        try
        {
            var validEncodingAndLength = false;
            try
            {
                var decoded = Convert.FromBase64String(submittedToken is { Length: <= 256 } ? submittedToken.Trim() : string.Empty);
                try
                {
                    if (decoded.Length == expected.Length)
                    {
                        decoded.CopyTo(supplied, 0);
                        validEncodingAndLength = true;
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(decoded);
                }
            }
            catch (FormatException)
            {
                // Keep the fixed-time comparison below for malformed input too.
            }

            return CryptographicOperations.FixedTimeEquals(expected, supplied) & validEncodingAndLength;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(supplied);
        }
    }

    private static byte[] Load(string path)
        => HostSecretFiles.ReadBase64Key(path, "setup-token");
}

public static class HostSecretValidator
{
    public static void EnsureDistinct(string setupTokenFile, string encryptionKeyFile)
    {
        byte[]? setupToken = null;
        byte[]? encryptionKey = null;
        try
        {
            setupToken = HostSecretFiles.ReadBase64Key(setupTokenFile, "setup-token");
            encryptionKey = HostSecretFiles.ReadBase64Key(encryptionKeyFile, "encryption-key");
            if (CryptographicOperations.FixedTimeEquals(setupToken, encryptionKey))
            {
                throw new InvalidOperationException(
                    "The VRCX Web setup-token and encryption-key secrets must be different values.");
            }
        }
        finally
        {
            if (setupToken is not null)
            {
                CryptographicOperations.ZeroMemory(setupToken);
            }
            if (encryptionKey is not null)
            {
                CryptographicOperations.ZeroMemory(encryptionKey);
            }
        }
    }
}

internal static class HostSecretFiles
{
    private const int KeyLength = 32;
    private const int MaximumFileBytes = 1024;

    public static byte[] ReadBase64Key(string path, string purpose)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException($"The VRCX Web {purpose} secret is not available.");
        }

        byte[]? encoded = null;
        byte[]? decoded = null;
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128,
                FileOptions.SequentialScan);
            if (stream.Length is <= 0 or > MaximumFileBytes)
            {
                throw new InvalidOperationException($"The VRCX Web {purpose} secret file has an invalid size.");
            }

            encoded = new byte[checked((int)stream.Length)];
            stream.ReadExactly(encoded);
            var encodedKey = TrimAsciiWhiteSpace(encoded);
            decoded = new byte[Base64.GetMaxDecodedFromUtf8Length(encodedKey.Length)];
            var status = Base64.DecodeFromUtf8(encodedKey, decoded, out var consumed, out var written);
            if (status != OperationStatus.Done || consumed != encodedKey.Length || written != KeyLength)
            {
                throw new InvalidOperationException(
                    $"The VRCX Web {purpose} secret must be base64 encoded and decode to exactly {KeyLength} bytes.");
            }

            return decoded.AsSpan(0, written).ToArray();
        }
        catch (FileNotFoundException exception)
        {
            throw new InvalidOperationException($"The VRCX Web {purpose} secret is not available.", exception);
        }
        finally
        {
            if (encoded is not null)
            {
                CryptographicOperations.ZeroMemory(encoded);
            }
            if (decoded is not null)
            {
                CryptographicOperations.ZeroMemory(decoded);
            }
        }
    }

    private static ReadOnlySpan<byte> TrimAsciiWhiteSpace(ReadOnlySpan<byte> value)
    {
        while (!value.IsEmpty && IsAsciiWhiteSpace(value[0]))
        {
            value = value[1..];
        }
        while (!value.IsEmpty && IsAsciiWhiteSpace(value[^1]))
        {
            value = value[..^1];
        }
        return value;
    }

    private static bool IsAsciiWhiteSpace(byte value) => value is 0x20 or 0x09 or 0x0D or 0x0A;
}

public static class TokenGenerator
{
    public static string NewToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        try
        {
            return Convert.ToBase64String(bytes)
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public static string Hash(string token)
    {
        var bytes = Encoding.UTF8.GetBytes(token);
        try
        {
            return Convert.ToHexString(SHA256.HashData(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
