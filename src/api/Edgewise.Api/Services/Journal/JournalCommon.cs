using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Auth;
using Edgewise.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Edgewise.Api.Services.Journal;

/// <summary>Shared helpers for the journal vertical.</summary>
public static class JournalCommon
{
    /// <summary>
    /// Money convention: minor units are hundredths of the major unit (cents).
    /// Prices/quantities stay decimal; conversion happens once per realisation.
    /// </summary>
    public static long ToMinor(decimal major) => (long)decimal.Round(major * 100m, 0, MidpointRounding.AwayFromZero);

    public static decimal ToMajor(long minor) => minor / 100m;

    /// <summary>Normalises an incoming timestamp to UTC (Npgsql requires Kind=Utc for timestamptz).</summary>
    public static DateTime AsUtc(DateTime at) => at.Kind switch
    {
        DateTimeKind.Utc => at,
        DateTimeKind.Local => at.ToUniversalTime(),
        _ => DateTime.SpecifyKind(at, DateTimeKind.Utc),
    };

    public static Guid RequireUserId(ICurrentUser currentUser) =>
        currentUser.UserId ?? throw ApiException.Unauthorized("unauthorized", "Authentication required.");

    /// <summary>
    /// Case-insensitive enum parse for query-string parameters (the JSON contract is
    /// camelCase, so clients naturally send e.g. ?status=open). Null/empty parses to null.
    /// </summary>
    public static TEnum? ParseEnum<TEnum>(string? value, string paramName)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed)
            ? parsed
            : throw ApiException.BadRequest("invalid_" + paramName, $"'{value}' is not a valid {paramName}.");
    }

    public static InstrumentSummaryDto ToSummary(Instrument i) =>
        new(i.Id, i.Symbol, i.Name, i.AssetClass, i.Currency, i.Exchange);

    public static PlanDto ToDto(TradePlan p, Instrument? instrument = null) => new(
        p.Id, p.TemplateId, p.InstrumentId, p.BucketId, p.RiskProfileId, p.Direction, p.SetupTag,
        p.TriggerText, p.StopPrice, p.TargetRuleJson, p.SizeQty, p.SizeOverridden, p.InvalidationNote,
        p.IsPaper, p.Status, p.CockpitCheckJson, p.ChecklistConfirmedJson, p.CreatedAt,
        instrument is null ? null : ToSummary(instrument));

    public static FillDto ToDto(Fill f) => new(
        f.Id, f.AccountId, f.InstrumentId, f.TradeId, f.Side, f.Qty, f.Price, f.FeeMinor,
        f.FeeCurrency, f.At, f.Source, f.MatchStatus);

    public static TradeDto ToDto(Trade t, Instrument? instrument = null) => new(
        t.Id, t.PlanId, t.InstrumentId, t.BucketId, t.AccountId, t.Direction, t.Status, t.OpenedAt,
        t.ClosedAt, t.Qty, t.AvgEntryPrice, t.AvgExitPrice, t.RealisedPnlMinor, t.FeesMinor,
        t.FundingMinor, t.Currency, t.RPlanned, t.RRealised, t.MaePct, t.MfePct, t.HoldingSeconds,
        t.EmotionTag, t.IsPaper, instrument is null ? null : ToSummary(instrument));

    public static AdherenceResultDto ToDto(AdherenceResult r) =>
        new(r.Id, r.RubricVersion, r.Score, r.Grade, r.DeductionsJson, r.ComputedAt);

    /// <summary>Resolves the user's IANA timezone, falling back to UTC.</summary>
    public static async Task<TimeZoneInfo> GetUserTimeZoneAsync(EdgewiseDbContext db, CancellationToken ct)
    {
        var tz = await db.Users.Select(u => u.Timezone).FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(tz))
        {
            return TimeZoneInfo.Utc;
        }

        return TimeZoneInfo.TryFindSystemTimeZoneById(tz, out var info) ? info! : TimeZoneInfo.Utc;
    }
}
