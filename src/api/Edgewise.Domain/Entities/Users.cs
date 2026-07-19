namespace Edgewise.Domain.Entities;

public class User
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string? TotpSecretEnc { get; set; }
    public bool TotpEnabled { get; set; }
    public string? RecoveryCodesJson { get; set; }
    public string BaseCurrency { get; set; } = "ZAR";
    public string Timezone { get; set; } = "Africa/Johannesburg";
    public bool LlmOptOut { get; set; }
    public long MonthlyTokenBudget { get; set; } = 2_000_000;
    public bool IsAdmin { get; set; }
    public string? SettingsJson { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class RefreshToken : IUserOwned
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? ReplacedByHash { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class ApiToken : IUserOwned
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public DateTime? LastUsedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class RiskProfile : IUserOwned
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Version { get; set; } = 1;
    public bool IsActive { get; set; }
    public decimal RiskPct { get; set; }
    public decimal HeatCapPct { get; set; }
    public decimal ClusterCapPct { get; set; }
    public decimal DailyStopPct { get; set; }
    public int DailyLossCountStop { get; set; }
    public decimal WeeklyStopPct { get; set; }
    public decimal MaxLeverage { get; set; }
    public decimal MinRR { get; set; }
    public int MaxPositions { get; set; }
    public string? LadderThresholdsJson { get; set; }
}

public class AuditLog : IUserOwned
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string? DiffJson { get; set; }
    public DateTime At { get; set; }
}
