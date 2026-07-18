using Edgewise.Domain.Engines.Indicators;
using Shouldly;

namespace Edgewise.Domain.Tests.Indicators;

public class IndicatorLibraryTests
{
    private static List<Bar> FromCloses(params decimal[] closes)
        => closes
            .Select((c, i) => new Bar(new DateTime(2024, 1, 1).AddDays(i), c, c, c, c, 1m))
            .ToList();

    // ---------------------------------------------------------------- SMA / EMA

    [Fact]
    public void Sma_trivial_series()
    {
        // Hand calc: closes 1..5, period 3 -> [null, null, (1+2+3)/3=2, 3, 4]
        var sma = IndicatorLibrary.Sma(FromCloses(1, 2, 3, 4, 5), 3);
        sma.ShouldBe(new decimal?[] { null, null, 2m, 3m, 4m });
    }

    [Fact]
    public void Ema_trivial_series_sma_seeded()
    {
        // Hand calc: period 3, k = 2/(3+1) = 0.5. Seed at index 2 = SMA(1,2,3) = 2.
        // idx3 = 2 + 0.5*(4-2) = 3; idx4 = 3 + 0.5*(5-3) = 4.
        var ema = IndicatorLibrary.Ema(FromCloses(1, 2, 3, 4, 5), 3);
        ema.ShouldBe(new decimal?[] { null, null, 2m, 3m, 4m });
    }

    [Fact]
    public void Ema_reacts_faster_than_sma_after_step()
    {
        var bars = FromCloses(10, 10, 10, 10, 10, 20);
        var ema = IndicatorLibrary.Ema(bars, 5);
        var sma = IndicatorLibrary.Sma(bars, 5);
        // Hand calc: k = 2/6 = 1/3; EMA idx5 = 10 + (1/3)*(20-10) = 13.333...; SMA idx5 = 12.
        ema[5]!.Value.ShouldBe(10m + (10m / 3m), 0.0001m);
        sma[5]!.Value.ShouldBe(12m);
    }

    // ---------------------------------------------------------------- RSI

    [Fact]
    public void Rsi_all_gains_is_100_then_wilder_smooths_a_loss()
    {
        // 15 closes rising by 1 (14 changes, all gains): avgGain=1, avgLoss=0 -> RSI[14]=100.
        // Then one drop of 0.5: Wilder smoothing:
        //   avgGain = (1*13 + 0)/14 = 13/14; avgLoss = (0*13 + 0.5)/14 = 0.5/14
        //   RS = 13/0.5 = 26 -> RSI = 100 - 100/27 = 96.296296...
        var closes = Enumerable.Range(10, 15).Select(x => (decimal)x).ToList();
        closes.Add(23.5m);
        var rsi = IndicatorLibrary.Rsi(FromCloses(closes.ToArray()), 14);

        for (var i = 0; i < 14; i++)
        {
            rsi[i].ShouldBeNull();
        }

        rsi[14].ShouldBe(100m);
        rsi[15]!.Value.ShouldBe(100m - (100m / 27m), 0.0001m);
    }

    [Fact]
    public void Rsi_matches_classic_wilder_reference_series()
    {
        // Reference: the classic RSI(14) worked example popularized by StockCharts
        // ("Relative Strength Index (RSI)" ChartSchool article), closes rounded to 2dp.
        // Hand verification for index 14 (first RSI):
        //   gains: .06+.72+.50+.27+.32+.42+.24+.14+.67 = 3.34 -> avgGain = 3.34/14
        //   losses: .25+.54+.19+.42 = 1.40               -> avgLoss = 1.40/14
        //   RSI = 100 - 100/(1 + 3.34/1.40) = 100 - 140/4.74 = 70.4641...
        // Index 15 (close 46.00, change -0.28), Wilder smoothing:
        //   avgGain = (3.34/14*13)/14 = 0.2215306; avgLoss = (0.10*13 + 0.28)/14 = 0.1128571
        //   RSI = 100 - 100/(1 + 1.9628960) = 66.2497...
        var bars = FromCloses(
            44.34m, 44.09m, 44.15m, 43.61m, 44.33m, 44.83m, 45.10m, 45.42m,
            45.84m, 46.08m, 45.89m, 46.03m, 45.61m, 46.28m, 46.28m, 46.00m);
        var rsi = IndicatorLibrary.Rsi(bars, 14);

        rsi[13].ShouldBeNull();
        rsi[14]!.Value.ShouldBe(70.4641m, 0.001m);
        rsi[15]!.Value.ShouldBe(66.2497m, 0.001m);
    }

    [Fact]
    public void Rsi_flat_series_is_50()
    {
        var rsi = IndicatorLibrary.Rsi(FromCloses(Enumerable.Repeat(5m, 20).ToArray()), 14);
        rsi[14].ShouldBe(50m);
    }

    // ---------------------------------------------------------------- MACD

    [Fact]
    public void Macd_constant_series_is_zero_with_correct_warmup()
    {
        // On a constant series every EMA equals the constant, so macd = signal = hist = 0.
        // Warmup: macd non-null from index slow-1 = 25; signal from 25 + (9-1) = 33.
        var bars = FromCloses(Enumerable.Repeat(100m, 40).ToArray());
        var (macd, signal, hist) = IndicatorLibrary.Macd(bars);

        macd[24].ShouldBeNull();
        macd[25].ShouldBe(0m);
        signal[32].ShouldBeNull();
        signal[33].ShouldBe(0m);
        hist[32].ShouldBeNull();
        hist[33].ShouldBe(0m);
        macd.Length.ShouldBe(40);
    }

    [Fact]
    public void Macd_line_equals_fast_ema_minus_slow_ema()
    {
        // Spot check on a rising series: the macd line must equal EMA(12) - EMA(26)
        // computed by the (independently hand-verified) EMA implementation.
        var bars = FromCloses(Enumerable.Range(1, 40).Select(x => (decimal)x).ToArray());
        var (macd, _, _) = IndicatorLibrary.Macd(bars);
        var fast = IndicatorLibrary.Ema(bars, 12);
        var slow = IndicatorLibrary.Ema(bars, 26);

        for (var i = 25; i < 40; i++)
        {
            macd[i]!.Value.ShouldBe(fast[i]!.Value - slow[i]!.Value);
        }

        // Rising series: fast EMA sits above slow EMA, so macd > 0.
        macd[39]!.Value.ShouldBeGreaterThan(0m);
    }

    // ---------------------------------------------------------------- ATR

    [Fact]
    public void Atr_hand_computed_small_series()
    {
        // Hand calc, period 3:
        //   b0 (H10,L8,C9):   TR = 10-8 = 2            (no previous close)
        //   b1 (H11,L9,C10):  TR = max(2, |11-9|, |9-9|)  = 2
        //   b2 (H12,L9,C11):  TR = max(3, |12-10|, |9-10|) = 3
        //   ATR[2] = (2+2+3)/3 = 7/3
        //   b3 (H12,L10,C11): TR = max(2, |12-11|, |10-11|) = 2
        //   ATR[3] = (7/3*2 + 2)/3 = 20/9 = 2.2222...
        var t0 = new DateTime(2024, 1, 1);
        var bars = new List<Bar>
        {
            new(t0, 9m, 10m, 8m, 9m, 1m),
            new(t0.AddDays(1), 10m, 11m, 9m, 10m, 1m),
            new(t0.AddDays(2), 11m, 12m, 9m, 11m, 1m),
            new(t0.AddDays(3), 11m, 12m, 10m, 11m, 1m),
        };
        var atr = IndicatorLibrary.Atr(bars, 3);

        atr[0].ShouldBeNull();
        atr[1].ShouldBeNull();
        atr[2]!.Value.ShouldBe(7m / 3m, 0.0001m);
        atr[3]!.Value.ShouldBe(20m / 9m, 0.0001m);
    }

    // ---------------------------------------------------------------- Bollinger

    [Fact]
    public void Bollinger_matches_manual_population_stddev_calc()
    {
        // Hand calc: closes 1..20, period 20, mult 2.
        //   mid = (1+...+20)/20 = 10.5
        //   population variance = sum((i-10.5)^2)/20 = 665/20 = 33.25 -> sd = sqrt(33.25) = 5.7662813...
        //   upper = 10.5 + 2*sd = 22.0325626; lower = 10.5 - 2*sd = -1.0325626
        var bars = FromCloses(Enumerable.Range(1, 20).Select(x => (decimal)x).ToArray());
        var (mid, upper, lower) = IndicatorLibrary.Bollinger(bars, 20, 2m);

        mid[18].ShouldBeNull();
        mid[19]!.Value.ShouldBe(10.5m);
        upper[19]!.Value.ShouldBe(22.0325626m, 0.0001m);
        lower[19]!.Value.ShouldBe(-1.0325626m, 0.0001m);
    }

    // ---------------------------------------------------------------- VWAP

    [Fact]
    public void Vwap_is_session_anchored_and_resets_each_calendar_day()
    {
        // Day 1 bar A: TP = (10+8+9)/3 = 9, V=2 -> VWAP = 18/2 = 9
        // Day 1 bar B: TP = (12+10+11)/3 = 11, V=1 -> VWAP = (18+11)/3 = 9.6666...
        // Day 2 bar C: TP = (20+18+19)/3 = 19, V=1 -> resets -> VWAP = 19
        var d1 = new DateTime(2024, 3, 5, 9, 30, 0);
        var d2 = new DateTime(2024, 3, 6, 9, 30, 0);
        var bars = new List<Bar>
        {
            new(d1, 9m, 10m, 8m, 9m, 2m),
            new(d1.AddHours(1), 11m, 12m, 10m, 11m, 1m),
            new(d2, 19m, 20m, 18m, 19m, 1m),
        };
        var vwap = IndicatorLibrary.Vwap(bars);

        vwap[0]!.Value.ShouldBe(9m);
        vwap[1]!.Value.ShouldBe(29m / 3m, 0.0001m);
        vwap[2]!.Value.ShouldBe(19m);
    }

    [Fact]
    public void Vwap_zero_volume_session_prefix_is_null()
    {
        var d = new DateTime(2024, 3, 5);
        var bars = new List<Bar>
        {
            new(d, 10m, 10m, 10m, 10m, 0m),
            new(d.AddHours(1), 10m, 12m, 8m, 10m, 3m),
        };
        var vwap = IndicatorLibrary.Vwap(bars);
        vwap[0].ShouldBeNull();
        vwap[1]!.Value.ShouldBe(10m);
    }

    // ---------------------------------------------------------------- alignment & validation

    [Fact]
    public void All_outputs_align_to_input_length()
    {
        var bars = FromCloses(Enumerable.Range(1, 60).Select(x => (decimal)x).ToArray());
        IndicatorLibrary.Sma(bars, 20).Length.ShouldBe(60);
        IndicatorLibrary.Ema(bars, 20).Length.ShouldBe(60);
        IndicatorLibrary.Rsi(bars).Length.ShouldBe(60);
        IndicatorLibrary.Atr(bars).Length.ShouldBe(60);
        IndicatorLibrary.Vwap(bars).Length.ShouldBe(60);
        var (m, s, h) = IndicatorLibrary.Macd(bars);
        m.Length.ShouldBe(60);
        s.Length.ShouldBe(60);
        h.Length.ShouldBe(60);
        var (mid, up, lo) = IndicatorLibrary.Bollinger(bars);
        mid.Length.ShouldBe(60);
        up.Length.ShouldBe(60);
        lo.Length.ShouldBe(60);
    }

    [Fact]
    public void Short_input_yields_all_nulls_not_throw()
    {
        var bars = FromCloses(1, 2, 3);
        IndicatorLibrary.Sma(bars, 10).ShouldAllBe(v => v == null);
        IndicatorLibrary.Rsi(bars, 14).ShouldAllBe(v => v == null);
        IndicatorLibrary.Atr(bars, 14).ShouldAllBe(v => v == null);
    }

    [Fact]
    public void Invalid_period_throws()
    {
        var bars = FromCloses(1, 2, 3);
        Should.Throw<ArgumentOutOfRangeException>(() => IndicatorLibrary.Sma(bars, 0));
        Should.Throw<ArgumentOutOfRangeException>(() => IndicatorLibrary.Rsi(bars, -1));
    }
}
