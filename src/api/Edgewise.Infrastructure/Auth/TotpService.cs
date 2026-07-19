using OtpNet;

namespace Edgewise.Infrastructure.Auth;

/// <summary>TOTP secrets, otpauth URIs and code verification (RFC 6238, 6 digits, 30 s).</summary>
public sealed class TotpService
{
    public const string Issuer = "Edgewise";

    /// <summary>Generates a new 160-bit secret, base32 encoded.</summary>
    public string GenerateSecret() => Base32Encoding.ToString(KeyGeneration.GenerateRandomKey(20));

    public string BuildOtpauthUri(string email, string base32Secret) =>
        $"otpauth://totp/{Uri.EscapeDataString(Issuer)}:{Uri.EscapeDataString(email)}" +
        $"?secret={base32Secret}&issuer={Uri.EscapeDataString(Issuer)}&algorithm=SHA1&digits=6&period=30";

    /// <summary>Verifies a 6-digit code with a ±1 step window.</summary>
    public bool VerifyCode(string base32Secret, string code)
    {
        code = code.Trim().Replace(" ", "");
        if (code.Length != 6 || !code.All(char.IsAsciiDigit))
        {
            return false;
        }

        var totp = new Totp(Base32Encoding.ToBytes(base32Secret));
        return totp.VerifyTotp(code, out _, new VerificationWindow(previous: 1, future: 1));
    }
}
