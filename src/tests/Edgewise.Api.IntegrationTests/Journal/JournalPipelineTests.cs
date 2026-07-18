using System.Net;
using System.Net.Http.Json;
using Edgewise.Api.Services.Journal;
using Edgewise.Contracts.Common;
using Edgewise.Domain.Entities;
using Shouldly;

namespace Edgewise.Api.IntegrationTests.Journal;

/// <summary>
/// The heart-of-the-product pipeline: template → plan (auto-sized) → promote with
/// fills → close → adherence result; plus the unplanned confess path and
/// cross-user isolation.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class JournalPipelineTests(TestAppFactory factory)
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = JournalTestHelpers.Json;

    private async Task<(HttpClient Client, Guid UserId, PlanLookupsDto Lookups)> SetUpUserAsync(long equityMinor)
    {
        var client = factory.CreateClient();
        var auth = await client.RegisterAsync(ApiClientHelpers.UniqueEmail());
        client.UseBearer(auth.AccessToken!);
        var userId = auth.User!.Id;

        var lookups = await client.GetFromJsonAsync<PlanLookupsDto>("/api/plans/lookups", Json);
        lookups.ShouldNotBeNull();
        var trading = lookups.Buckets.Single(b => b.Kind == BucketKind.Trading);
        await factory.SeedSnapshotAsync(userId, trading.Id, equityMinor);
        return (client, userId, lookups);
    }

    [Fact]
    public async Task Clean_planned_trade_scores_grade_A()
    {
        // Bucket equity R10,000.00; Standard profile risks 1% => R100.00 (10_000 minor).
        var (client, _, lookups) = await SetUpUserAsync(equityMinor: 1_000_000);
        var bucket = lookups.Buckets.Single(b => b.Kind == BucketKind.Trading);
        var instrument = lookups.Instruments.First(i => i.Symbol == "BTC-USD");
        var template = lookups.Templates.First(t => t.Shipped && t.Name == "Trend-Pullback");
        var profile = lookups.RiskProfiles.Single(p => p.Name == "Standard");

        // Size preview: risk 10_000 minor over a 5.00 stop distance => qty 20.
        var preview = await client.GetFromJsonAsync<SizePreviewDto>(
            $"/api/plans/size-preview?bucketId={bucket.Id}&stopPrice=95&entryPrice=100&riskProfileId={profile.Id}",
            Json);
        preview.ShouldNotBeNull();
        preview.RValueMinor.ShouldBe(10_000);
        preview.SuggestedQty.ShouldBe(20m);
        preview.NotionalMinor.ShouldBe(200_000);

        // Create the plan; the server keeps the suggested size (no override flag).
        var createResponse = await client.PostAsJsonAsync("/api/plans", new CreatePlanRequest(
            template.Id, instrument.Id, bucket.Id, profile.Id, TradeDirection.Long,
            "trend-pullback", "Pullback held and trigger bar closed", 95m, null,
            null, null, "Loses the 20EMA on a daily close", false,
            """{"htf-trend":true,"pullback-depth":true,"zone-confluence":true,"trigger-bar":true,"no-catalyst":true}""",
            null, 100m), Json);
        createResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        var plan = (await createResponse.Content.ReadFromJsonAsync<PlanDto>(Json))!;
        plan.Status.ShouldBe(TradePlanStatus.Active);
        plan.SizeQty.ShouldBe(20m);
        plan.SizeOverridden.ShouldBeFalse();

        // Version 1 snapshot exists.
        var detail = await client.GetFromJsonAsync<PlanDto>($"/api/plans/{plan.Id}", Json);
        detail!.Id.ShouldBe(plan.Id);

        // Promote with an entry fill strictly after plan creation.
        var entryAt = DateTime.UtcNow.AddMinutes(5);
        var promoteResponse = await client.PostAsJsonAsync($"/api/plans/{plan.Id}/promote",
            new PromotePlanRequest(null, [new PromoteFillRequest(null, FillSide.Buy, 20m, 100m, 50, entryAt)]), Json);
        promoteResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var trade = (await promoteResponse.Content.ReadFromJsonAsync<TradeDto>(Json))!;
        trade.Status.ShouldBe(TradeStatus.Open);
        trade.PlanId.ShouldBe(plan.Id);
        trade.Qty.ShouldBe(20m);
        trade.AvgEntryPrice.ShouldBe(100m);

        // Close at 110 => +10 per unit * 20 = 200.00 => 20_000 minor => R realised = +2.
        var closeResponse = await client.PostAsJsonAsync($"/api/trades/{trade.Id}/close",
            new CloseTradeRequest(null, entryAt.AddHours(2), 110m, null), Json);
        closeResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var closed = (await closeResponse.Content.ReadFromJsonAsync<TradeDetailDto>(Json))!;

        closed.Trade.Status.ShouldBe(TradeStatus.Closed);
        closed.Trade.RealisedPnlMinor.ShouldBe(20_000);
        closed.Trade.RRealised.ShouldBe(2m);
        closed.Trade.HoldingSeconds.ShouldBe(7200);
        closed.PlanVersions.Count.ShouldBe(1);
        closed.Fills.Count.ShouldBe(1);

        closed.Adherence.ShouldNotBeNull();
        closed.Adherence.Score.ShouldBe(100);
        closed.Adherence.Grade.ShouldBe("A");

        // The plan is now promoted.
        var promoted = await client.GetFromJsonAsync<PlanDto>($"/api/plans/{plan.Id}", Json);
        promoted!.Status.ShouldBe(TradePlanStatus.Promoted);
    }

    [Fact]
    public async Task Unplanned_confessed_trade_is_capped_at_C()
    {
        var (client, _, lookups) = await SetUpUserAsync(equityMinor: 1_000_000);
        var instrument = lookups.Instruments.First(i => i.Symbol == "ETH-USD");

        // Log a naked manual fill: it lands in the inbox as Proposed.
        var fillResponse = await client.PostAsJsonAsync("/api/fills", new CreateFillRequest(
            null, instrument.Id, FillSide.Buy, 2m, 50m, 10, null, DateTime.UtcNow, null), Json);
        fillResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        var fill = (await fillResponse.Content.ReadFromJsonAsync<FillDto>(Json))!;
        fill.MatchStatus.ShouldBe(MatchStatus.Proposed);

        // Confess it: an unplanned open trade is created.
        var confessResponse = await client.PostAsJsonAsync($"/api/fills/{fill.Id}/confess", new { }, Json);
        confessResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var confessed = (await confessResponse.Content.ReadFromJsonAsync<FillDto>(Json))!;
        confessed.MatchStatus.ShouldBe(MatchStatus.Confessed);
        confessed.TradeId.ShouldNotBeNull();

        var closeResponse = await client.PostAsJsonAsync($"/api/trades/{confessed.TradeId}/close",
            new CloseTradeRequest(null, DateTime.UtcNow.AddHours(1), 55m, null), Json);
        closeResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var closed = (await closeResponse.Content.ReadFromJsonAsync<TradeDetailDto>(Json))!;

        // NO_PLAN caps the score at 60 => C; R is null because there was no planned risk.
        closed.Adherence.ShouldNotBeNull();
        closed.Adherence.Score.ShouldBe(60);
        closed.Adherence.Grade.ShouldBe("C");
        closed.Trade.RRealised.ShouldBeNull();
        var deductions = closed.Adherence.DeductionsJson!;
        deductions.ShouldContain("NO_PLAN");
    }

    [Fact]
    public async Task Trade_patch_adds_notes_tags_and_emotion()
    {
        var (client, _, lookups) = await SetUpUserAsync(equityMinor: 500_000);
        var instrument = lookups.Instruments.First();

        var fill = (await (await client.PostAsJsonAsync("/api/fills", new CreateFillRequest(
                null, instrument.Id, FillSide.Buy, 1m, 10m, 0, null, DateTime.UtcNow, null), Json))
            .Content.ReadFromJsonAsync<FillDto>(Json))!;
        var confessed = (await (await client.PostAsJsonAsync($"/api/fills/{fill.Id}/confess", new { }, Json))
            .Content.ReadFromJsonAsync<FillDto>(Json))!;

        var patchResponse = await client.PatchAsJsonAsync($"/api/trades/{confessed.TradeId}",
            new PatchTradeRequest(EmotionTag.Fomo, "Chased the breakout without a plan.", ["breakout", "late-entry"]), Json);
        patchResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var detail = (await patchResponse.Content.ReadFromJsonAsync<TradeDetailDto>(Json))!;

        detail.Trade.EmotionTag.ShouldBe(EmotionTag.Fomo);
        detail.JournalEntries.Count.ShouldBe(1);
        detail.JournalEntries[0].NotesMd.ShouldContain("Chased");
        detail.Tags.ShouldBe(["breakout", "late-entry"], ignoreOrder: true);
    }

    [Fact]
    public async Task Cross_user_isolation_journal_surfaces_are_empty_for_other_users()
    {
        var (clientA, _, lookups) = await SetUpUserAsync(equityMinor: 1_000_000);
        var instrument = lookups.Instruments.First();

        // A has a plan and a proposed fill.
        var bucket = lookups.Buckets.Single(b => b.Kind == BucketKind.Trading);
        (await clientA.PostAsJsonAsync("/api/plans", new CreatePlanRequest(
                null, instrument.Id, bucket.Id, null, TradeDirection.Long, "iso", "t", 9m, null,
                1m, false, null, false, null, null, 10m), Json))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        (await clientA.PostAsJsonAsync("/api/fills", new CreateFillRequest(
                null, instrument.Id, FillSide.Buy, 1m, 10m, 0, null, DateTime.UtcNow, null), Json))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        // B sees none of it.
        var clientB = factory.CreateClient();
        var authB = await clientB.RegisterAsync(ApiClientHelpers.UniqueEmail());
        clientB.UseBearer(authB.AccessToken!);

        (await clientB.GetFromJsonAsync<List<PlanDto>>("/api/plans", Json))!.ShouldBeEmpty();
        (await clientB.GetFromJsonAsync<List<InboxGroupDto>>("/api/fills/inbox", Json))!.ShouldBeEmpty();
        var trades = await clientB.GetFromJsonAsync<PagedResult<TradeListItemDto>>("/api/trades", Json);
        trades!.Items.ShouldBeEmpty();
        trades.TotalCount.ShouldBe(0);
    }
}
