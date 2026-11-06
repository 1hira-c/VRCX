using System.Buffers;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace VRCX.Transport;

/// <summary>
/// Protects the VRChat cookie snapshot stored by the headless VRCX worker.
/// This envelope is deliberately server-only; the desktop persistence format is unchanged.
/// </summary>
public sealed class ServerCookieProtector : IDisposable
{
    public const string EnvelopePrefix = "vrcx-web-cookie-v1:";
    private const int KeyLength = 32;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int MaximumKeyFileBytes = 1024;
    private const int MaximumEnvelopeCharacters = 4 * 1024 * 1024;
    private static readonly byte[] AdditionalData = Encoding.UTF8.GetBytes("VRCX Web cookie store v1");
    private readonly object syncRoot = new();
    private readonly byte[] key;
    private bool disposed;

    public ServerCookieProtector(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeyLength)
        {
            throw new ArgumentException("The VRCX Web secret key must contain exactly 32 bytes.", nameof(key));
        }

        this.key = key.ToArray();
    }

    public static ServerCookieProtector FromBase64File(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("The VRCX Web secret key is not available.");
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
            if (stream.Length is <= 0 or > MaximumKeyFileBytes)
            {
                throw new InvalidOperationException("The VRCX Web secret key file has an invalid size.");
            }

            encoded = new byte[checked((int)stream.Length)];
            stream.ReadExactly(encoded);
            var encodedKey = TrimAsciiWhiteSpace(encoded);
            if (encodedKey.IsEmpty)
            {
                throw new InvalidOperationException("The VRCX Web secret key is not valid base64.");
            }

            decoded = new byte[Base64.GetMaxDecodedFromUtf8Length(encodedKey.Length)];
            var status = Base64.DecodeFromUtf8(encodedKey, decoded, out var consumed, out var written);
            if (status != OperationStatus.Done || consumed != encodedKey.Length)
            {
                throw new InvalidOperationException("The VRCX Web secret key is not valid base64.");
            }

            return new ServerCookieProtector(decoded.AsSpan(0, written));
        }
        catch (FileNotFoundException exception)
        {
            throw new InvalidOperationException("The VRCX Web secret key is not available.", exception);
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

    private static bool IsAsciiWhiteSpace(byte value) =>
        value is 0x20 or 0x09 or 0x0D or 0x0A;

    public static bool IsProtectedEnvelope(string? value) =>
        value?.StartsWith(EnvelopePrefix, StringComparison.Ordinal) == true;

    public string Protect(ReadOnlySpan<byte> plaintext)
    {
        using var aes = CreateAesGcm();

        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagLength];
        var envelope = new byte[NonceLength + TagLength + ciphertext.Length];
        try
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag, AdditionalData);
            nonce.CopyTo(envelope, 0);
            tag.CopyTo(envelope, NonceLength);
            ciphertext.CopyTo(envelope, NonceLength + TagLength);
            return EnvelopePrefix + Convert.ToBase64String(envelope);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
            CryptographicOperations.ZeroMemory(envelope);
        }
    }

    public byte[] Unprotect(string envelope)
    {
        using var aes = CreateAesGcm();
        ArgumentNullException.ThrowIfNull(envelope);
        if (!IsProtectedEnvelope(envelope) || envelope.Length > MaximumEnvelopeCharacters)
        {
            throw new CryptographicException("The cookie envelope is not a supported encrypted value.");
        }

        byte[] protectedBytes;
        try
        {
            protectedBytes = Convert.FromBase64String(envelope[EnvelopePrefix.Length..]);
        }
        catch (FormatException exception)
        {
            throw new CryptographicException("The cookie envelope is malformed.", exception);
        }

        if (protectedBytes.Length < NonceLength + TagLength)
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            throw new CryptographicException("The cookie envelope is truncated.");
        }

        var plaintext = new byte[protectedBytes.Length - NonceLength - TagLength];
        try
        {
            aes.Decrypt(
                protectedBytes.AsSpan(0, NonceLength),
                protectedBytes.AsSpan(NonceLength + TagLength),
                protectedBytes.AsSpan(NonceLength, TagLength),
                plaintext,
                AdditionalData);
            return plaintext;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    public void Dispose()
    {
        lock (syncRoot)
        {
            if (disposed)
            {
                return;
            }

            CryptographicOperations.ZeroMemory(key);
            disposed = true;
        }
    }

    private AesGcm CreateAesGcm()
    {
        lock (syncRoot)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return new AesGcm(key, TagLength);
        }
    }
}
