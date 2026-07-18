namespace Edgewise.Contracts.Me;

/// <summary>Partial update; only non-null members are applied.</summary>
public sealed record UpdateMeRequest(
    string? BaseCurrency = null,
    string? Timezone = null,
    bool? LlmOptOut = null,
    string? SettingsJson = null);

public sealed record ApiTokenDto(
    Guid Id,
    string Name,
    DateTime CreatedAt,
    DateTime? LastUsedAt);

public sealed record CreateApiTokenRequest(string Name);

/// <summary>Token is returned exactly once at creation time.</summary>
public sealed record CreateApiTokenResponse(
    Guid Id,
    string Name,
    string Token,
    DateTime CreatedAt);
