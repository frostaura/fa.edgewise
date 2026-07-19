using System.Security.Cryptography;
using System.Text;

namespace Edgewise.Infrastructure.Security;

/// <summary>
/// Envelope encryption with AES-256-GCM. Each Encrypt call generates a fresh
/// random data-encryption key (DEK) that encrypts the payload; the DEK is then
/// wrapped with the master key. Output format:
/// <c>v1:{base64(dekNonce || wrappedDek || dekTag)}:{base64(payloadNonce || ciphertext || payloadTag)}</c>.
/// The master key comes from the EDGEWISE_ENCRYPTION_KEY environment/config value
/// (base64, exactly 32 bytes).
/// </summary>
public sealed class EnvelopeCrypto
{
    public const string EnvVarName = "EDGEWISE_ENCRYPTION_KEY";

    private const string Version = "v1";
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _masterKey;

    public EnvelopeCrypto(byte[] masterKey)
    {
        if (masterKey.Length != KeySize)
        {
            throw new ArgumentException($"Master key must be exactly {KeySize} bytes.", nameof(masterKey));
        }

        _masterKey = (byte[])masterKey.Clone();
    }

    public static EnvelopeCrypto FromBase64(string base64MasterKey)
    {
        byte[] key;
        try
        {
            key = Convert.FromBase64String(base64MasterKey);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException($"{EnvVarName} must be valid base64.", nameof(base64MasterKey), ex);
        }

        return new EnvelopeCrypto(key);
    }

    public string Encrypt(string plaintext) => Encrypt(Encoding.UTF8.GetBytes(plaintext));

    public string Encrypt(byte[] plaintext)
    {
        var dek = RandomNumberGenerator.GetBytes(KeySize);
        try
        {
            var payloadBlob = Seal(dek, plaintext);
            var wrappedDekBlob = Seal(_masterKey, dek);
            return $"{Version}:{Convert.ToBase64String(wrappedDekBlob)}:{Convert.ToBase64String(payloadBlob)}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    public string DecryptToString(string blob) => Encoding.UTF8.GetString(Decrypt(blob));

    public byte[] Decrypt(string blob)
    {
        var parts = blob.Split(':');
        if (parts.Length != 3 || parts[0] != Version)
        {
            throw new CryptographicException("Unrecognised ciphertext format.");
        }

        var wrappedDekBlob = Convert.FromBase64String(parts[1]);
        var payloadBlob = Convert.FromBase64String(parts[2]);

        var dek = Open(_masterKey, wrappedDekBlob);
        try
        {
            return Open(dek, payloadBlob);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    private static byte[] Seal(byte[] key, byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var blob = new byte[NonceSize + ciphertext.Length + TagSize];
        nonce.CopyTo(blob, 0);
        ciphertext.CopyTo(blob, NonceSize);
        tag.CopyTo(blob, NonceSize + ciphertext.Length);
        return blob;
    }

    private static byte[] Open(byte[] key, byte[] blob)
    {
        if (blob.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("Ciphertext too short.");
        }

        var nonce = blob.AsSpan(0, NonceSize);
        var ciphertext = blob.AsSpan(NonceSize, blob.Length - NonceSize - TagSize);
        var tag = blob.AsSpan(blob.Length - TagSize, TagSize);
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }
}
