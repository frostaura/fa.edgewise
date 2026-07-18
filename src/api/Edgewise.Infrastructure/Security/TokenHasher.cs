using System.Security.Cryptography;
using System.Text;

namespace Edgewise.Infrastructure.Security;

/// <summary>
/// SHA-256 hashing for opaque tokens (refresh tokens, PATs, recovery codes)
/// stored server-side. Hex lowercase output.
/// </summary>
public static class TokenHasher
{
    public static string Sha256Hex(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
