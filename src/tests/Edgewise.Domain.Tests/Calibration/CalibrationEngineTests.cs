using Edgewise.Domain.Engines.Calibration;
using FsCheck.Xunit;
using Shouldly;

namespace Edgewise.Domain.Tests.Calibration;

public class CalibrationEngineTests
{
    private static readonly DateTime Jan = new(2026, 1, 15);
    private static readonly DateTime Feb = new(2026, 2, 15);

    private static ForecastResolution F(decimal p, bool outcome, decimal? pMarket = null, DateTime? at = null)
        => new(p, pMarket ?? p, outcome, at ?? Jan);

    // ---------------------------------------------------------------- Brier score

    [Theory]
    [InlineData(1.0, true, 0.0)]     // perfect confident call
    [InlineData(0.0, false, 0.0)]    // perfect confident negative call
    [InlineData(0.5, true, 0.25)]    // fence-sitting always scores 0.25
    [InlineData(0.5, false, 0.25)]
    [InlineData(0.0, true, 1.0)]     // maximally wrong
    [InlineData(0.7, false, 0.49)]   // (0.7 - 0)^2
    [InlineData(0.7, true, 0.09)]    // (0.7 - 1)^2
    public void Brier_hand_values(decimal p, bool outcome, decimal expected)
    {
        CalibrationEngine.BrierScore(p, outcome).ShouldBe(expected);
    }

    [Fact]
    public void Brier_rejects_probabilities_outside_unit_interval()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => CalibrationEngine.BrierScore(1.01m, true));
        Should.Throw<ArgumentOutOfRangeException>(() => CalibrationEngine.BrierScore(-0.01m, false));
    }

    [Property]
    public bool Brier_is_always_within_unit_interval(int raw)
    {
        var p = Math.Abs(raw % 101) / 100m; // deterministic map into [0, 1]
        var s1 = CalibrationEngine.BrierScore(p, true);
        var s2 = CalibrationEngine.BrierScore(p, false);
        return s1 >= 0m && s1 <= 1m && s2 >= 0m && s2 <= 1m;
    }

    // ---------------------------------------------------------------- report

    [Fact]
    public void Calibrated_forecaster_has_near_zero_overconfidence_and_flat_reliability()
    {
        // p=0.25 bucket: 20 forecasts, 5 true (freq 0.25). p=0.75 bucket: 20 forecasts, 15 true.
        var forecasts = new List<ForecastResolution>();
        for (var i = 0; i < 20; i++)
        {
            forecasts.Add(F(0.25m, i < 5));
            forecasts.Add(F(0.75m, i < 15));
        }

        var report = CalibrationEngine.BuildReport(forecasts);

        report.N.ShouldBe(40);
        report.OverconfidenceIndex.ShouldBe(0m);
        report.Reliability.Count.ShouldBe(2);

        var low = report.Reliability.Single(b => b.BucketMid == 0.25m);
        low.N.ShouldBe(20);
        low.AvgP.ShouldBe(0.25m);
        low.ActualFreq.ShouldBe(0.25m);

        var high = report.Reliability.Single(b => b.BucketMid == 0.75m);
        high.AvgP.ShouldBe(0.75m);
        high.ActualFreq.ShouldBe(0.75m);
    }

    [Fact]
    public void Overconfident_forecaster_has_positive_index()
    {
        // Claims 90% but hits only 50%: overconfidence = 0.9 - 0.5 = 0.4.
        var forecasts = Enumerable.Range(0, 10).Select(i => F(0.9m, i < 5)).ToList();
        var report = CalibrationEngine.BuildReport(forecasts);
        report.OverconfidenceIndex.ShouldBe(0.4m);
    }

    [Fact]
    public void Longshot_bias_measures_low_probability_bucket()
    {
        // p=0.1 forecasts (below the 0.15 longshot threshold): 10 forecasts, 3 true.
        // longshotBias = actualFreq - avgP = 0.3 - 0.1 = 0.2.
        var forecasts = Enumerable.Range(0, 10).Select(i => F(0.1m, i < 3)).ToList();
        var report = CalibrationEngine.BuildReport(forecasts);
        report.LongshotBias.ShouldBe(0.2m);
    }

    [Fact]
    public void Longshot_bias_is_null_when_no_longshots_exist()
    {
        var report = CalibrationEngine.BuildReport([F(0.6m, true), F(0.7m, false)]);
        report.LongshotBias.ShouldBeNull();
    }

    [Fact]
    public void Edge_vs_market_is_visible_via_mean_briers()
    {
        // User says 0.9 on events that happen; market said 0.6 on the same events.
        // meanBrier = 0.01, meanMarketBrier = 0.16 -> the user beats the market.
        var forecasts = Enumerable.Range(0, 5)
            .Select(_ => F(0.9m, true, pMarket: 0.6m))
            .ToList();
        var report = CalibrationEngine.BuildReport(forecasts);
        report.MeanBrier.ShouldBe(0.01m, 0.000001m);
        report.MeanMarketBrier.ShouldBe(0.16m, 0.000001m);
    }

    [Fact]
    public void Trend_groups_by_resolution_month_chronologically()
    {
        // Jan: p=1 true (0) and p=0.5 anything (0.25) -> mean 0.125.
        // Feb: p=0 true (1) -> mean 1.
        var forecasts = new List<ForecastResolution>
        {
            F(0m, true, at: Feb),
            F(1m, true, at: Jan),
            F(0.5m, false, at: Jan),
        };
        var report = CalibrationEngine.BuildReport(forecasts);

        report.Trend.Count.ShouldBe(2);
        report.Trend[0].Year.ShouldBe(2026);
        report.Trend[0].Month.ShouldBe(1);
        report.Trend[0].N.ShouldBe(2);
        report.Trend[0].MeanBrier.ShouldBe(0.125m);
        report.Trend[1].Month.ShouldBe(2);
        report.Trend[1].MeanBrier.ShouldBe(1m);
    }

    [Fact]
    public void Boundary_probability_one_lands_in_top_bucket()
    {
        var report = CalibrationEngine.BuildReport([F(1m, true)]);
        report.Reliability.Single().BucketMid.ShouldBe(0.95m);
    }

    [Fact]
    public void Empty_input_throws()
    {
        Should.Throw<ArgumentException>(() => CalibrationEngine.BuildReport([]));
    }
}
