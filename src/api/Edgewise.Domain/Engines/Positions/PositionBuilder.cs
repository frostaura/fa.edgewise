namespace Edgewise.Domain.Engines.Positions;

/// <summary>Side of an individual fill.</summary>
public enum Side
{
    Buy = 0,
    Sell = 1,
}

/// <summary>Direction of a trade (position) aggregated from fills.</summary>
public enum TradeDirection
{
    Long = 0,
    Short = 1,
}

/// <summary>Lifecycle status of a trade.</summary>
public enum TradeStatus
{
    Open = 0,
    Closed = 1,
}

/// <summary>
/// A single execution fill for one (account, instrument).
/// </summary>
/// <param name="Key">Stable unique identifier of the fill (e.g. exchange execution id).</param>
/// <param name="At">Execution timestamp.</param>
/// <param name="Side">Buy or Sell.</param>
/// <param name="Qty">Executed quantity. Fills with Qty &lt;= 0 are ignored.</param>
/// <param name="Price">Execution price (major units).</param>
/// <param name="FeeMinor">Fee charged for this fill, in minor currency units.</param>
/// <param name="FundingMinor">Funding paid/accrued attributed to this fill, in minor currency units.</param>
/// <param name="PinnedTradeKey">
/// When set, forces this fill into the trade group identified by the key (manual split/merge).
/// Pinned fills are processed in their own groups first; unpinned fills flow FIFO.
/// </param>
public sealed record FillEvent(
    string Key,
    DateTime At,
    Side Side,
    decimal Qty,
    decimal Price,
    long FeeMinor,
    long FundingMinor = 0,
    string? PinnedTradeKey = null);

/// <summary>A remaining (unclosed) FIFO entry lot of an open trade.</summary>
public sealed record OpenLot(decimal Qty, decimal Price);

/// <summary>
/// A trade (round trip or open position) aggregated from fills.
/// </summary>
/// <param name="TradeKey">Deterministic key: "{instrument}:{seq}" for FIFO trades, the pinned key for pinned trades.</param>
/// <param name="Direction">Long or Short.</param>
/// <param name="OpenedAt">Timestamp of the opening fill.</param>
/// <param name="ClosedAt">Timestamp of the fill that flattened the trade, null while open.</param>
/// <param name="Status">Open or Closed.</param>
/// <param name="Qty">Total quantity opened into the trade (sum of entry fills).</param>
/// <param name="AvgEntryPrice">Volume-weighted average entry price over all entry fills.</param>
/// <param name="AvgExitPrice">Volume-weighted average exit price over all exit fills, null if nothing exited.</param>
/// <param name="RealisedPnlMinor">Realised PnL in minor units (excludes fees/funding).</param>
/// <param name="FeesMinor">Fees accumulated on this trade (pro-rata split on flips).</param>
/// <param name="FundingMinor">Funding accumulated on this trade (pro-rata split on flips).</param>
/// <param name="FillKeys">Keys of every fill that touched this trade (a flip fill appears in both trades).</param>
/// <param name="RemainingQty">Quantity still open (0 for closed trades).</param>
/// <param name="OpenLots">Remaining FIFO entry lots (empty for closed trades).</param>
public sealed record TradeAggregate(
    string TradeKey,
    TradeDirection Direction,
    DateTime OpenedAt,
    DateTime? ClosedAt,
    TradeStatus Status,
    decimal Qty,
    decimal AvgEntryPrice,
    decimal? AvgExitPrice,
    long RealisedPnlMinor,
    long FeesMinor,
    long FundingMinor,
    IReadOnlyList<string> FillKeys,
    decimal RemainingQty,
    IReadOnlyList<OpenLot> OpenLots);

/// <summary>
/// Pure, deterministic FIFO position accounting engine. Rebuildable: same input always
/// produces the same output. No I/O, no clock, no shared state.
/// </summary>
public static class PositionBuilder
{
    /// <summary>
    /// Builds the trade history for one (account, instrument) fill stream.
    /// Fills are stably sorted by timestamp (input order breaks ties). Pinned fills are
    /// processed in their own groups (in order of first appearance); the remainder flows FIFO.
    /// Money math stays in decimal; <paramref name="toMinor"/> converts once per realisation event.
    /// </summary>
    public static IReadOnlyList<TradeAggregate> Build(
        string instrument,
        IEnumerable<FillEvent> fills,
        Func<decimal, long> toMinor)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        ArgumentNullException.ThrowIfNull(fills);
        ArgumentNullException.ThrowIfNull(toMinor);

        // Stable sort by timestamp; LINQ OrderBy is stable so ties keep input order.
        var ordered = fills
            .Where(f => f is not null && f.Qty > 0m)
            .OrderBy(f => f.At)
            .ToList();

        var states = new List<TradeState>();

        // Pinned groups first, in order of first appearance (deterministic).
        foreach (var group in ordered
                     .Where(f => f.PinnedTradeKey is not null)
                     .GroupBy(f => f.PinnedTradeKey!))
        {
            var pinSeq = 0;
            ProcessGroup(
                group.ToList(),
                toMinor,
                states,
                () =>
                {
                    pinSeq++;
                    return pinSeq == 1 ? group.Key : $"{group.Key}:{pinSeq}";
                });
        }

        // Remainder flows FIFO with generated "{instrument}:{seq}" keys.
        var fifoSeq = 0;
        ProcessGroup(
            ordered.Where(f => f.PinnedTradeKey is null).ToList(),
            toMinor,
            states,
            () => $"{instrument}:{++fifoSeq}");

        return states
            .OrderBy(t => t.OpenedAt)
            .ThenBy(t => t.CreatedIndex)
            .Select(t => t.ToAggregate())
            .ToList();
    }

    /// <summary>
    /// Maximum adverse / favourable excursion, as percentages of entry price (e.g. 2.5m = 2.5%).
    /// Excursions that never occurred clamp to 0. Empty bars or non-positive entry return (0, 0).
    /// </summary>
    public static (decimal MaePct, decimal MfePct) ComputeMaeMfe(
        decimal entryPrice,
        TradeDirection direction,
        IReadOnlyList<(decimal High, decimal Low)> bars)
    {
        if (entryPrice <= 0m || bars is null || bars.Count == 0)
        {
            return (0m, 0m);
        }

        var maxHigh = bars.Max(b => b.High);
        var minLow = bars.Min(b => b.Low);

        decimal adverse;
        decimal favourable;
        if (direction == TradeDirection.Long)
        {
            adverse = (entryPrice - minLow) / entryPrice;
            favourable = (maxHigh - entryPrice) / entryPrice;
        }
        else
        {
            adverse = (maxHigh - entryPrice) / entryPrice;
            favourable = (entryPrice - minLow) / entryPrice;
        }

        return (Math.Max(0m, adverse) * 100m, Math.Max(0m, favourable) * 100m);
    }

    private static void ProcessGroup(
        List<FillEvent> fills,
        Func<decimal, long> toMinor,
        List<TradeState> states,
        Func<string> nextKey)
    {
        TradeState? open = null;

        foreach (var fill in fills)
        {
            var fillDirection = fill.Side == Side.Buy ? TradeDirection.Long : TradeDirection.Short;

            if (open is null)
            {
                // Flat: a fill opens a new trade in its own direction.
                open = OpenTrade(states, nextKey(), fillDirection, fill.At, fill.Qty, fill.Price,
                    fill.FeeMinor, fill.FundingMinor, fill.Key);
                continue;
            }

            if (fillDirection == open.Direction)
            {
                // Scale in: VWAP entry accumulates, new FIFO lot appended.
                open.Lots.Add(new Lot(fill.Qty, fill.Price));
                open.OpenedQty += fill.Qty;
                open.EntryNotional += fill.Qty * fill.Price;
                open.Fees += fill.FeeMinor;
                open.Funding += fill.FundingMinor;
                open.FillKeys.Add(fill.Key);
                continue;
            }

            // Opposite direction: scale out FIFO, possibly closing and/or flipping.
            var remaining = open.RemainingQty;
            var closeQty = Math.Min(fill.Qty, remaining);
            var dirSign = open.Direction == TradeDirection.Long ? 1m : -1m;

            // Realise against FIFO lots in decimal; convert once per realisation event.
            var realised = 0m;
            var toMatch = closeQty;
            while (toMatch > 0m && open.Lots.Count > 0)
            {
                var lot = open.Lots[0];
                var matched = Math.Min(lot.Qty, toMatch);
                realised += (fill.Price - lot.Price) * matched * dirSign;
                if (matched == lot.Qty)
                {
                    open.Lots.RemoveAt(0);
                }
                else
                {
                    open.Lots[0] = lot with { Qty = lot.Qty - matched };
                }

                toMatch -= matched;
            }

            open.RealisedMinor += toMinor(realised);
            open.ExitQty += closeQty;
            open.ExitNotional += closeQty * fill.Price;
            open.FillKeys.Add(fill.Key);

            if (fill.Qty > remaining)
            {
                // Flip: the fill crosses through flat. Close the current trade and open a new
                // one in the opposite direction with the residual quantity. Fees/funding on the
                // flip fill are split pro-rata by quantity fraction; the split conserves totals.
                var closedFraction = remaining / fill.Qty;
                var closedFee = RoundMinor(fill.FeeMinor * closedFraction);
                var closedFunding = RoundMinor(fill.FundingMinor * closedFraction);

                open.Fees += closedFee;
                open.Funding += closedFunding;
                CloseTrade(open, fill.At);

                var residualQty = fill.Qty - remaining;
                open = OpenTrade(states, nextKey(), fillDirection, fill.At, residualQty, fill.Price,
                    fill.FeeMinor - closedFee, fill.FundingMinor - closedFunding, fill.Key);
            }
            else
            {
                open.Fees += fill.FeeMinor;
                open.Funding += fill.FundingMinor;
                if (closeQty == remaining)
                {
                    CloseTrade(open, fill.At);
                    open = null;
                }
            }
        }
    }

    private static TradeState OpenTrade(
        List<TradeState> states,
        string key,
        TradeDirection direction,
        DateTime at,
        decimal qty,
        decimal price,
        long feeMinor,
        long fundingMinor,
        string fillKey)
    {
        var state = new TradeState
        {
            Key = key,
            Direction = direction,
            OpenedAt = at,
            CreatedIndex = states.Count,
            OpenedQty = qty,
            EntryNotional = qty * price,
            Fees = feeMinor,
            Funding = fundingMinor,
        };
        state.Lots.Add(new Lot(qty, price));
        state.FillKeys.Add(fillKey);
        states.Add(state);
        return state;
    }

    private static void CloseTrade(TradeState state, DateTime at)
    {
        state.IsClosed = true;
        state.ClosedAt = at;
        state.Lots.Clear();
    }

    private static long RoundMinor(decimal value) =>
        (long)decimal.Round(value, 0, MidpointRounding.AwayFromZero);

    private readonly record struct Lot(decimal Qty, decimal Price);

    private sealed class TradeState
    {
        public string Key { get; set; } = string.Empty;

        public TradeDirection Direction { get; set; }

        public DateTime OpenedAt { get; set; }

        public DateTime? ClosedAt { get; set; }

        public int CreatedIndex { get; set; }

        public List<Lot> Lots { get; } = [];

        public decimal OpenedQty { get; set; }

        public decimal EntryNotional { get; set; }

        public decimal ExitQty { get; set; }

        public decimal ExitNotional { get; set; }

        public long RealisedMinor { get; set; }

        public long Fees { get; set; }

        public long Funding { get; set; }

        public List<string> FillKeys { get; } = [];

        public bool IsClosed { get; set; }

        public decimal RemainingQty => Lots.Sum(l => l.Qty);

        public TradeAggregate ToAggregate() => new(
            Key,
            Direction,
            OpenedAt,
            ClosedAt,
            IsClosed ? TradeStatus.Closed : TradeStatus.Open,
            OpenedQty,
            OpenedQty > 0m ? EntryNotional / OpenedQty : 0m,
            ExitQty > 0m ? ExitNotional / ExitQty : null,
            RealisedMinor,
            Fees,
            Funding,
            FillKeys.ToArray(),
            IsClosed ? 0m : RemainingQty,
            Lots.Select(l => new OpenLot(l.Qty, l.Price)).ToArray());
    }
}
