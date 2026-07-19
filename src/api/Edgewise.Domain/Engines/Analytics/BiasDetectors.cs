using Edgewise.Domain.Engines.Adherence;

namespace Edgewise.Domain.Engines.Analytics;

/// <summary>
/// Input for bias detection. <see cref="Key"/> is a caller-supplied opaque trade key used
/// for evidence lists. <see cref="RiskAmount"/> is the position risk taken (any consistent
/// unit). <see cref="TriggerPrice"/>/<see cref="EntryPrice"/> are optional and only used by
/// the FOMO-chase detector.
/// </summary>
public sealed record BiasTrade
{
    public required string Key { get; init; }
    public DateTime EntryAt { get; init; }
    public DateTime ClosedAt { get; init; }
    public bool IsWin { get; init; }
    public decimal? RRealised { get; init; }
    public long? HoldingSeconds { get; init; }
    public decimal? RiskAmount { get; init; }
    public decimal? TriggerPrice { get; init; }
    public decimal? EntryPrice { get; init; }
    public TradeDirection Direction { get; init; } = TradeDirection.Long;
}

/// <summary>
/// Result of one bias detector: the measured metric, the breach threshold, whether it breached,
/// the trade keys forming the evidence, and named figures ready for narrative rendering.
/// </summary>
public sealed record BiasCard(
    string Code,
    decimal Metric,
    decimal Threshold,
    bool Breached,
    IReadOnlyList<string> EvidenceKeys,
    IReadOnlyDictionary<string, decimal> Details);

/// <summary>Pure detectors for common trading biases.</summary>
public static class BiasDetectors
{
    public const string DispositionCode = "DISPOSITION";
    public const string RevengeCode = "REVENGE_ENTRIES";
    public const string SizeCreepCode = "SIZE_CREEP";
    public const string FomoChaseCode = "FOMO_CHASE";

    /// <summary>
    /// Disposition effect: average holding time of winners divided by average holding time of
    /// losers. Breach when the ratio exceeds <paramref name="threshold"/> (default 1.5).
    /// Evidence: winners held longer than the average loser hold.
    /// </summary>
    public static BiasCard DispositionRatio(IEnumerable<BiasTrade> trades, decimal threshold = 1.5m)
    {
        ArgumentNullException.ThrowIfNull(trades);

        var items = trades.ToList();
        var winners = items.Where(t => t.IsWin).ToList();
        var losers = items.Where(t => !t.IsWin).ToList();

        if (winners.Count == 0 || losers.Count == 0)
        {
            return new BiasCard(DispositionCode, 0m, threshold, false, [],
                Details(("winners", winners.Count), ("losers", losers.Count)));
        }

        var avgWinHold = (decimal)winners.Average(HoldSeconds);
        var avgLossHold = (decimal)losers.Average(HoldSeconds);
        if (avgLossHold <= 0m)
        {
            return new BiasCard(DispositionCode, 0m, threshold, false, [],
                Details(("winners", winners.Count), ("losers", losers.Count),
                    ("avgHoldWinnersSeconds", avgWinHold), ("avgHoldLosersSeconds", avgLossHold)));
        }

        var ratio = avgWinHold / avgLossHold;
        var evidence = winners
            .Where(t => (decimal)HoldSeconds(t) > avgLossHold)
            .Select(t => t.Key)
            .ToList();

        return new BiasCard(DispositionCode, ratio, threshold, ratio > threshold, evidence,
            Details(("winners", winners.Count), ("losers", losers.Count),
                ("avgHoldWinnersSeconds", avgWinHold), ("avgHoldLosersSeconds", avgLossHold)));
    }

    /// <summary>
    /// Revenge entries: trades entered within <paramref name="windowMinutes"/> after a prior
    /// loss closed, counted over the <paramref name="lookbackDays"/> before <paramref name="asOf"/>.
    /// Breach when the count is at least <paramref name="breachCount"/> (default 3 in 30 days).
    /// </summary>
    public static BiasCard RevengeEntries(
        IEnumerable<BiasTrade> trades,
        DateTime asOf,
        int windowMinutes = 30,
        int lookbackDays = 30,
        int breachCount = 3)
    {
        ArgumentNullException.ThrowIfNull(trades);

        var items = trades.ToList();
        var lossCloses = items.Where(t => !t.IsWin).Select(t => t.ClosedAt).ToList();
        var windowStart = asOf.AddDays(-lookbackDays);

        var evidence = items
            .Where(t => t.EntryAt > windowStart && t.EntryAt <= asOf)
            .Where(t => lossCloses.Any(lossClose =>
                lossClose <= t.EntryAt && (t.EntryAt - lossClose).TotalMinutes < windowMinutes))
            .OrderBy(t => t.EntryAt)
            .Select(t => t.Key)
            .ToList();

        return new BiasCard(RevengeCode, evidence.Count, breachCount, evidence.Count >= breachCount,
            evidence,
            Details(("windowMinutes", windowMinutes), ("lookbackDays", lookbackDays)));
    }

    /// <summary>
    /// Size creep: average position risk of trades entered right after a streak of
    /// <paramref name="streakLength"/> consecutive wins (by close time) versus the average risk
    /// of all other trades. Breach when the ratio exceeds <paramref name="threshold"/> (default 1.25).
    /// Trades without <see cref="BiasTrade.RiskAmount"/> are ignored.
    /// </summary>
    public static BiasCard SizeCreep(
        IEnumerable<BiasTrade> trades,
        decimal threshold = 1.25m,
        int streakLength = 3)
    {
        ArgumentNullException.ThrowIfNull(trades);

        var all = trades.ToList();
        var items = all.Where(t => t.RiskAmount is not null).OrderBy(t => t.EntryAt).ToList();
        var closes = all.OrderBy(t => t.ClosedAt).ToList();

        var postStreak = new List<BiasTrade>();
        var baseline = new List<BiasTrade>();
        foreach (var trade in items)
        {
            var closedBefore = closes.Where(c => c.ClosedAt <= trade.EntryAt).ToList();
            var onStreak = closedBefore.Count >= streakLength &&
                           closedBefore.TakeLast(streakLength).All(c => c.IsWin);
            (onStreak ? postStreak : baseline).Add(trade);
        }

        if (postStreak.Count == 0 || baseline.Count == 0)
        {
            return new BiasCard(SizeCreepCode, 0m, threshold, false, [],
                Details(("postStreakTrades", postStreak.Count), ("baselineTrades", baseline.Count)));
        }

        var postAvg = postStreak.Average(t => t.RiskAmount!.Value);
        var baseAvg = baseline.Average(t => t.RiskAmount!.Value);
        if (baseAvg <= 0m)
        {
            return new BiasCard(SizeCreepCode, 0m, threshold, false, [],
                Details(("postStreakTrades", postStreak.Count), ("baselineTrades", baseline.Count)));
        }

        var ratio = postAvg / baseAvg;
        return new BiasCard(SizeCreepCode, ratio, threshold, ratio > threshold,
            postStreak.Select(t => t.Key).ToList(),
            Details(("postStreakTrades", postStreak.Count), ("baselineTrades", baseline.Count),
                ("avgRiskPostStreak", postAvg), ("avgRiskBaseline", baseAvg)));
    }

    /// <summary>
    /// FOMO chase: entries filled more than <paramref name="chaseThresholdPct"/> (fraction, 0.01 == 1%)
    /// beyond the trigger price in the adverse direction (above for longs, below for shorts),
    /// counted over the <paramref name="lookbackDays"/> before <paramref name="asOf"/>.
    /// Breach when the count is at least <paramref name="breachCount"/> (default 3 in 30 days).
    /// Trades without both trigger and entry prices are ignored.
    /// </summary>
    public static BiasCard FomoChase(
        IEnumerable<BiasTrade> trades,
        DateTime asOf,
        decimal chaseThresholdPct,
        int lookbackDays = 30,
        int breachCount = 3)
    {
        ArgumentNullException.ThrowIfNull(trades);

        var windowStart = asOf.AddDays(-lookbackDays);
        var chases = new List<(string Key, decimal ChasePct)>();
        foreach (var trade in trades.OrderBy(t => t.EntryAt))
        {
            if (trade.TriggerPrice is not decimal trigger || trigger <= 0m ||
                trade.EntryPrice is not decimal entry ||
                trade.EntryAt <= windowStart || trade.EntryAt > asOf)
            {
                continue;
            }

            var chasePct = trade.Direction == TradeDirection.Long
                ? (entry - trigger) / trigger
                : (trigger - entry) / trigger;
            if (chasePct > chaseThresholdPct)
            {
                chases.Add((trade.Key, chasePct));
            }
        }

        var details = Details(
            ("chaseThresholdPct", chaseThresholdPct),
            ("lookbackDays", lookbackDays),
            ("maxChasePct", chases.Count == 0 ? 0m : chases.Max(c => c.ChasePct)));

        return new BiasCard(FomoChaseCode, chases.Count, breachCount, chases.Count >= breachCount,
            chases.Select(c => c.Key).ToList(), details);
    }

    private static double HoldSeconds(BiasTrade trade) =>
        trade.HoldingSeconds ?? (trade.ClosedAt - trade.EntryAt).TotalSeconds;

    private static Dictionary<string, decimal> Details(params (string Key, decimal Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);
}
