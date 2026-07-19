using Edgewise.Domain.Engines.Indicators;

namespace Edgewise.Domain.Engines.Backtesting;

/// <summary>
/// The honest backtester. Single instrument, bar-driven, pure and deterministic.
///
/// No-look-ahead is structural:
/// - every condition series is strictly causal (value at t depends only on bars[0..t]);
/// - a signal on bar t results in an entry at bars[t+1].Open (cost-adjusted);
/// - a signal on the final bar produces no trade (there is no t+1 to enter on);
/// - stops/targets/trails are armed from values known at or before the bar they act on.
///
/// Intra-bar conservatism: if a bar's range reaches both the stop and a target/partial,
/// the STOP fills first (worst case). Gap-through stops fill at the bar's open when the
/// open is already beyond the stop.
/// </summary>
public static class Backtester
{
    /// <summary>Minimum bars each side of the 70/30 split needs for an out-of-sample run.</summary>
    public const int MinSplitBars = 50;

    public const int AtrPeriod = 14;
    public const decimal DefaultStopAtrMult = 2m;

    public static BacktestResult Run(
        IReadOnlyList<Bar> bars,
        RuleTree tree,
        ExitLadder ladder,
        CostModel costs,
        RiskModel risk)
    {
        Validate(bars, tree, ladder, costs, risk);

        var n = bars.Count;
        var series = tree.Conditions
            .Select(c => c.Enabled ? BuildSeries(c, bars) : null)
            .ToArray();
        var atr = IndicatorLibrary.Atr(bars, AtrPeriod);

        var isLong = tree.Direction == TradeDirection.Long;
        var dir = isLong ? 1m : -1m;
        var trades = new List<Trade>();
        var equityCurve = new decimal[n];
        decimal cash = risk.EquityStartMinor;
        var barsExposed = 0;

        // Open-position state.
        var open = false;
        var pendingEntry = false;
        decimal pendingStopDistance = 0m; // 0 = percent stop, resolved against the entry fill
        decimal entryFill = 0m, stopPx = 0m, stopDistance = 0m, qty = 0m, initialQty = 0m;
        decimal initialRiskMinor = 0m, cashBeforeEntry = 0m, peakClose = 0m;
        var partialDone = false;
        var breakevenArmed = false;
        var barsHeld = 0;
        DateTime entryTs = default;

        // Settles a fill at an ALREADY cost-adjusted price: moves cash by the signed
        // notional and deducts per-side commission.
        void Settle(decimal quantity, decimal adjPx, bool isBuy)
        {
            var notional = quantity * adjPx;
            var commission = Math.Abs(notional) * costs.CommissionPctPerSide / 100m;
            cash += (isBuy ? -notional : notional) - commission;
        }

        // Closes the remaining position at a RAW price (cost adjustment applied here).
        void CloseTrade(int barIdx, decimal rawPx)
        {
            var fill = AdjustFill(rawPx, isBuy: !isLong, costs);
            Settle(qty, fill, isBuy: !isLong);
            var pnl = cash - cashBeforeEntry;
            trades.Add(new Trade(
                entryTs, bars[barIdx].Ts, tree.Direction, entryFill, fill,
                initialRiskMinor == 0m ? 0m : pnl / initialRiskMinor, pnl));
            open = false;
            qty = 0m;
        }

        for (var i = 0; i < n; i++)
        {
            var bar = bars[i];

            if (pendingEntry)
            {
                pendingEntry = false;
                entryFill = AdjustFill(bar.O, isBuy: isLong, costs);
                stopDistance = pendingStopDistance > 0m
                    ? pendingStopDistance
                    : entryFill * (ladder.StopPct ?? 0m) / 100m;

                if (stopDistance > 0m && cash > 0m)
                {
                    qty = cash * risk.RiskPctPerTrade / 100m / stopDistance;
                    if (qty > 0m)
                    {
                        open = true;
                        initialQty = qty;
                        initialRiskMinor = qty * stopDistance;
                        stopPx = entryFill - (dir * stopDistance);
                        cashBeforeEntry = cash;
                        Settle(qty, entryFill, isBuy: isLong);
                        partialDone = false;
                        breakevenArmed = false;
                        barsHeld = 0;
                        peakClose = entryFill;
                        entryTs = bar.Ts;
                    }
                }
            }

            if (open)
            {
                barsExposed++;
                barsHeld++;

                // 1) Stop check FIRST (worst case). Gap-through fills at the open.
                var stopHit = isLong ? bar.L <= stopPx : bar.H >= stopPx;
                if (stopHit)
                {
                    var raw = isLong ? Math.Min(bar.O, stopPx) : Math.Max(bar.O, stopPx);
                    CloseTrade(i, raw);
                }
                else
                {
                    // 2) Partial take-profit (only reachable when the stop was not hit).
                    if (!partialDone && ladder.PartialTakeAtR is decimal pr && ladder.PartialPct > 0m)
                    {
                        var target = entryFill + (dir * pr * stopDistance);
                        var targetHit = isLong ? bar.H >= target : bar.L <= target;
                        if (targetHit)
                        {
                            var partialQty = Math.Min(initialQty * ladder.PartialPct / 100m, qty);
                            if (partialQty > 0m)
                            {
                                Settle(partialQty, AdjustFill(target, isBuy: !isLong, costs), isBuy: !isLong);
                                qty -= partialQty;
                            }

                            partialDone = true;
                        }
                    }

                    // 3) Breakeven arming (takes effect from the NEXT bar via stop update below).
                    var armBreakevenNow = false;
                    if (!breakevenArmed && ladder.BreakevenAtR is decimal ber)
                    {
                        var trigger = entryFill + (dir * ber * stopDistance);
                        armBreakevenNow = isLong ? bar.H >= trigger : bar.L <= trigger;
                    }

                    // 4) Time stop: exit at the close of the Nth held bar; end of data force-close.
                    if (ladder.TimeStopBars is int tsb && barsHeld >= tsb)
                    {
                        CloseTrade(i, bar.C);
                    }
                    else if (i == n - 1)
                    {
                        CloseTrade(i, bar.C);
                    }

                    if (open)
                    {
                        // 5) Funding accrual on the marked notional per 8h held.
                        if (costs.FundingPctPer8h != 0m)
                        {
                            var hours = i > 0
                                ? (decimal)(bar.Ts - bars[i - 1].Ts).TotalHours
                                : 24m;
                            cash -= Math.Abs(qty * bar.C) * costs.FundingPctPer8h / 100m * (hours / 8m);
                        }

                        // 6) Trail update on bar close (effective from the next bar).
                        peakClose = isLong ? Math.Max(peakClose, bar.C) : Math.Min(peakClose, bar.C);
                        decimal? candidate = ladder.TrailMethod switch
                        {
                            TrailMethod.AtrMult when atr[i] is decimal a =>
                                bar.C - (dir * ladder.TrailParam * a),
                            TrailMethod.PctFromPeak =>
                                peakClose * (1m - (dir * ladder.TrailParam / 100m)),
                            _ => null,
                        };
                        if (candidate is decimal cand)
                        {
                            stopPx = isLong ? Math.Max(stopPx, cand) : Math.Min(stopPx, cand);
                        }

                        // 7) Apply breakeven from the next bar onward.
                        if (armBreakevenNow)
                        {
                            breakevenArmed = true;
                            stopPx = isLong ? Math.Max(stopPx, entryFill) : Math.Min(stopPx, entryFill);
                        }
                    }
                }
            }

            // Mark-to-close equity.
            equityCurve[i] = cash + (open ? dir * qty * bar.C : 0m);

            // Signal evaluation uses bars[0..i] only; the entry happens at bars[i+1].Open,
            // so a signal on the final bar can never become a trade.
            if (!open && !pendingEntry && i + 1 < n && SignalAt(i, tree, series))
            {
                if (ladder.StopAtrMult is not null || ladder.StopPct is null)
                {
                    // ATR-based stop (explicit or the 2xATR14 default): needs ATR at signal time.
                    var mult = ladder.StopAtrMult ?? DefaultStopAtrMult;
                    if (atr[i] is decimal a && a > 0m)
                    {
                        pendingEntry = true;
                        pendingStopDistance = mult * a;
                    }
                }
                else
                {
                    // Percent stop: distance depends on the entry fill; resolved at entry.
                    pendingEntry = true;
                    pendingStopDistance = 0m;
                }
            }
        }

        return Summarize(trades, equityCurve, barsExposed, n);
    }

    /// <summary>Builds the full honesty report; see <see cref="HonestyReport"/>.</summary>
    public static HonestyReport Build(
        IReadOnlyList<Bar> bars,
        RuleTree tree,
        ExitLadder ladder,
        CostModel costs,
        RiskModel risk)
    {
        var full = Run(bars, tree, ladder, costs, risk);

        BacktestResult? inSample = null, outOfSample = null;
        var isCount = (int)(bars.Count * 0.7);
        var oosCount = bars.Count - isCount;
        if (isCount >= MinSplitBars && oosCount >= MinSplitBars)
        {
            inSample = Run(Slice(bars, 0, isCount), tree, ladder, costs, risk);
            outOfSample = Run(Slice(bars, isCount, oosCount), tree, ladder, costs, risk);
        }

        var doubledCosts = Run(bars, tree, ladder, costs with
        {
            CommissionPctPerSide = costs.CommissionPctPerSide * 2m,
            SpreadPct = costs.SpreadPct * 2m,
            SlippagePct = costs.SlippagePct * 2m,
            FundingPctPer8h = costs.FundingPctPer8h * 2m,
        }, risk);

        var wiggles = new List<WiggleEntry>();
        for (var ci = 0; ci < tree.Conditions.Count; ci++)
        {
            var cond = tree.Conditions[ci];
            if (!cond.Enabled)
            {
                continue;
            }

            foreach (var (name, original) in EnumerateOperands(cond))
            {
                foreach (var factor in new[] { 0.8m, 1.2m })
                {
                    var wiggled = original * factor;
                    var newCond = name == nameof(Condition.Operand)
                        ? cond with { Operand = wiggled }
                        : cond with { Operand2 = wiggled };
                    var conditions = tree.Conditions.ToArray();
                    conditions[ci] = newCond;
                    var result = Run(bars, tree with { Conditions = conditions }, ladder, costs, risk);
                    wiggles.Add(new WiggleEntry(
                        ci, name, original, wiggled, result.ExpectancyR - full.ExpectancyR));
                }
            }
        }

        var maxSensitivity = wiggles.Count == 0 ? 0m : wiggles.Max(w => Math.Abs(w.ExpectancyRDelta));
        var verdict = full.N >= 300 ? SampleVerdict.Adequate
            : full.N >= 100 ? SampleVerdict.Thin
            : SampleVerdict.Exploratory;
        var redFlag = verdict == SampleVerdict.Exploratory;
        var isExploratory = outOfSample is null || verdict == SampleVerdict.Exploratory;

        return new HonestyReport(
            full, inSample, outOfSample, doubledCosts,
            wiggles, maxSensitivity, verdict, redFlag, isExploratory);
    }

    private static IEnumerable<(string Name, decimal Value)> EnumerateOperands(Condition c)
    {
        yield return (nameof(Condition.Operand), c.Operand);
        if (c.Operand2 is decimal o2)
        {
            yield return (nameof(Condition.Operand2), o2);
        }
    }

    private static IReadOnlyList<Bar> Slice(IReadOnlyList<Bar> bars, int start, int count)
    {
        var outp = new Bar[count];
        for (var i = 0; i < count; i++)
        {
            outp[i] = bars[start + i];
        }

        return outp;
    }

    /// <summary>Buy fills pay up by slippage + half the spread; sell fills receive less.</summary>
    private static decimal AdjustFill(decimal px, bool isBuy, CostModel costs)
        => px * (1m + ((isBuy ? 1m : -1m) * (costs.SlippagePct + (costs.SpreadPct / 2m)) / 100m));

    private static bool SignalAt(int t, RuleTree tree, decimal?[]?[] series)
    {
        for (var c = 0; c < tree.Conditions.Count; c++)
        {
            var cond = tree.Conditions[c];
            if (!cond.Enabled)
            {
                continue;
            }

            var s = series[c]!;
            if (s[t] is not decimal v)
            {
                return false;
            }

            var ok = cond.Operator switch
            {
                ConditionOperator.Gt => v > cond.Operand,
                ConditionOperator.Lt => v < cond.Operand,
                ConditionOperator.CrossAbove =>
                    t > 0 && s[t - 1] is decimal p1 && p1 <= cond.Operand && v > cond.Operand,
                ConditionOperator.CrossBelow =>
                    t > 0 && s[t - 1] is decimal p2 && p2 >= cond.Operand && v < cond.Operand,
                ConditionOperator.Within => v >= cond.Operand && v <= cond.Operand2!.Value,
                _ => false,
            };
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Builds the causal scalar series for one condition (see <see cref="IndicatorKind"/>).</summary>
    private static decimal?[] BuildSeries(Condition c, IReadOnlyList<Bar> bars)
    {
        var n = bars.Count;
        var outp = new decimal?[n];
        switch (c.Indicator)
        {
            case IndicatorKind.PriceVsMa:
                {
                    var period = (int)Param(c, "period", 20m);
                    var sma = IndicatorLibrary.Sma(bars, period);
                    for (var i = 0; i < n; i++)
                    {
                        if (sma[i] is decimal m && m != 0m)
                        {
                            outp[i] = (bars[i].C - m) / m;
                        }
                    }

                    break;
                }

            case IndicatorKind.RsiBand:
                {
                    var period = (int)Param(c, "period", 14m);
                    outp = IndicatorLibrary.Rsi(bars, period);
                    break;
                }

            case IndicatorKind.BreakoutNBarHigh:
                {
                    var lookback = (int)Param(c, "n", 20m);
                    if (lookback <= 0)
                    {
                        throw new ArgumentOutOfRangeException(nameof(c), "BreakoutNBarHigh 'n' must be positive.");
                    }

                    for (var i = lookback; i < n; i++)
                    {
                        var maxHigh = decimal.MinValue;
                        for (var j = i - lookback; j < i; j++)
                        {
                            maxHigh = Math.Max(maxHigh, bars[j].H);
                        }

                        if (maxHigh != 0m)
                        {
                            outp[i] = (bars[i].C - maxHigh) / maxHigh;
                        }
                    }

                    break;
                }

            case IndicatorKind.VolumeVsAvg:
                {
                    var period = (int)Param(c, "period", 20m);
                    var vols = new decimal[n];
                    for (var i = 0; i < n; i++)
                    {
                        vols[i] = bars[i].V;
                    }

                    var avg = IndicatorLibrary.SmaOf(vols, period);
                    for (var i = 0; i < n; i++)
                    {
                        if (avg[i] is decimal a && a != 0m)
                        {
                            outp[i] = bars[i].V / a;
                        }
                    }

                    break;
                }

            case IndicatorKind.MacdCross:
                {
                    var fast = (int)Param(c, "fast", 12m);
                    var slow = (int)Param(c, "slow", 26m);
                    var signal = (int)Param(c, "signal", 9m);
                    var (_, _, hist) = IndicatorLibrary.Macd(bars, fast, slow, signal);
                    outp = hist;
                    break;
                }

            case IndicatorKind.PriceVsBollinger:
                {
                    var period = (int)Param(c, "period", 20m);
                    var sd = Param(c, "sd", 2m);
                    var (mid, upper, _) = IndicatorLibrary.Bollinger(bars, period, sd);
                    for (var i = 0; i < n; i++)
                    {
                        if (mid[i] is decimal m && upper[i] is decimal u)
                        {
                            var half = u - m;
                            outp[i] = half == 0m ? 0m : (bars[i].C - m) / half;
                        }
                    }

                    break;
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(c), c.Indicator, "Unknown indicator kind.");
        }

        return outp;
    }

    private static decimal Param(Condition c, string name, decimal fallback)
        => c.Params is not null && c.Params.TryGetValue(name, out var v) ? v : fallback;

    private static BacktestResult Summarize(
        List<Trade> trades, decimal[] equityCurve, int barsExposed, int totalBars)
    {
        var n = trades.Count;
        var winRate = n == 0 ? 0m : (decimal)trades.Count(t => t.PnlMinor > 0m) / n;
        var avgR = n == 0 ? 0m : trades.Average(t => t.RMultiple);

        decimal peak = decimal.MinValue, maxDd = 0m;
        foreach (var eq in equityCurve)
        {
            peak = Math.Max(peak, eq);
            if (peak > 0m)
            {
                maxDd = Math.Max(maxDd, (peak - eq) / peak * 100m);
            }
        }

        var exposure = totalBars == 0 ? 0m : (decimal)barsExposed / totalBars * 100m;
        return new BacktestResult(n, winRate, avgR, avgR, maxDd, exposure, trades, equityCurve);
    }

    private static void Validate(
        IReadOnlyList<Bar> bars, RuleTree tree, ExitLadder ladder, CostModel costs, RiskModel risk)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(ladder);
        ArgumentNullException.ThrowIfNull(costs);
        ArgumentNullException.ThrowIfNull(risk);

        // Honesty by construction: a backtest with zero trading costs is not allowed.
        if (costs.CommissionPctPerSide == 0m && costs.SpreadPct == 0m
            && costs.SlippagePct == 0m && costs.FundingPctPer8h == 0m)
        {
            throw new ArgumentException(
                "CostModel has all-zero costs; an honest backtest requires at least one non-zero cost component.",
                nameof(costs));
        }

        if (costs.CommissionPctPerSide < 0m || costs.SpreadPct < 0m
            || costs.SlippagePct < 0m || costs.FundingPctPer8h < 0m)
        {
            throw new ArgumentException("CostModel components cannot be negative.", nameof(costs));
        }

        if (tree.Conditions is null || tree.Conditions.Count == 0 || tree.Conditions.All(c => !c.Enabled))
        {
            throw new ArgumentException("RuleTree must contain at least one enabled condition.", nameof(tree));
        }

        foreach (var cond in tree.Conditions)
        {
            if (cond.Enabled && cond.Operator == ConditionOperator.Within && cond.Operand2 is null)
            {
                throw new ArgumentException("Within operator requires Operand2.", nameof(tree));
            }
        }

        if (risk.RiskPctPerTrade <= 0m || risk.RiskPctPerTrade > 100m)
        {
            throw new ArgumentOutOfRangeException(nameof(risk), risk.RiskPctPerTrade, "RiskPctPerTrade must be in (0, 100].");
        }

        if (risk.EquityStartMinor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(risk), risk.EquityStartMinor, "EquityStartMinor must be positive.");
        }

        if (ladder.StopPct is <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(ladder), ladder.StopPct, "StopPct must be positive when set.");
        }

        if (ladder.StopAtrMult is <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(ladder), ladder.StopAtrMult, "StopAtrMult must be positive when set.");
        }

        if (ladder.PartialTakeAtR is not null && (ladder.PartialPct <= 0m || ladder.PartialPct >= 100m))
        {
            throw new ArgumentOutOfRangeException(nameof(ladder), ladder.PartialPct, "PartialPct must be in (0, 100) when PartialTakeAtR is set.");
        }
    }
}
