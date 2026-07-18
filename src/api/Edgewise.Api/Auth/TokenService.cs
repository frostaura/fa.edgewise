using System.Security.Claims;
using System.Text;
using Edgewise.Domain.Entities;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Edgewise.Api.Auth;

/// <summary>
/// HS256 JWT creation/validation. The signing secret comes from the
/// EDGEWISE_JWT_SECRET environment/config value (min 32 bytes; a dev-only
/// fallback lives in appsettings.Development.json).
/// </summary>
public sealed class TokenService
{
    public const string SecretConfigKey = "EDGEWISE_JWT_SECRET";
    public const string Issuer = "edgewise";
    public const string Audience = "edgewise";
    public const string PurposeClaim = "purpose";
    public const string AccessPurpose = "access";
    public const string TotpPurpose = "totp";

    public static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan TotpTokenLifetime = TimeSpan.FromMinutes(5);

    private readonly SymmetricSecurityKey _key;
    private readonly JsonWebTokenHandler _handler = new();

    public TokenService(IConfiguration configuration)
    {
        var secret = configuration[SecretConfigKey];
        if (string.IsNullOrEmpty(secret) || Encoding.UTF8.GetByteCount(secret) < 32)
        {
            throw new InvalidOperationException(
                $"{SecretConfigKey} must be set and at least 32 bytes long.");
        }

        _key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
    }

    public SecurityKey SigningKey => _key;

    public string CreateAccessToken(User user) =>
        CreateToken(user.Id, AccessPurpose, AccessTokenLifetime, new Dictionary<string, object>
        {
            ["email"] = user.Email,
            ["admin"] = user.IsAdmin,
        });

    /// <summary>Short-lived token proving the password step succeeded, pending TOTP.</summary>
    public string CreateTotpToken(User user) =>
        CreateToken(user.Id, TotpPurpose, TotpTokenLifetime, []);

    /// <summary>Returns the user id when the token is a valid, unexpired TOTP step-up token.</summary>
    public async Task<Guid?> ValidateTotpTokenAsync(string token)
    {
        var result = await _handler.ValidateTokenAsync(token, BuildValidationParameters());
        if (!result.IsValid)
        {
            return null;
        }

        if (!result.Claims.TryGetValue(PurposeClaim, out var purpose) || purpose as string != TotpPurpose)
        {
            return null;
        }

        return result.Claims.TryGetValue("sub", out var sub) && Guid.TryParse(sub as string, out var id)
            ? id
            : null;
    }

    public TokenValidationParameters BuildValidationParameters() => new()
    {
        ValidIssuer = Issuer,
        ValidAudience = Audience,
        IssuerSigningKey = _key,
        ValidateIssuerSigningKey = true,
        ClockSkew = TimeSpan.FromMinutes(1),
        NameClaimType = "sub",
        RoleClaimType = "role",
    };

    private string CreateToken(Guid userId, string purpose, TimeSpan lifetime, Dictionary<string, object> extraClaims)
    {
        var claims = new Dictionary<string, object>(extraClaims)
        {
            ["sub"] = userId.ToString(),
            [PurposeClaim] = purpose,
            ["jti"] = Guid.NewGuid().ToString("N"),
        };

        return _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Claims = claims,
            NotBefore = DateTime.UtcNow,
            Expires = DateTime.UtcNow.Add(lifetime),
            SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256),
        });
    }

    /// <summary>Builds the claims identity used by both JWT and PAT authentication.</summary>
    public static ClaimsIdentity BuildIdentity(User user, string authenticationType) =>
        new(
        [
            new Claim("sub", user.Id.ToString()),
            new Claim("email", user.Email),
            new Claim("admin", user.IsAdmin ? "true" : "false"),
            new Claim(PurposeClaim, AccessPurpose),
        ], authenticationType, nameType: "sub", roleType: "role");
}
