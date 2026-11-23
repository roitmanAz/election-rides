using System.Security.Cryptography;
using System.Text;

namespace ElectionRides.Api.Auth;

/// <summary>Random tokens and codes. Only their SHA-256 hashes are stored.</summary>
public static class Secrets
{
    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public static string NewCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    public static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>The code hash includes the challenge id, so equal codes in different challenges differ.</summary>
    public static string HashCode(Guid challengeId, string code) => Hash($"{challengeId:N}:{code}");

    public static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
