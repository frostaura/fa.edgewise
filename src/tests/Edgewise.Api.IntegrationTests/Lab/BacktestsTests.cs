using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;

namespace Edgewise.Api.IntegrationTests.Lab;

/// <summary>
/// Backtests run INLINE here (TestAppFactory disables Hangfire), so the POST
/// response already carries the final status.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class BacktestsTests(TestAppFactory factory)
{
    private async Task<(HttpClient Client, Guid StrategyId)> SetupAsync()
    {
        var client = factory.CreateClient();
        var auth = await client.RegisterAsync(ApiClientHelpers.UniqueEmail());
        client.UseBearer(auth.AccessToken!);

        var create = await client.PostAsJsonAsync("/api/strategies", new
        {
            name = "Backtest target",
            ruleTree = LabTestData.RuleTree(),
        });
        create.StatusCode.ShouldBe(HttpStatusCode.Created);
        var strategyId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        return (client, strategyId);
    }

    [Fact]
    public async Task Backtest_runs_inline_over_seeded_bars_and_reports_honesty()
    {
        var (client, strategyId) = await SetupAsync();
        var instrumentId = await LabTestData.SeedInstrumentAsync(factory);
        await LabTestData.SeedDailyBarsAsync(factory, instrumentId, count: 300);

        var response = await client.PostAsJsonAsync("/api/backtests", new
        {
            strategyId,
            instrumentId,
            timeframe = "d1",
            from = "2023-12-01T00:00:00Z",
            to = "2025-06-01T00:00:00Z",
            costModel = new { venue = "binance" },
            riskModel = new { riskPctPerTrade = 1.0, equityStartMinor = 1_000_000L },
        });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var dto = await response.Content.ReadFromJsonAsync<JsonElement>();
        dto.GetProperty("status").GetString().ShouldBe("done");

        var id = dto.GetProperty("id").GetGuid();
        var fetched = await client.GetFromJsonAsync<JsonElement>($"/api/backtests/{id}");

        // Cost preset resolved and visible.
        var config = fetched.GetProperty("config");
        config.GetProperty("venue").GetString().ShouldBe("binance");
        config.GetProperty("commissionPctPerSide").GetDecimal().ShouldBe(0.1m);
        config.GetProperty("spreadPct").GetDecimal().ShouldBe(0.02m);
        config.GetProperty("slippagePct").GetDecimal().ShouldBe(0.03m);

        // Result: the synthetic trend + sine wave must fire at least one trade.
        var result = fetched.GetProperty("result");
        result.GetProperty("n").GetInt32().ShouldBeGreaterThanOrEqualTo(1);
        result.GetProperty("equityCurveMinor").GetArrayLength().ShouldBe(300);
        result.TryGetProperty("winRate", out _).ShouldBeTrue();
        result.TryGetProperty("maxDrawdownPct", out _).ShouldBeTrue();

        // Honesty report: all sections present; doubled costs can never beat base costs.
        var honesty = fetched.GetProperty("honesty");
        honesty.GetProperty("sampleVerdict").GetString().ShouldNotBeNullOrEmpty();
        honesty.TryGetProperty("isExploratory", out var exploratory).ShouldBeTrue();
        exploratory.ValueKind.ShouldBeOneOf(JsonValueKind.True, JsonValueKind.False);
        honesty.GetProperty("fullSample").GetProperty("n").GetInt32()
            .ShouldBe(result.GetProperty("n").GetInt32());
        honesty.GetProperty("doubledCosts").GetProperty("expectancyR").GetDecimal()
            .ShouldBeLessThanOrEqualTo(result.GetProperty("expectancyR").GetDecimal());
        honesty.TryGetProperty("wiggles", out var wiggles).ShouldBeTrue();
        wiggles.GetArrayLength().ShouldBeGreaterThan(0);
        honesty.TryGetProperty("maxWiggleSensitivityR", out _).ShouldBeTrue();

        // List by strategy finds it.
        var list = await client.GetFromJsonAsync<JsonElement>($"/api/backtests?strategyId={strategyId}");
        list.EnumerateArray().ShouldContain(b => b.GetProperty("id").GetGuid() == id);
    }

    [Fact]
    public async Task Backtest_fails_cleanly_with_insufficient_bars()
    {
        var (client, strategyId) = await SetupAsync();
        var instrumentId = await LabTestData.SeedInstrumentAsync(factory); // no bars seeded

        var response = await client.PostAsJsonAsync("/api/backtests", new
        {
            strategyId,
            instrumentId,
            timeframe = "d1",
            from = "2024-01-01T00:00:00Z",
            to = "2024-12-31T00:00:00Z",
            costModel = new { venue = "binance" },
        });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var dto = await response.Content.ReadFromJsonAsync<JsonElement>();
        dto.GetProperty("status").GetString().ShouldBe("failed");
        dto.GetProperty("error").GetString().ShouldNotBeNull();
        dto.GetProperty("error").GetString()!.ShouldContain("100");
    }

    [Fact]
    public async Task Zero_cost_model_is_rejected_up_front()
    {
        var (client, strategyId) = await SetupAsync();
        var instrumentId = await LabTestData.SeedInstrumentAsync(factory);

        var response = await client.PostAsJsonAsync("/api/backtests", new
        {
            strategyId,
            instrumentId,
            timeframe = "d1",
            from = "2024-01-01T00:00:00Z",
            to = "2024-12-31T00:00:00Z",
            costModel = new { venue = "custom" }, // all-zero
        });
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetProperty("code").GetString().ShouldBe("zero_cost_model");
    }

    [Fact]
    public async Task Venue_presets_are_exposed()
    {
        var client = factory.CreateClient();
        var auth = await client.RegisterAsync(ApiClientHelpers.UniqueEmail());
        client.UseBearer(auth.AccessToken!);

        var presets = await client.GetFromJsonAsync<JsonElement>("/api/backtests/presets");
        var venues = presets.EnumerateArray().Select(p => p.GetProperty("venue").GetString()).ToList();
        venues.ShouldContain("binance");
        venues.ShouldContain("jse");
        venues.ShouldContain("forex");
    }
}
