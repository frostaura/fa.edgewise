using System.Net;
using System.Net.Http.Json;
using Edgewise.Api.Endpoints;
using Edgewise.Api.Services.Portfolio;
using Edgewise.Contracts.Common;
using Edgewise.Domain.Entities;
using Edgewise.Api.IntegrationTests.Journal;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Edgewise.Api.IntegrationTests.Settings;

[Collection(ApiCollection.Name)]
public sealed class RiskProfilesTests(TestAppFactory factory)
{
    private async Task<(HttpClient Client, Guid UserId)> NewUserClientAsync()
    {
        var client = factory.CreateClient();
        var registered = await client.RegisterAsync(ApiClientHelpers.UniqueEmail());
        client.UseBearer(registered.AccessToken!);
        return (client, registered.User!.Id);
    }

    [Fact]
    public async Task List_returns_seeded_profiles_with_fractions_and_default_ladder()
    {
        var (client, _) = await NewUserClientAsync();

        var profiles = await client.GetFromJsonAsync<List<RiskProfileDto>>("/api/risk-profiles");
        profiles.ShouldNotBeNull();
        profiles.Select(p => p.Name).ShouldBe(["Aggressive", "Conservative", "Standard"]);
        profiles.Count(p => p.IsActive).ShouldBe(1);

        var standard = profiles.Single(p => p.Name == "Standard");
        standard.IsActive.ShouldBeTrue();
        standard.Version.ShouldBe(1);
        // Entity stores percent points (1 = 1%); the wire carries fractions.
        standard.RiskPct.ShouldBe(0.01m);
        standard.HeatCapPct.ShouldBe(0.04m);
        standard.DailyStopPct.ShouldBe(0.03m);
        standard.WeeklyStopPct.ShouldBe(0.06m);
        standard.MaxLeverage.ShouldBe(2m);
        standard.MinRR.ShouldBe(1.5m);
        standard.MaxPositions.ShouldBe(5);
        // The shipped R-multiple ladder array is not a thresholds object → defaults.
        standard.LadderThresholds.ShouldBe(new LadderThresholdsDto(0.05m, 0.10m, 0.15m));
    }

    [Fact]
    public async Task Create_persists_ladder_json_in_the_shape_portfolio_service_reads()
    {
        var (client, userId) = await NewUserClientAsync();

        var response = await client.PostAsJsonAsync("/api/risk-profiles", new CreateRiskProfileRequest(
            "Scalping", 0.005m, 0.02m, 0.01m, 0.015m, 2, 0.03m, 5m, 1.8m, 2,
            new LadderThresholdsDto(0.04m, 0.08m, 0.12m)));
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var created = await response.Content.ReadFromJsonAsync<RiskProfileDto>();
        created!.Version.ShouldBe(1);
        created.IsActive.ShouldBeFalse();
        created.RiskPct.ShouldBe(0.005m);
        created.LadderThresholds.ShouldBe(new LadderThresholdsDto(0.04m, 0.08m, 0.12m));

        await using var db = factory.CreateDbContext(userId);
        var row = await db.RiskProfiles.SingleAsync(p => p.Id == created.Id);
        row.RiskPct.ShouldBe(0.5m); // percent points in the entity

        // The stored JSON must round-trip through the same parser PortfolioService uses.
        using var doc = System.Text.Json.JsonDocument.Parse(row.LadderThresholdsJson!);
        doc.RootElement.GetProperty("riskHalvedPct").GetDecimal().ShouldBe(0.04m);
        doc.RootElement.GetProperty("pausedPct").GetDecimal().ShouldBe(0.08m);
        doc.RootElement.GetProperty("paperPct").GetDecimal().ShouldBe(0.12m);
        var ladder = PortfolioService.BuildLadderState(100_000, 90_000, RiskProfilesEndpoints.ParseLadder(row.LadderThresholdsJson));
        ladder.State.ShouldBe(LadderState.Paused); // 10% drawdown ≥ pausedPct 8%
    }

    [Fact]
    public async Task Create_rejects_duplicate_names_and_bad_values()
    {
        var (client, _) = await NewUserClientAsync();

        var duplicate = await client.PostAsJsonAsync("/api/risk-profiles", new CreateRiskProfileRequest(
            "standard", 0.01m, 0.04m, 0.02m, 0.03m, 3, 0.06m, 2m, 1.5m, 5, null));
        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await duplicate.Content.ReadFromJsonAsync<ErrorResponse>())!.Error.Code.ShouldBe("risk_profile_name_taken");

        // Percent points instead of a fraction → rejected.
        var badPct = await client.PostAsJsonAsync("/api/risk-profiles", new CreateRiskProfileRequest(
            "Bad", 2m, 0.04m, 0.02m, 0.03m, 3, 0.06m, 2m, 1.5m, 5, null));
        badPct.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var badLadder = await client.PostAsJsonAsync("/api/risk-profiles", new CreateRiskProfileRequest(
            "BadLadder", 0.01m, 0.04m, 0.02m, 0.03m, 3, 0.06m, 2m, 1.5m, 5,
            new LadderThresholdsDto(0.15m, 0.10m, 0.05m)));
        badLadder.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Update_inserts_a_new_version_and_carries_the_active_flag()
    {
        var (client, _) = await NewUserClientAsync();
        var profiles = await client.GetFromJsonAsync<List<RiskProfileDto>>("/api/risk-profiles");
        var standard = profiles!.Single(p => p.Name == "Standard");

        var response = await client.PutAsJsonAsync($"/api/risk-profiles/{standard.Id}", new UpdateRiskProfileRequest(
            0.0075m, 0.03m, 0.02m, 0.02m, 2, 0.05m, 2m, 2m, 4, new LadderThresholdsDto(0.05m, 0.10m, 0.15m)));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var updated = await response.Content.ReadFromJsonAsync<RiskProfileDto>();

        updated!.Id.ShouldNotBe(standard.Id); // new row, old version untouched
        updated.Name.ShouldBe("Standard");
        updated.Version.ShouldBe(2);
        updated.IsActive.ShouldBeTrue(); // carried from the active v1
        updated.RiskPct.ShouldBe(0.0075m);

        // The list shows only the latest version, and exactly one profile is active.
        var after = await client.GetFromJsonAsync<List<RiskProfileDto>>("/api/risk-profiles");
        after.ShouldNotBeNull();
        after.Count.ShouldBe(3);
        var latestStandard = after.Single(p => p.Name == "Standard");
        latestStandard.Version.ShouldBe(2);
        latestStandard.Id.ShouldBe(updated.Id);
        after.Count(p => p.IsActive).ShouldBe(1);

        // The old version still exists and is fetchable by id.
        var v1 = await client.GetFromJsonAsync<RiskProfileDto>($"/api/risk-profiles/{standard.Id}");
        v1!.Version.ShouldBe(1);
        v1.IsActive.ShouldBeFalse();
    }

    [Fact]
    public async Task Activate_deactivates_every_other_profile()
    {
        var (client, _) = await NewUserClientAsync();
        var profiles = await client.GetFromJsonAsync<List<RiskProfileDto>>("/api/risk-profiles");
        var conservative = profiles!.Single(p => p.Name == "Conservative");
        conservative.IsActive.ShouldBeFalse();

        var response = await client.PostAsync($"/api/risk-profiles/{conservative.Id}/activate", null);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<RiskProfileDto>())!.IsActive.ShouldBeTrue();

        var after = await client.GetFromJsonAsync<List<RiskProfileDto>>("/api/risk-profiles");
        after.ShouldNotBeNull();
        after.Single(p => p.IsActive).Name.ShouldBe("Conservative");
    }

    [Fact]
    public async Task Delete_guards_active_and_plan_referenced_profiles()
    {
        var (client, userId) = await NewUserClientAsync();
        var profiles = await client.GetFromJsonAsync<List<RiskProfileDto>>("/api/risk-profiles");
        profiles.ShouldNotBeNull();
        var standard = profiles.Single(p => p.Name == "Standard");
        var aggressive = profiles.Single(p => p.Name == "Aggressive");
        var conservative = profiles.Single(p => p.Name == "Conservative");

        // Active profile cannot be deleted.
        var activeDelete = await client.DeleteAsync($"/api/risk-profiles/{standard.Id}");
        activeDelete.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await activeDelete.Content.ReadFromJsonAsync<ErrorResponse>())!.Error.Code.ShouldBe("risk_profile_active");

        // A profile referenced by a trade plan (any version) cannot be deleted.
        await SeedPlanReferencingAsync(userId, aggressive.Id);
        var referencedDelete = await client.DeleteAsync($"/api/risk-profiles/{aggressive.Id}");
        referencedDelete.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await referencedDelete.Content.ReadFromJsonAsync<ErrorResponse>())!.Error.Code.ShouldBe("risk_profile_in_use");

        // Unreferenced, inactive → gone, all versions included.
        await client.PutAsJsonAsync($"/api/risk-profiles/{conservative.Id}", new UpdateRiskProfileRequest(
            0.004m, 0.02m, 0.01m, 0.02m, 2, 0.04m, 1m, 2m, 3, null));
        var latest = (await client.GetFromJsonAsync<List<RiskProfileDto>>("/api/risk-profiles"))!
            .Single(p => p.Name == "Conservative");
        latest.Version.ShouldBe(2);

        var deleted = await client.DeleteAsync($"/api/risk-profiles/{latest.Id}");
        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var after = await client.GetFromJsonAsync<List<RiskProfileDto>>("/api/risk-profiles");
        after!.Select(p => p.Name).ShouldBe(["Aggressive", "Standard"]);
        (await client.GetAsync($"/api/risk-profiles/{conservative.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Risk_profiles_are_isolated_per_user()
    {
        var (clientA, _) = await NewUserClientAsync();
        var (clientB, _) = await NewUserClientAsync();

        var createdA = await (await clientA.PostAsJsonAsync("/api/risk-profiles", new CreateRiskProfileRequest(
            "Only-A", 0.01m, 0.04m, 0.02m, 0.03m, 3, 0.06m, 2m, 1.5m, 5, null)))
            .Content.ReadFromJsonAsync<RiskProfileDto>();

        var listB = await clientB.GetFromJsonAsync<List<RiskProfileDto>>("/api/risk-profiles");
        listB!.ShouldNotContain(p => p.Name == "Only-A");

        (await clientB.GetAsync($"/api/risk-profiles/{createdA!.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await clientB.DeleteAsync($"/api/risk-profiles/{createdA.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await clientB.PostAsync($"/api/risk-profiles/{createdA.Id}/activate", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>Seeds a minimal trade plan referencing the given risk profile directly in the database.</summary>
    private async Task SeedPlanReferencingAsync(Guid userId, Guid riskProfileId)
    {
        await using var db = factory.CreateDbContext(userId);
        var bucketId = await db.Buckets.Select(b => b.Id).FirstAsync();
        var instrument = await db.Instruments.FirstOrDefaultAsync();
        if (instrument is null)
        {
            instrument = new Instrument
            {
                Id = Guid.NewGuid(),
                Symbol = $"TST-{Guid.NewGuid():N}"[..12],
                Name = "Test instrument",
                AssetClass = AssetClass.Equity,
                Currency = "ZAR",
            };
            db.Instruments.Add(instrument);
        }

        db.TradePlans.Add(new TradePlan
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            InstrumentId = instrument.Id,
            BucketId = bucketId,
            RiskProfileId = riskProfileId,
            Direction = TradeDirection.Long,
            StopPrice = 90m,
            SizeQty = 1m,
            Status = TradePlanStatus.Draft,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }
}
