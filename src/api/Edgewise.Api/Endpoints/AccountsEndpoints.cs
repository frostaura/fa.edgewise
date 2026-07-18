using System.Text.Json;
using System.Text.RegularExpressions;
using Edgewise.Api.Services.Integrations;
using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Auth;
using Edgewise.Infrastructure.Data;
using Edgewise.Infrastructure.Ingestion.Connectors;
using Edgewise.Infrastructure.Security;
using Hangfire;
using Microsoft.EntityFrameworkCore;

namespace Edgewise.Api.Endpoints;

/// <summary>
/// Exchange account management: connect (with per-venue credential validation),
/// list, sync, revoke, health. Credentials are stored only through
/// EnvelopeCrypto and never appear in responses or logs.
/// </summary>
public sealed partial class AccountsEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/accounts").RequireAuthorization();

        group.MapGet("/", ListAccounts);
        group.MapPost("/connect", ConnectAccount);
        group.MapPost("/{id:guid}/sync", SyncAccount);
        group.MapDelete("/{id:guid}", RevokeAccount);
        group.MapGet("/{id:guid}/health", GetAccountHealth);
    }

    // -------------------------------------------------------------- list

    private static async Task<IResult> ListAccounts(EdgewiseDbContext db, EnvelopeCrypto crypto, CancellationToken ct)
    {
        var accounts = await db.Accounts.OrderBy(a => a.Name).ToListAsync(ct);
        return Results.Ok(accounts.Select(a => ToDto(a, crypto)).ToList());
    }

    // ----------------------------------------------------------- connect

    private static async Task<IResult> ConnectAccount(
        ConnectAccountRequest request,
        EdgewiseDbContext db,
        ICurrentUser currentUser,
        EnvelopeCrypto crypto,
        PolymarketClient polymarket,
        BinanceClient binance,
        CoinbaseClient coinbase,
        CancellationToken ct)
    {
        var userId = currentUser.UserId
            ?? throw ApiException.Unauthorized("unauthorized", "Authentication required.");

        if (!Enum.TryParse<Venue>(request.Venue, ignoreCase: true, out var venue)
            || venue is not (Venue.Polymarket or Venue.Binance or Venue.Coinbase))
        {
            throw ApiException.BadRequest(
                "unsupported_venue",
                "venue must be one of: polymarket, binance, coinbase. EasyEquities and other brokers import via CSV.");
        }

        var bucketExists = await db.Buckets.AnyAsync(b => b.Id == request.BucketId, ct);
        if (!bucketExists)
        {
            throw ApiException.BadRequest("unknown_bucket", "bucketId does not match one of your buckets.");
        }

        string? warning = null;
        string? credentialsEnc = null;
        string? walletAddress = null;

        switch (venue)
        {
            case Venue.Polymarket:
            {
                walletAddress = request.WalletAddress?.Trim();
                if (walletAddress is null || !WalletRegex().IsMatch(walletAddress))
                {
                    throw ApiException.BadRequest(
                        "invalid_wallet", "walletAddress must be a 0x-prefixed 40-hex-character address.");
                }

                // PRD gotcha: Polymarket keys data by the proxy/deposit wallet, not the
                // signing EOA. Empty positions AND activity → warn, but still connect.
                var positions = await polymarket.GetPositionsAsync(walletAddress, ct);
                if (positions.Count == 0)
                {
                    var activity = await polymarket.GetActivityCountAsync(walletAddress, ct);
                    if (activity == 0)
                    {
                        warning = "No data found for this wallet — did you use the proxy/deposit wallet "
                            + "shown in your Polymarket profile, not your EOA signing wallet?";
                    }
                }

                break;
            }

            case Venue.Binance:
            {
                if (string.IsNullOrWhiteSpace(request.ApiKey) || string.IsNullOrWhiteSpace(request.ApiSecret))
                {
                    throw ApiException.BadRequest("missing_credentials", "apiKey and apiSecret are required for Binance.");
                }

                BinanceApiRestrictions restrictions;
                try
                {
                    restrictions = await binance.GetApiRestrictionsAsync(
                        request.ApiKey.Trim(), request.ApiSecret.Trim(), ct);
                }
                catch (IntegrationException ex)
                {
                    throw ApiException.BadRequest(ex.Code, ex.Message);
                }

                // INT-004: reject over-scoped keys with a clear, actionable error.
                var problems = new List<string>();
                if (!restrictions.EnableReading)
                {
                    problems.Add("\"Enable Reading\" is switched off");
                }

                if (restrictions.EnableSpotAndMarginTrading)
                {
                    problems.Add("\"Enable Spot & Margin Trading\" is switched on");
                }

                if (restrictions.EnableWithdrawals)
                {
                    problems.Add("\"Enable Withdrawals\" is switched on");
                }

                if (problems.Count > 0)
                {
                    throw ApiException.BadRequest(
                        "key_over_scoped",
                        "This Binance API key is not read-only: " + string.Join("; ", problems)
                        + ". Create a key with only \"Enable Reading\" and try again.");
                }

                credentialsEnc = crypto.Encrypt(JsonSerializer.Serialize(
                    new BinanceCredentials(request.ApiKey.Trim(), request.ApiSecret.Trim()),
                    IntegrationJson.Options));
                break;
            }

            case Venue.Coinbase:
            {
                if (string.IsNullOrWhiteSpace(request.KeyName) || string.IsNullOrWhiteSpace(request.PrivateKeyPem))
                {
                    throw ApiException.BadRequest(
                        "missing_credentials", "keyName and privateKeyPem are required for Coinbase.");
                }

                try
                {
                    CoinbaseJwtGenerator.ValidatePrivateKey(request.PrivateKeyPem);
                    await coinbase.ValidateCredentialsAsync(request.KeyName.Trim(), request.PrivateKeyPem, ct);
                }
                catch (IntegrationException ex)
                {
                    throw ApiException.BadRequest(ex.Code, ex.Message);
                }

                credentialsEnc = crypto.Encrypt(JsonSerializer.Serialize(
                    new CoinbaseCredentials(request.KeyName.Trim(), request.PrivateKeyPem),
                    IntegrationJson.Options));
                break;
            }
        }

        var stats = new AccountSyncStats { Warning = warning };
        var account = new Account
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            BucketId = request.BucketId,
            Venue = venue,
            Name = string.IsNullOrWhiteSpace(request.Name) ? venue.ToString() : request.Name.Trim(),
            CredentialsEnc = credentialsEnc,
            WalletAddress = walletAddress,
            Status = AccountStatus.Connected,
            LastSyncStatsJson = warning is null ? null : stats.ToJson(),
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync(ct);

        return Results.Created(
            $"/api/accounts/{account.Id}", new ConnectAccountResponse(ToDto(account, crypto), warning));
    }

    // -------------------------------------------------------------- sync

    private static async Task<IResult> SyncAccount(
        Guid id,
        EdgewiseDbContext db,
        EnvelopeCrypto crypto,
        AccountSyncOrchestrator orchestrator,
        IServiceProvider services,
        CancellationToken ct)
    {
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == id, ct)
            ?? throw ApiException.NotFound("account_not_found", "Account not found.");
        if (account.Status == AccountStatus.Revoked)
        {
            throw ApiException.Conflict("account_revoked", "This account was revoked — reconnect it to sync again.");
        }

        if (!AccountSyncOrchestrator.IsSyncableVenue(account.Venue))
        {
            throw ApiException.BadRequest("venue_not_syncable", "This account type cannot be synced from an API.");
        }

        // Enqueue when Hangfire is running; run inline when it is disabled.
        var backgroundJobs = services.GetService<IBackgroundJobClient>();
        if (backgroundJobs is not null)
        {
            backgroundJobs.Enqueue<ExchangeSyncJob>(job => job.SyncAccountAsync(id));
            return Results.Accepted($"/api/accounts/{id}/health", new SyncAccountResponse(true, null));
        }

        await orchestrator.SyncAccountAsync(id, ct);
        var refreshed = await db.Accounts.AsNoTracking().FirstAsync(a => a.Id == id, ct);
        return Results.Ok(new SyncAccountResponse(false, ToDto(refreshed, crypto)));
    }

    // ------------------------------------------------------------ revoke

    private static async Task<IResult> RevokeAccount(Guid id, EdgewiseDbContext db, CancellationToken ct)
    {
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == id, ct)
            ?? throw ApiException.NotFound("account_not_found", "Account not found.");

        account.Status = AccountStatus.Revoked;
        account.CredentialsEnc = null;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    // ------------------------------------------------------------ health

    private static async Task<IResult> GetAccountHealth(Guid id, EdgewiseDbContext db, CancellationToken ct)
    {
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == id, ct)
            ?? throw ApiException.NotFound("account_not_found", "Account not found.");

        var stats = AccountSyncStats.Parse(account.LastSyncStatsJson);
        var providerKey = AccountSyncOrchestrator.ProviderKey(account.Venue);
        var health = await db.ProviderHealths.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Provider == providerKey, ct);

        var recentErrors = new List<string>();
        if (stats.Error is not null)
        {
            recentErrors.Add(stats.Error);
        }

        if (health?.ErrorNote is not null && health.ErrorNote != stats.Error)
        {
            recentErrors.Add(health.ErrorNote);
        }

        return Results.Ok(new AccountHealthDto(
            account.Id,
            account.Status,
            account.LastSyncAt,
            account.LastSyncStatsJson is null ? null : stats,
            health is null ? null : new ProviderHealthDto(health.LastSuccessAt, health.LastErrorAt, health.ErrorNote),
            recentErrors));
    }

    // ------------------------------------------------------------ mapping

    private static AccountDto ToDto(Account account, EnvelopeCrypto crypto) =>
        new(
            account.Id,
            account.Venue,
            account.Name,
            account.BucketId,
            account.Status,
            MaskWallet(account.WalletAddress),
            KeyLastFour(account, crypto),
            account.LastSyncAt,
            account.LastSyncStatsJson is null ? null : AccountSyncStats.Parse(account.LastSyncStatsJson));

    /// <summary>0x1a2b3c…def0 — enough to recognise, never the full address.</summary>
    internal static string? MaskWallet(string? wallet) =>
        wallet is null || wallet.Length < 12 ? wallet : $"{wallet[..8]}…{wallet[^4..]}";

    /// <summary>Last four characters of the stored API key / key name; never the key itself.</summary>
    private static string? KeyLastFour(Account account, EnvelopeCrypto crypto)
    {
        if (account.CredentialsEnc is null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(crypto.DecryptToString(account.CredentialsEnc));
            var key = doc.RootElement.TryGetProperty("apiKey", out var apiKey)
                ? apiKey.GetString()
                : doc.RootElement.TryGetProperty("keyName", out var keyName) ? keyName.GetString() : null;
            return key is { Length: >= 4 } ? key[^4..] : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    [GeneratedRegex("^0x[0-9a-fA-F]{40}$")]
    private static partial Regex WalletRegex();
}
