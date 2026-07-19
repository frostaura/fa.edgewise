using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Edgewise.Api.Auth;
using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Auth;
using Edgewise.Infrastructure.Data;
using Edgewise.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Edgewise.Api.Endpoints;

/// <summary>Password confirmation body for account deletion.</summary>
public sealed record DeleteMeRequest(string Password);

/// <summary>
/// POPIA data-subject endpoints: GET /api/me/export streams a ZIP with one JSON
/// file per data pillar (plus CSV for trades/fills/holdings); DELETE /api/me
/// hard-deletes every row the user owns (FK-safe order, tokens revoked) after
/// password confirmation, leaving a single tombstone audit row keyed by a
/// hashed identifier. Both endpoints require an interactive JWT — ew_ personal
/// access tokens are rejected.
/// </summary>
public sealed class ExportEndpoints : IEndpointModule
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/me").RequireAuthorization();
        group.MapGet("/export", Export);
        group.MapDelete("/", DeleteMe);
    }

    /// <summary>Data-subject endpoints prove interactive presence: PATs are machine credentials.</summary>
    private static void RequireJwt(HttpContext http)
    {
        if (http.User.Identity?.AuthenticationType == ApiTokenAuthenticationHandler.SchemeName)
        {
            throw ApiException.Forbidden(
                "pat_not_allowed",
                "Personal access tokens cannot use this endpoint; sign in and use a session (JWT) instead.");
        }
    }

    // -------------------------------------------------------------- export

    private static async Task<IResult> Export(HttpContext http, EdgewiseDbContext db, CancellationToken ct)
    {
        RequireJwt(http);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(ct)
            ?? throw ApiException.Unauthorized("unauthorized", "Authentication required.");

        // Materialize everything before streaming so a query failure yields a clean
        // error response instead of a truncated archive. Global query filters scope
        // every set to the current user.
        var trades = await db.Trades.AsNoTracking().OrderBy(t => t.OpenedAt).ToListAsync(ct);
        var plans = await db.TradePlans.AsNoTracking().OrderBy(p => p.CreatedAt).ToListAsync(ct);
        var planVersions = await db.TradePlanVersions.AsNoTracking().OrderBy(v => v.At)
            .Select(v => new { v.Id, v.PlanId, v.Version, v.FieldsJson, v.At }).ToListAsync(ct);
        var fills = await db.Fills.AsNoTracking().OrderBy(f => f.At).ToListAsync(ct);
        var journalEntries = await db.JournalEntries.AsNoTracking().OrderBy(j => j.At)
            .Select(j => new { j.Id, j.TradeId, j.NotesMd, j.At }).ToListAsync(ct);
        var adherence = await db.AdherenceResults.AsNoTracking().OrderBy(a => a.ComputedAt)
            .Select(a => new { a.Id, a.TradeId, a.RubricVersion, a.Score, a.Grade, a.DeductionsJson, a.ComputedAt })
            .ToListAsync(ct);
        var tags = await db.Tags.AsNoTracking().ToListAsync(ct);
        var tradeTags = await db.TradeTags.AsNoTracking()
            .Select(tt => new { tt.TradeId, tt.TagId }).ToListAsync(ct);
        var holdings = await db.Holdings.AsNoTracking().ToListAsync(ct);
        var lots = await db.Lots.AsNoTracking()
            .Select(l => new { l.Id, l.HoldingId, l.Qty, l.CostMinor, l.CostCurrency, l.AcquiredAt, l.Source, l.TradeId })
            .ToListAsync(ct);
        var buckets = await db.Buckets.AsNoTracking().ToListAsync(ct);
        var accounts = await db.Accounts.AsNoTracking()
            .Select(a => new { a.Id, a.BucketId, a.Venue, a.Name, a.WalletAddress, a.Status, a.LastSyncAt })
            .ToListAsync(ct);
        var cashflows = await db.CashFlows.AsNoTracking().OrderBy(f => f.At).ToListAsync(ct);
        var dividends = await db.Dividends.AsNoTracking()
            .Select(d => new { d.Id, d.HoldingId, d.ExDate, d.PayDate, d.AmountMinor, d.Currency, d.Reinvested, d.DripLotId })
            .ToListAsync(ct);
        var snapshots = await db.Snapshots.AsNoTracking().OrderBy(s => s.Date).ToListAsync(ct);
        var watchlists = await db.Watchlists.AsNoTracking().ToListAsync(ct);
        var watchlistItems = await db.WatchlistItems.AsNoTracking()
            .Select(w => new { w.Id, w.WatchlistId, w.InstrumentId, w.Note, w.WhyWatching, w.AlertLevelsJson })
            .ToListAsync(ct);
        var alerts = await db.Alerts.AsNoTracking().ToListAsync(ct);
        var zones = await db.Zones.AsNoTracking().ToListAsync(ct);
        var strategies = await db.Strategies.AsNoTracking().ToListAsync(ct);
        var strategyVersions = await db.StrategyVersions.AsNoTracking()
            .Select(v => new { v.Id, v.StrategyId, v.Version, v.RuleTreeJson, v.At }).ToListAsync(ct);
        var backtests = await db.Backtests.AsNoTracking().ToListAsync(ct);
        var insights = await db.Insights.AsNoTracking().OrderBy(i => i.CreatedAt).ToListAsync(ct);
        var brier = await db.BrierForecasts.AsNoTracking().ToListAsync(ct);
        var decisions = await db.DecisionLogs.AsNoTracking().OrderBy(d => d.At).ToListAsync(ct);
        var overrides = await db.OverrideLogs.AsNoTracking().OrderBy(o => o.At).ToListAsync(ct);
        var auditLog = await db.AuditLogs.AsNoTracking().OrderBy(a => a.At).ToListAsync(ct);

        // Symbol lookup for the CSV files (instruments are a global catalogue).
        var instrumentIds = trades.Select(t => t.InstrumentId)
            .Concat(fills.Select(f => f.InstrumentId))
            .Concat(holdings.Select(h => h.InstrumentId))
            .Distinct().ToList();
        var symbols = await db.Instruments.AsNoTracking()
            .Where(i => instrumentIds.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, i => i.Symbol, ct);

        var profile = new
        {
            user.Id,
            user.Email,
            user.BaseCurrency,
            user.Timezone,
            user.LlmOptOut,
            user.MonthlyTokenBudget,
            user.IsAdmin,
            user.TotpEnabled,
            user.SettingsJson,
            user.CreatedAt,
        };

        var entries = new (string Name, object Payload)[]
        {
            ("trades.json", trades),
            ("plans.json", new { plans, versions = planVersions }),
            ("fills.json", fills),
            ("holdings.json", new { holdings, lots }),
            ("buckets.json", new { buckets, accounts }),
            ("cashflows.json", cashflows),
            ("dividends.json", dividends),
            ("snapshots.json", snapshots),
            ("watchlists.json", new { watchlists, items = watchlistItems }),
            ("alerts.json", alerts),
            ("zones.json", zones),
            ("strategies.json", new { strategies, versions = strategyVersions }),
            ("backtests.json", backtests),
            ("insights.json", insights),
            ("brier.json", brier),
            ("decisions.json", decisions),
            ("overrides.json", overrides),
            ("audit-log.json", auditLog),
            ("profile.json", profile),
            ("journal-entries.json", new { journalEntries, adherence, tags, tradeTags }),
        };

        string Symbol(Guid id) => symbols.GetValueOrDefault(id, id.ToString());

        var tradesCsv = ToCsv(
            ["Id", "Symbol", "Direction", "Status", "OpenedAt", "ClosedAt", "Qty", "AvgEntryPrice", "AvgExitPrice",
             "RealisedPnlMinor", "FeesMinor", "Currency", "RPlanned", "RRealised", "EmotionTag", "IsPaper"],
            trades.Select(t => new object?[]
            {
                t.Id, Symbol(t.InstrumentId), t.Direction, t.Status, t.OpenedAt, t.ClosedAt, t.Qty,
                t.AvgEntryPrice, t.AvgExitPrice, t.RealisedPnlMinor, t.FeesMinor, t.Currency,
                t.RPlanned, t.RRealised, t.EmotionTag, t.IsPaper,
            }));
        var fillsCsv = ToCsv(
            ["Id", "Symbol", "Side", "Qty", "Price", "FeeMinor", "FeeCurrency", "At", "Source", "MatchStatus", "TradeId"],
            fills.Select(f => new object?[]
            {
                f.Id, Symbol(f.InstrumentId), f.Side, f.Qty, f.Price, f.FeeMinor, f.FeeCurrency, f.At,
                f.Source, f.MatchStatus, f.TradeId,
            }));
        var lotsByHolding = lots.GroupBy(l => l.HoldingId).ToDictionary(g => g.Key, g => g.ToList());
        var holdingsCsv = ToCsv(
            ["Id", "Symbol", "BucketId", "LotCount", "TotalQty", "TotalCostMinor"],
            holdings.Select(h =>
            {
                var holdingLots = lotsByHolding.GetValueOrDefault(h.Id);
                return new object?[]
                {
                    h.Id, Symbol(h.InstrumentId), h.BucketId, holdingLots?.Count ?? 0,
                    holdingLots?.Sum(l => l.Qty) ?? 0m, holdingLots?.Sum(l => l.CostMinor) ?? 0L,
                };
            }));

        var fileName = $"edgewise-export-{DateTime.UtcNow:yyyy-MM-dd}.zip";

        // ZipArchive still performs the occasional synchronous write between entries
        // (even via its async APIs), which servers reject by default mid-stream.
        var bodyControl = http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpBodyControlFeature>();
        if (bodyControl is not null)
        {
            bodyControl.AllowSynchronousIO = true;
        }

        return Results.Stream(
            async (stream) =>
            {
                await using var zip = await ZipArchive.CreateAsync(
                    stream, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: null, cancellationToken: ct);
                foreach (var (name, payload) in entries)
                {
                    await WriteEntryAsync(zip, name, JsonSerializer.SerializeToUtf8Bytes(payload, Json), ct);
                }

                await WriteEntryAsync(zip, "trades.csv", Encoding.UTF8.GetBytes(tradesCsv), ct);
                await WriteEntryAsync(zip, "fills.csv", Encoding.UTF8.GetBytes(fillsCsv), ct);
                await WriteEntryAsync(zip, "holdings.csv", Encoding.UTF8.GetBytes(holdingsCsv), ct);
            },
            contentType: "application/zip",
            fileDownloadName: fileName);
    }

    private static async Task WriteEntryAsync(ZipArchive zip, string name, byte[] content, CancellationToken ct)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
        await using var entryStream = await entry.OpenAsync(ct);
        await entryStream.WriteAsync(content, ct);
    }

    /// <summary>Minimal RFC-4180 CSV writer (invariant culture, ISO timestamps).</summary>
    private static string ToCsv(string[] header, IEnumerable<object?[]> rows)
    {
        var sb = new StringBuilder();
        sb.AppendJoin(',', header.Select(CsvField)).Append('\n');
        foreach (var row in rows)
        {
            sb.AppendJoin(',', row.Select(v => CsvField(Format(v)))).Append('\n');
        }

        return sb.ToString();
    }

    private static string Format(object? value) => value switch
    {
        null => string.Empty,
        DateTime dt => dt.ToString("O"),
        DateOnly d => d.ToString("O"),
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string CsvField(string value) =>
        value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;

    // -------------------------------------------------------------- delete

    private static async Task<IResult> DeleteMe(HttpContext http, EdgewiseDbContext db, CancellationToken ct)
    {
        RequireJwt(http);
        var request = await http.Request.ReadFromJsonAsync<DeleteMeRequest>(ct);
        var user = await db.Users.SingleOrDefaultAsync(ct)
            ?? throw ApiException.Unauthorized("unauthorized", "Authentication required.");

        if (string.IsNullOrEmpty(request?.Password) || !PasswordHasher.Verify(request.Password, user.PasswordHash))
        {
            throw ApiException.Unauthorized(
                "invalid_password", "Password confirmation failed; the account was not deleted.");
        }

        var userId = user.Id;

        // Tombstone first: proof of erasure keyed by hashed identifiers only.
        var tombstone = new AuditLog
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            EntityType = "User",
            EntityId = TokenHasher.Sha256Hex(user.Email.Trim().ToLowerInvariant()),
            Action = "AccountDeleted",
            DiffJson = JsonSerializer.Serialize(new
            {
                emailHash = TokenHasher.Sha256Hex(user.Email.Trim().ToLowerInvariant()),
                userIdHash = TokenHasher.Sha256Hex(userId.ToString()),
                deletedAt = DateTime.UtcNow,
            }),
            At = DateTime.UtcNow,
        };

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        db.AuditLogs.Add(tombstone);
        await db.SaveChangesAsync(ct);

        // Hard delete in FK-safe order: children before parents. Every set below is
        // narrowed to the current user by the global query filters; the two shared
        // tables (PlaybookTemplates, CatalystEvents) get an explicit owner predicate
        // because their filters also expose global rows.
        await db.Attachments.ExecuteDeleteAsync(ct);
        await db.JournalEntries.ExecuteDeleteAsync(ct);
        await db.AdherenceResults.ExecuteDeleteAsync(ct);
        await db.TradeTags.ExecuteDeleteAsync(ct);
        await db.TradePlanVersions.ExecuteDeleteAsync(ct);
        await db.Fills.ExecuteDeleteAsync(ct);
        await db.Trades.ExecuteDeleteAsync(ct);
        await db.TradePlans.ExecuteDeleteAsync(ct);
        await db.Tags.ExecuteDeleteAsync(ct);
        await db.PlaybookTemplates.Where(t => t.UserId == userId).ExecuteDeleteAsync(ct);
        await db.CatalystEvents.Where(c => c.UserId == userId).ExecuteDeleteAsync(ct);
        await db.Dividends.ExecuteDeleteAsync(ct);
        await db.Lots.ExecuteDeleteAsync(ct);
        await db.Holdings.ExecuteDeleteAsync(ct);
        await db.WatchlistItems.ExecuteDeleteAsync(ct);
        await db.Watchlists.ExecuteDeleteAsync(ct);
        await db.Alerts.ExecuteDeleteAsync(ct);
        await db.Notifications.ExecuteDeleteAsync(ct);
        await db.PushSubscriptions.ExecuteDeleteAsync(ct);
        await db.Zones.ExecuteDeleteAsync(ct);
        await db.Backtests.ExecuteDeleteAsync(ct);
        await db.PipelineStateChanges.ExecuteDeleteAsync(ct);
        await db.StrategyVersions.ExecuteDeleteAsync(ct);
        await db.Strategies.ExecuteDeleteAsync(ct);
        await db.ImportMappings.ExecuteDeleteAsync(ct);
        await db.Insights.ExecuteDeleteAsync(ct);
        await db.LlmRequestLogs.ExecuteDeleteAsync(ct);
        await db.WeeklyReviews.ExecuteDeleteAsync(ct);
        await db.BrierForecasts.ExecuteDeleteAsync(ct);
        await db.DecisionLogs.ExecuteDeleteAsync(ct);
        await db.OverrideLogs.ExecuteDeleteAsync(ct);
        await db.CashFlows.ExecuteDeleteAsync(ct);
        await db.Snapshots.ExecuteDeleteAsync(ct);
        await db.Forecasts.ExecuteDeleteAsync(ct);
        await db.Accounts.ExecuteDeleteAsync(ct);
        await db.Buckets.ExecuteDeleteAsync(ct);
        await db.RiskProfiles.ExecuteDeleteAsync(ct);

        // Token revocation: PATs and refresh tokens are destroyed outright.
        await db.ApiTokens.ExecuteDeleteAsync(ct);
        await db.RefreshTokens.ExecuteDeleteAsync(ct);

        // Everything but the tombstone, then the user row itself.
        await db.AuditLogs.Where(a => a.Id != tombstone.Id).ExecuteDeleteAsync(ct);
        await db.Users.ExecuteDeleteAsync(ct);

        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }
}
