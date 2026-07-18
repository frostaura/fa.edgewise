using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Edgewise.Infrastructure.Ingestion.Connectors;

/// <summary>
/// Builds CDP (Coinbase Developer Platform) API JWTs, hand-rolled with the BCL:
/// ES256 over the key's EC private key PEM.
///   header: {alg:"ES256", kid:{keyName}, typ:"JWT", nonce:{random hex}}
///   claims: {sub:{keyName}, iss:"cdp", nbf:now, exp:now+120, uri:"{METHOD} {host}{path}"}
/// The private key never leaves this process and is never logged.
/// </summary>
public static class CoinbaseJwtGenerator
{
    public static string Generate(
        string keyName, string privateKeyPem, string method, string host, string path, DateTimeOffset? now = null)
    {
        var issuedAt = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();

        var header = new Dictionary<string, object>
        {
            ["alg"] = "ES256",
            ["kid"] = keyName,
            ["typ"] = "JWT",
            ["nonce"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
        };
        var claims = new Dictionary<string, object>
        {
            ["sub"] = keyName,
            ["iss"] = "cdp",
            ["nbf"] = issuedAt,
            ["exp"] = issuedAt + 120,
            ["uri"] = $"{method} {host}{path}",
        };

        var signingInput =
            $"{Base64Url(JsonSerializer.SerializeToUtf8Bytes(header))}.{Base64Url(JsonSerializer.SerializeToUtf8Bytes(claims))}";

        using var ecdsa = ImportPrivateKey(privateKeyPem);
        // SignData returns the IEEE P-1363 (r||s) form JOSE requires.
        var signature = ecdsa.SignData(Encoding.UTF8.GetBytes(signingInput), HashAlgorithmName.SHA256);
        return $"{signingInput}.{Base64Url(signature)}";
    }

    /// <summary>Validates that the PEM parses to an EC key usable for ES256. Throws IntegrationException otherwise.</summary>
    public static void ValidatePrivateKey(string privateKeyPem)
    {
        using var _ = ImportPrivateKey(privateKeyPem);
    }

    private static ECDsa ImportPrivateKey(string privateKeyPem)
    {
        try
        {
            var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(privateKeyPem);
            return ecdsa;
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            throw new IntegrationException(
                "coinbase_invalid_private_key",
                "The private key PEM could not be read. Paste the full EC private key from the CDP portal, " +
                "including the BEGIN/END lines.",
                ex);
        }
    }

    internal static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
