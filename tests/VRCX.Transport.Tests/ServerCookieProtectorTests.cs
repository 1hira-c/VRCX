using System.Security.Cryptography;
using System.Text;
using VRCX.Transport;
using Xunit;

namespace VRCX.Transport.Tests;

public sealed class ServerCookieProtectorTests
{
    [Fact]
    public void ProtectRoundTripsWithAUniqueAuthenticatedEnvelope()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var plaintext = Encoding.UTF8.GetBytes("[{\"name\":\"auth\",\"value\":\"secret-cookie\"}]");
        try
        {
            using var protector = new ServerCookieProtector(key);
            var first = protector.Protect(plaintext);
            var second = protector.Protect(plaintext);

            Assert.StartsWith(ServerCookieProtector.EnvelopePrefix, first, StringComparison.Ordinal);
            Assert.NotEqual(first, second);
            Assert.DoesNotContain("secret-cookie", first, StringComparison.Ordinal);
            Assert.Equal(plaintext, protector.Unprotect(first));
            Assert.Equal(plaintext, protector.Unprotect(second));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    [Fact]
    public void UnprotectRejectsPlaintextTamperingAndWrongKeys()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var wrongKey = RandomNumberGenerator.GetBytes(32);
        try
        {
            using var protector = new ServerCookieProtector(key);
            using var wrongProtector = new ServerCookieProtector(wrongKey);
            var envelope = protector.Protect("cookie"u8);
            var last = envelope[^1];
            var tampered = envelope[..^1] + (last == 'A' ? 'B' : 'A');

            Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect("Y29va2ll"));
            Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(tampered));
            Assert.ThrowsAny<CryptographicException>(() => wrongProtector.Unprotect(envelope));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(wrongKey);
        }
    }

    [Fact]
    public void Base64FileRequiresExactlyThirtyTwoBytes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vrcx-cookie-protector-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var keyPath = Path.Combine(directory, "key");
            File.WriteAllText(keyPath, Convert.ToBase64String(RandomNumberGenerator.GetBytes(31)));
            Assert.Throws<ArgumentException>(() => ServerCookieProtector.FromBase64File(keyPath));

            var key = RandomNumberGenerator.GetBytes(32);
            try
            {
                File.WriteAllText(keyPath, Convert.ToBase64String(key));
                using var protector = ServerCookieProtector.FromBase64File(keyPath);
                Assert.Equal("cookie"u8.ToArray(), protector.Unprotect(protector.Protect("cookie"u8)));

                File.WriteAllText(keyPath, " \r\n" + Convert.ToBase64String(key) + "\n");
                using var whitespaceProtector = ServerCookieProtector.FromBase64File(keyPath);
                Assert.Equal(
                    "cookie"u8.ToArray(),
                    whitespaceProtector.Unprotect(whitespaceProtector.Protect("cookie"u8)));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void KeyFilesAreBoundedAndDisposedProtectorsCannotBeUsed()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vrcx-cookie-protector-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var keyPath = Path.Combine(directory, "key");
            File.WriteAllText(keyPath, "not-base64");
            Assert.Throws<InvalidOperationException>(() => ServerCookieProtector.FromBase64File(keyPath));

            File.WriteAllBytes(keyPath, new byte[1025]);
            Assert.Throws<InvalidOperationException>(() => ServerCookieProtector.FromBase64File(keyPath));

            var key = RandomNumberGenerator.GetBytes(32);
            try
            {
                var protector = new ServerCookieProtector(key);
                protector.Dispose();
                Assert.Throws<ObjectDisposedException>(() => protector.Protect("cookie"u8));
                Assert.Throws<ObjectDisposedException>(() => protector.Unprotect(
                    ServerCookieProtector.EnvelopePrefix + "AA=="));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
