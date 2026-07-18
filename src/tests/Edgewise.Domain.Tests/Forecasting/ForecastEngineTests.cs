using Edgewise.Domain.Engines.Forecasting;
using Shouldly;

namespace Edgewise.Domain.Tests.Forecasting;

public class ForecastEngineTests
{
    private static Assumptions Make(
        decimal growthPct,
        decimal? volPct,
        long currentMinor = 100_000,
        long contributionMinor = 12_000,
        decimal contributionIncreasePct = 0m,
        int horizonYears = 3,
        decimal haircutPct = 0m)
        => new(
            [new AssetAssumption("a", currentMinor, growthPct, volPct)],
            new ContributionPlan(contributionMinor, contributionIncreasePct, new Dictionary<string, decimal> { ["a"] = 1m }),
            ReinvestDividends: true,
            HorizonYears: horizonYears,
            HaircutPct: haircutPct);

    // ---------------------------------------------------------------- deterministic

    [Fact]
    public void Zero_growth_zero_vol_is_flat_plus_exact_contribution_sum()
    {
        // 100,000 start, 12,000/yr contributed as 1,000/month, no growth:
        // year-end totals are exactly 112,000 / 124,000 / 136,000 and all bands coincide.
        var bands = ForecastEngine.Deterministic(Make(0m, 0m));

        bands.Count.ShouldBe(3);
        bands[0].BaseMinor.ShouldBe(112_000m);
        bands[1].BaseMinor.ShouldBe(124_000m);
        bands[2].BaseMinor.ShouldBe(136_000m);
        foreach (var b in bands)
        {
            b.BearMinor.ShouldBe(b.BaseMinor);
            b.BullMinor.ShouldBe(b.BaseMinor);
        }
    }

    [Fact]
    public void Bands_are_monotone_bear_below_base_below_bull()
    {
        var bands = ForecastEngine.Deterministic(Make(5m, 10m, horizonYears: 10));
        foreach (var b in bands)
        {
            b.BearMinor.ShouldBeLessThan(b.BaseMinor);
            b.BaseMinor.ShouldBeLessThan(b.BullMinor);
        }
    }

    [Fact]
    public void Haircut_reduces_base_growth()
    {
        var plain = ForecastEngine.Deterministic(Make(6m, 0m));
        var haircut = ForecastEngine.Deterministic(Make(6m, 0m, haircutPct: 2m));
        haircut[2].BaseMinor.ShouldBeLessThan(plain[2].BaseMinor);
    }

    [Fact]
    public void One_year_5pct_growth_no_contribution_compounds_monthly()
    {
        // 100,000 at 5%/yr compounded monthly at (1.05)^(1/12)-1 ends year 1 at 105,000
        // (monthly compounding of the annual-effective rate reproduces the annual rate).
        var a = Make(5m, 0m, contributionMinor: 0, horizonYears: 1);
        var bands = ForecastEngine.Deterministic(a);
        bands[0].BaseMinor.ShouldBe(105_000m, 0.01m);
    }

    [Fact]
    public void Contribution_split_and_annual_increase_are_applied()
    {
        // Two assets, zero growth. Split 75/25 of 12,000/yr rising 10%/yr:
        // y1 total = 200,000 + 12,000 ; y2 = + 13,200.
        var assumptions = new Assumptions(
            [new AssetAssumption("a", 100_000, 0m, 0m), new AssetAssumption("b", 100_000, 0m, 0m)],
            new ContributionPlan(12_000, 10m, new Dictionary<string, decimal> { ["a"] = 3m, ["b"] = 1m }),
            true, 2, 0m);
        var bands = ForecastEngine.Deterministic(assumptions);
        bands[0].BaseMinor.ShouldBe(212_000m, 0.0001m);
        bands[1].BaseMinor.ShouldBe(225_200m, 0.0001m);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    [InlineData(-3)]
    public void Horizon_outside_1_to_20_throws(int years)
    {
        Should.Throw<ArgumentOutOfRangeException>(
            () => ForecastEngine.Deterministic(Make(5m, 10m, horizonYears: years)));
    }

    // ---------------------------------------------------------------- Monte Carlo

    [Fact]
    public void Same_seed_produces_identical_bands()
    {
        var a = Make(5m, 15m, horizonYears: 5);
        var r1 = ForecastEngine.MonteCarlo(a, paths: 500, seed: 42);
        var r2 = ForecastEngine.MonteCarlo(a, paths: 500, seed: 42);
        r1.ShouldBe(r2);
    }

    [Fact]
    public void Different_seed_produces_different_bands()
    {
        var a = Make(5m, 15m, horizonYears: 5);
        var r1 = ForecastEngine.MonteCarlo(a, paths: 500, seed: 1);
        var r2 = ForecastEngine.MonteCarlo(a, paths: 500, seed: 2);
        r1[4].P50Minor.ShouldNotBe(r2[4].P50Minor);
    }

    [Fact]
    public void Percentiles_are_ordered_p5_to_p95()
    {
        var bands = ForecastEngine.MonteCarlo(Make(5m, 20m, horizonYears: 10), paths: 1000, seed: 7);
        foreach (var b in bands)
        {
            b.P5Minor.ShouldBeLessThanOrEqualTo(b.P25Minor);
            b.P25Minor.ShouldBeLessThanOrEqualTo(b.P50Minor);
            b.P50Minor.ShouldBeLessThanOrEqualTo(b.P75Minor);
            b.P75Minor.ShouldBeLessThanOrEqualTo(b.P95Minor);
        }
    }

    [Fact]
    public void More_vol_widens_the_band()
    {
        var narrow = ForecastEngine.MonteCarlo(Make(5m, 5m, horizonYears: 5), paths: 1000, seed: 11);
        var wide = ForecastEngine.MonteCarlo(Make(5m, 20m, horizonYears: 5), paths: 1000, seed: 11);
        var narrowSpread = narrow[4].P95Minor - narrow[4].P5Minor;
        var wideSpread = wide[4].P95Minor - wide[4].P5Minor;
        wideSpread.ShouldBeGreaterThan(narrowSpread);
    }

    [Fact]
    public void Zero_vol_monte_carlo_collapses_to_a_point()
    {
        // With sigma = 0 every path is identical (GBM becomes exp(g*t) growth).
        var bands = ForecastEngine.MonteCarlo(Make(5m, 0m, horizonYears: 3), paths: 100, seed: 3);
        foreach (var b in bands)
        {
            b.P5Minor.ShouldBe(b.P95Minor, 0.01m);
        }
    }

    [Fact]
    public void Invalid_paths_throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(
            () => ForecastEngine.MonteCarlo(Make(5m, 10m), paths: 0, seed: 1));
    }
}
