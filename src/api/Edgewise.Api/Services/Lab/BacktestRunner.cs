using System.Text.Json;
using Edgewise.Domain.Engines.Backtesting;
using Edgewise.Domain.Engines.Indicators;
using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Auth;
using Edgewise.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Edgewise.Api.Services.Lab;

/// <summary>The resolved cost + risk configuration persisted on a Backtest row (CostModelJson).</summary>
public sealed record BacktestConfig(
    string Venue,
    decimal CommissionPctPerSide,
    decimal SpreadPct,
    decimal SlippagePct,
    decimal FundingPctPer8h,
    decimal RiskPctPerTrade,
    long EquityStartMinor);

/// <summary>
/// Executes a queued backtest: loads bars from PriceBar, maps the strategy version's
/// rule tree onto the domain records, runs <see cref="Backtester.Build"/> and persists
/// the result + honesty report. Runs either as a Hangfire job (no HTTP user - all reads
/// ignore the per-user query filters; ownership was enforced when the row was created)
/// or inline when Hangfire is disabled.
/// </summary>
public sealed class BacktestRunner(EdgewiseDbContext db)
{
    public const int MinBars = 100;

    /// <summary>Venue cost presets, in percent. "custom" means caller-supplied values.</summary>
    public static readonly IReadOnlyDictionary<string, (decimal Commission, decimal Spread, decimal Slippage, decimal Funding)> VenuePresets =
        new Dictionary<string, (decimal, decimal, decimal, decimal)>(StringComparer.OrdinalIgnoreCase)
        {
            ["binance"] = (0.1m, 0.02m, 0.03m, 0m),
            ["jse"] = (0.25m, 0.15m, 0.1m, 0m),
            ["forex"] = (0m, 0.05m, 0.02m, 0.01m),
        };

    public async Task RunAsync(Guid backtestId, CancellationToken ct = default)
    {
        var backtest = await db.Backtests.IgnoreQueryFilters()
            .SingleOrDefaultAsync(b => b.Id == backtestId, ct);
        if (backtest is null || backtest.Status is BacktestStatus.Done)
        {
            return;
        }

        backtest.Status = BacktestStatus.Running;
        await db.SaveChangesAsync(ct);

        try
        {
            var version = await db.StrategyVersions.IgnoreQueryFilters()
                .SingleAsync(v => v.Id == backtest.StrategyVersionId, ct);
            var config = LabJson.Deserialize<BacktestConfig>(backtest.CostModelJson
                ?? throw new InvalidOperationException("Backtest has no cost model."))!;

            var instrumentIds = LabJson.Deserialize<List<Guid>>(backtest.InstrumentIdsJson) ?? [];
            if (instrumentIds.Count == 0)
            {
                throw new InvalidOperationException("Backtest has no instrument.");
            }

            var instrumentId = instrumentIds[0];
            var bars = await db.PriceBars
                .Where(p => p.InstrumentId == instrumentId
                    && p.Timeframe == backtest.Timeframe
                    && p.Ts >= backtest.RangeStart
                    && p.Ts <= backtest.RangeEnd)
                .OrderBy(p => p.Ts)
                .Select(p => new Bar(p.Ts, p.O, p.H, p.L, p.C, p.V))
                .ToListAsync(ct);

            if (bars.Count < MinBars)
            {
                throw new ApiException(422, "insufficient_bars",
                    $"Backtesting needs at least {MinBars} stored bars in the selected range; found {bars.Count}. "
                    + "Widen the date range, pick a coarser timeframe, or choose an instrument with stored history.");
            }

            using var ruleTreeDoc = JsonDocument.Parse(version.RuleTreeJson);
            var (tree, ladder, riskPct) = RuleTreeValidator.Map(ruleTreeDoc.RootElement);

            var costs = new CostModel(
                config.CommissionPctPerSide, config.SpreadPct, config.SlippagePct, config.FundingPctPer8h);
            var risk = new RiskModel(
                config.RiskPctPerTrade > 0m ? config.RiskPctPerTrade : riskPct,
                config.EquityStartMinor);

            var honesty = Backtester.Build(bars, tree, ladder, costs, risk);

            backtest.ResultJson = LabJson.Serialize(honesty.FullSample);
            backtest.HonestyJson = LabJson.Serialize(honesty);
            backtest.Status = BacktestStatus.Done;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var message = ex is ApiException apiEx ? apiEx.Message : $"Backtest failed: {ex.Message}";
            backtest.ResultJson = LabJson.Serialize(new { error = message });
            backtest.HonestyJson = null;
            backtest.Status = BacktestStatus.Failed;
        }

        await db.SaveChangesAsync(ct);
    }
}
