using System.Security.Cryptography;
using System.Text.Json;
using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Data;
using Edgewise.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Edgewise.Infrastructure.Auth;

/// <summary>
/// Users, credentials, refresh-token rotation, TOTP and personal access tokens.
/// Auth flows run before a user identity exists in the scope, so every query
/// here bypasses the per-user global filters explicitly.
/// </summary>
public sealed class AuthService(EdgewiseDbContext db, EnvelopeCrypto crypto, TotpService totp)
{
    public const string PatPrefix = "ew_";
    public static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(30);

    private const string RecoveryCodeAlphabet = "ABCDEFGHJKMNPQRSTVWXYZ23456789";

    // ---------------------------------------------------------------- users

    public async Task<User> RegisterAsync(string email, string password, CancellationToken ct = default)
    {
        email = NormalizeEmail(email);
        ValidateEmail(email);
        ValidatePassword(password);

        var exists = await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == email, ct);
        if (exists)
        {
            throw ApiException.Conflict("email_taken", "An account with this email already exists.");
        }

        var isFirstUser = !await db.Users.IgnoreQueryFilters().AnyAsync(ct);

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            PasswordHash = PasswordHasher.Hash(password),
            IsAdmin = isFirstUser,
            CreatedAt = DateTime.UtcNow,
        };

        db.Users.Add(user);
        await db.SaveChangesAsync(ct);

        await DataSeeder.SeedUserDefaultsAsync(db, user.Id, user.BaseCurrency, ct);
        return user;
    }

    public async Task<User?> ValidateCredentialsAsync(string email, string password, CancellationToken ct = default)
    {
        email = NormalizeEmail(email);
        var user = await db.Users.IgnoreQueryFilters().SingleOrDefaultAsync(u => u.Email == email, ct);
        if (user is null || !PasswordHasher.Verify(password, user.PasswordHash))
        {
            return null;
        }

        return user;
    }

    public Task<User?> FindByIdAsync(Guid userId, CancellationToken ct = default) =>
        db.Users.IgnoreQueryFilters().SingleOrDefaultAsync(u => u.Id == userId, ct);

    // ------------------------------------------------------- refresh tokens

    /// <summary>Issues a new refresh token; only its SHA-256 hash is stored.</summary>
    public async Task<string> IssueRefreshTokenAsync(Guid userId, CancellationToken ct = default)
    {
        var token = GenerateOpaqueToken();
        db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = TokenHasher.Sha256Hex(token),
            ExpiresAt = DateTime.UtcNow.Add(RefreshTokenLifetime),
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        return token;
    }

    /// <summary>
    /// Rotates a refresh token. Presenting an already-revoked token is treated
    /// as theft: every active token for that user is revoked.
    /// </summary>
    public async Task<(User User, string NewRefreshToken)> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var hash = TokenHasher.Sha256Hex(refreshToken);
        var stored = await db.RefreshTokens.IgnoreQueryFilters()
            .SingleOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (stored is null)
        {
            throw ApiException.Unauthorized("invalid_refresh_token", "Refresh token is not recognised.");
        }

        if (stored.RevokedAt is not null)
        {
            // Reuse detected — revoke the whole family for this user.
            await db.RefreshTokens.IgnoreQueryFilters()
                .Where(t => t.UserId == stored.UserId && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, DateTime.UtcNow), ct);
            throw ApiException.Unauthorized("refresh_token_reused", "Refresh token reuse detected; all sessions revoked.");
        }

        if (stored.ExpiresAt <= DateTime.UtcNow)
        {
            throw ApiException.Unauthorized("refresh_token_expired", "Refresh token has expired.");
        }

        var user = await FindByIdAsync(stored.UserId, ct)
            ?? throw ApiException.Unauthorized("invalid_refresh_token", "Refresh token is not recognised.");

        var newToken = GenerateOpaqueToken();
        var newHash = TokenHasher.Sha256Hex(newToken);
        stored.RevokedAt = DateTime.UtcNow;
        stored.ReplacedByHash = newHash;
        db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = stored.UserId,
            TokenHash = newHash,
            ExpiresAt = DateTime.UtcNow.Add(RefreshTokenLifetime),
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);

        return (user, newToken);
    }

    public async Task LogoutAsync(string refreshToken, CancellationToken ct = default)
    {
        var hash = TokenHasher.Sha256Hex(refreshToken);
        await db.RefreshTokens.IgnoreQueryFilters()
            .Where(t => t.TokenHash == hash && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, DateTime.UtcNow), ct);
    }

    // ------------------------------------------------------------------ totp

    public async Task<(string Secret, string OtpauthUri)> SetupTotpAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await RequireUserAsync(userId, ct);
        if (user.TotpEnabled)
        {
            throw ApiException.Conflict("totp_already_enabled", "TOTP is already enabled; disable it first.");
        }

        var secret = totp.GenerateSecret();
        user.TotpSecretEnc = crypto.Encrypt(secret);
        await db.SaveChangesAsync(ct);

        return (secret, totp.BuildOtpauthUri(user.Email, secret));
    }

    public async Task<IReadOnlyList<string>> EnableTotpAsync(Guid userId, string code, CancellationToken ct = default)
    {
        var user = await RequireUserAsync(userId, ct);
        if (user.TotpEnabled)
        {
            throw ApiException.Conflict("totp_already_enabled", "TOTP is already enabled.");
        }

        if (user.TotpSecretEnc is null)
        {
            throw ApiException.BadRequest("totp_not_initialised", "Call /api/auth/totp/setup first.");
        }

        VerifyTotpCodeOrThrow(user, code);

        var codes = GenerateRecoveryCodes();
        user.TotpEnabled = true;
        user.RecoveryCodesJson = JsonSerializer.Serialize(
            codes.Select(c => TokenHasher.Sha256Hex(NormalizeRecoveryCode(c))).ToArray());
        await db.SaveChangesAsync(ct);
        return codes;
    }

    public async Task DisableTotpAsync(Guid userId, string code, CancellationToken ct = default)
    {
        var user = await RequireUserAsync(userId, ct);
        if (!user.TotpEnabled)
        {
            throw ApiException.BadRequest("totp_not_enabled", "TOTP is not enabled.");
        }

        if (!VerifyTotpOrRecoveryCode(user, code))
        {
            throw ApiException.Unauthorized("invalid_totp_code", "Invalid TOTP or recovery code.");
        }

        user.TotpEnabled = false;
        user.TotpSecretEnc = null;
        user.RecoveryCodesJson = null;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Step-up verification during login. Accepts a TOTP code or a one-time
    /// recovery code (which is consumed). Persists recovery-code consumption.
    /// </summary>
    public async Task<User> CompleteTotpLoginAsync(Guid userId, string code, CancellationToken ct = default)
    {
        var user = await RequireUserAsync(userId, ct);
        if (!user.TotpEnabled)
        {
            throw ApiException.BadRequest("totp_not_enabled", "TOTP is not enabled for this account.");
        }

        if (!VerifyTotpOrRecoveryCode(user, code))
        {
            throw ApiException.Unauthorized("invalid_totp_code", "Invalid TOTP or recovery code.");
        }

        await db.SaveChangesAsync(ct);
        return user;
    }

    private void VerifyTotpCodeOrThrow(User user, string code)
    {
        var secret = crypto.DecryptToString(user.TotpSecretEnc!);
        if (!totp.VerifyCode(secret, code))
        {
            throw ApiException.Unauthorized("invalid_totp_code", "Invalid TOTP code.");
        }
    }

    /// <summary>Tries a TOTP code first, then recovery codes (consuming on match). Does not save.</summary>
    private bool VerifyTotpOrRecoveryCode(User user, string code)
    {
        if (user.TotpSecretEnc is not null)
        {
            var secret = crypto.DecryptToString(user.TotpSecretEnc);
            if (totp.VerifyCode(secret, code))
            {
                return true;
            }
        }

        if (user.RecoveryCodesJson is null)
        {
            return false;
        }

        var hashes = JsonSerializer.Deserialize<List<string>>(user.RecoveryCodesJson) ?? [];
        var candidate = TokenHasher.Sha256Hex(NormalizeRecoveryCode(code));
        if (!hashes.Remove(candidate))
        {
            return false;
        }

        user.RecoveryCodesJson = JsonSerializer.Serialize(hashes);
        return true;
    }

    // ------------------------------------------------------------------- PATs

    public async Task<(ApiToken Token, string Plaintext)> CreateApiTokenAsync(Guid userId, string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw ApiException.BadRequest("validation_error", "Token name is required.");
        }

        var plaintext = PatPrefix + GenerateOpaqueToken();
        var token = new ApiToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = name.Trim(),
            TokenHash = TokenHasher.Sha256Hex(plaintext),
            CreatedAt = DateTime.UtcNow,
        };
        db.ApiTokens.Add(token);
        await db.SaveChangesAsync(ct);
        return (token, plaintext);
    }

    /// <summary>Resolves a PAT to its user, updating LastUsedAt. Null when invalid.</summary>
    public async Task<User?> ValidateApiTokenAsync(string plaintext, CancellationToken ct = default)
    {
        if (!plaintext.StartsWith(PatPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var hash = TokenHasher.Sha256Hex(plaintext);
        var token = await db.ApiTokens.IgnoreQueryFilters().SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is null)
        {
            return null;
        }

        token.LastUsedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return await FindByIdAsync(token.UserId, ct);
    }

    public Task<List<ApiToken>> ListApiTokensAsync(Guid userId, CancellationToken ct = default) =>
        db.ApiTokens.IgnoreQueryFilters()
            .Where(t => t.UserId == userId)
            .OrderBy(t => t.CreatedAt)
            .ToListAsync(ct);

    public async Task DeleteApiTokenAsync(Guid userId, Guid tokenId, CancellationToken ct = default)
    {
        var token = await db.ApiTokens.IgnoreQueryFilters()
            .SingleOrDefaultAsync(t => t.Id == tokenId && t.UserId == userId, ct);
        if (token is null)
        {
            throw ApiException.NotFound("token_not_found", "API token not found.");
        }

        db.ApiTokens.Remove(token);
        await db.SaveChangesAsync(ct);
    }

    // ---------------------------------------------------------------- helpers

    private async Task<User> RequireUserAsync(Guid userId, CancellationToken ct) =>
        await FindByIdAsync(userId, ct)
        ?? throw ApiException.Unauthorized("user_not_found", "User no longer exists.");

    private static string GenerateOpaqueToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    private static void ValidateEmail(string email)
    {
        var at = email.IndexOf('@');
        if (email.Length is < 3 or > 320 || at <= 0 || at == email.Length - 1 || !email[(at + 1)..].Contains('.'))
        {
            throw ApiException.BadRequest("invalid_email", "A valid email address is required.");
        }
    }

    private static void ValidatePassword(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 8)
        {
            throw ApiException.BadRequest("weak_password", "Password must be at least 8 characters.");
        }

        if (password.Length > 256)
        {
            throw ApiException.BadRequest("invalid_password", "Password must be at most 256 characters.");
        }
    }

    private static string NormalizeRecoveryCode(string code) =>
        code.Trim().Replace("-", "").Replace(" ", "").ToUpperInvariant();

    private static string[] GenerateRecoveryCodes()
    {
        var codes = new string[8];
        for (var i = 0; i < codes.Length; i++)
        {
            Span<char> chars = stackalloc char[8];
            for (var j = 0; j < chars.Length; j++)
            {
                chars[j] = RecoveryCodeAlphabet[RandomNumberGenerator.GetInt32(RecoveryCodeAlphabet.Length)];
            }

            codes[i] = $"{new string(chars[..4])}-{new string(chars[4..])}";
        }

        return codes;
    }
}
