using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Ingestion.Connectors;

namespace Edgewise.Api.Services.Integrations;

/// <summary>
/// Wire DTOs for /api/accounts. Credentials NEVER appear here: wallet addresses
/// are masked and API keys reduce to their last four characters.
/// </summary>
public sealed record AccountDto(
    Guid Id,
    Venue Venue,
    string Name,
    Guid BucketId,
    AccountStatus Status,
    string? WalletAddress,
    string? KeyLastFour,
    DateTime? LastSyncAt,
    AccountSyncStats? LastSyncStats);

public sealed record ConnectAccountRequest(
    string Venue,
    Guid BucketId,
    string? Name,
    string? WalletAddress,
    string? ApiKey,
    string? ApiSecret,
    string? KeyName,
    string? PrivateKeyPem);

public sealed record ConnectAccountResponse(AccountDto Account, string? Warning);

public sealed record SyncAccountResponse(bool Queued, AccountDto? Account);

public sealed record ProviderHealthDto(DateTime? LastSuccessAt, DateTime? LastErrorAt, string? ErrorNote);

public sealed record AccountHealthDto(
    Guid Id,
    AccountStatus Status,
    DateTime? LastSyncAt,
    AccountSyncStats? LastSyncStats,
    ProviderHealthDto? Provider,
    IReadOnlyList<string> RecentErrors);
