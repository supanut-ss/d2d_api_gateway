using System.Security.Cryptography;
using System.Text;

namespace D2D.Infrastructure.Line;

/// <summary>x-line-signature = Base64(HMAC-SHA256(channelSecret, rawBody)).</summary>
public static class LineSignature
{
    public static string Sign(string channelSecret, ReadOnlySpan<byte> body) =>
        Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(channelSecret), body));

    public static bool IsValid(string channelSecret, ReadOnlySpan<byte> body, string? signature)
    {
        if (string.IsNullOrEmpty(signature)) return false;

        Span<byte> provided = stackalloc byte[64];
        if (!Convert.TryFromBase64String(signature, provided, out var written) || written != 32) return false;

        Span<byte> expected = stackalloc byte[32];
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(channelSecret), body, expected);
        return CryptographicOperations.FixedTimeEquals(expected, provided[..written]);
    }
}
