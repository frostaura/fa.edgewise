namespace Edgewise.Contracts.Auth;

public sealed record RegisterRequest(string Email, string Password);

public sealed record LoginRequest(string Email, string Password);

/// <summary>Second step of a TOTP-protected login. Code may be a 6-digit TOTP or a recovery code.</summary>
public sealed record TotpLoginRequest(string TotpToken, string Code);

public sealed record RefreshRequest(string RefreshToken);

public sealed record LogoutRequest(string RefreshToken);

public sealed record UserDto(
    Guid Id,
    string Email,
    string BaseCurrency,
    string Timezone,
    bool TotpEnabled,
    bool IsAdmin,
    bool LlmOptOut,
    string? SettingsJson,
    DateTime CreatedAt);

/// <summary>
/// Login/register/refresh response. When RequiresTotp is true only TotpToken is set;
/// otherwise AccessToken/RefreshToken/User are set. Null members are omitted from JSON.
/// </summary>
public sealed record AuthResponse(
    bool RequiresTotp,
    string? AccessToken = null,
    string? RefreshToken = null,
    UserDto? User = null,
    string? TotpToken = null);

public sealed record TotpSetupResponse(string Secret, string OtpauthUri);

public sealed record TotpCodeRequest(string Code);

public sealed record TotpEnableResponse(IReadOnlyList<string> RecoveryCodes);
