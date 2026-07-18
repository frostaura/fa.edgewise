using System.Net;
using System.Net.Http.Json;
using System.Text;
using Edgewise.Api.Services.Journal;
using Edgewise.Domain.Entities;
using Shouldly;

namespace Edgewise.Api.IntegrationTests.Journal;

/// <summary>CSV import preview/commit idempotency and the inbox match flow.</summary>
[Collection(ApiCollection.Name)]
public sealed class CsvImportTests(TestAppFactory factory)
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = JournalTestHelpers.Json;

    private const string GenericCsv =
        """
        timestamp,symbol,side,qty,price,fee
        2026-07-01T09:30:00Z,BTC-USD,buy,0.5,60000,12.5
        2026-07-01T11:00:00Z,BTC-USD,sell,0.5,61000,12.5
        2026-07-02T10:00:00Z,ETH-USD,buy,2,3000,6
        """;

    private static MultipartFormDataContent CsvForm(string csv, params (string Name, string Value)[] fields)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/csv");
        content.Add(file, "file", "fills.csv");
        foreach (var (name, value) in fields)
        {
            content.Add(new StringContent(value), name);
        }

        return content;
    }

    private async Task<(HttpClient Client, Guid AccountId)> SetUpAsync()
    {
        var client = factory.CreateClient();
        var auth = await client.RegisterAsync(ApiClientHelpers.UniqueEmail());
        client.UseBearer(auth.AccessToken!);
        var userId = auth.User!.Id;

        var lookups = await client.GetFromJsonAsync<PlanLookupsDto>("/api/plans/lookups", Json);
        var trading = lookups!.Buckets.Single(b => b.Kind == BucketKind.Trading);
        await factory.SeedSnapshotAsync(userId, trading.Id, 10_000_000);
        var accountId = await factory.SeedAccountAsync(userId, trading.Id);
        return (client, accountId);
    }

    [Fact]
    public async Task Preview_suggests_mapping_and_commit_is_idempotent()
    {
        var (client, accountId) = await SetUpAsync();

        // Preview: columns + suggested generic mapping + sample rows.
        var previewResponse = await client.PostAsync("/api/imports/csv/preview", CsvForm(GenericCsv));
        previewResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var preview = (await previewResponse.Content.ReadFromJsonAsync<ImportPreviewDto>(Json))!;
        preview.Columns.ShouldBe(["timestamp", "symbol", "side", "qty", "price", "fee"]);
        preview.SuggestedMapping["timestamp"].ShouldBe("timestamp");
        preview.SuggestedMapping["qty"].ShouldBe("qty");
        preview.SampleRows.Count.ShouldBe(3);

        var mappingJson = System.Text.Json.JsonSerializer.Serialize(preview.SuggestedMapping, Json);

        // First commit imports all three rows and saves the mapping.
        var commit1 = await client.PostAsync("/api/imports/csv/commit", CsvForm(GenericCsv,
            ("mapping", mappingJson), ("accountId", accountId.ToString()),
            ("venue", "generic"), ("saveMappingAs", "My generic broker")));
        commit1.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result1 = (await commit1.Content.ReadFromJsonAsync<ImportCommitResponse>(Json))!;
        result1.Imported.ShouldBe(3);
        result1.Duplicates.ShouldBe(0);
        result1.Errors.ShouldBeEmpty();

        // Second commit of the same file: everything is a duplicate.
        var commit2 = await client.PostAsync("/api/imports/csv/commit", CsvForm(GenericCsv,
            ("mapping", mappingJson), ("accountId", accountId.ToString()), ("venue", "generic")));
        var result2 = (await commit2.Content.ReadFromJsonAsync<ImportCommitResponse>(Json))!;
        result2.Imported.ShouldBe(0);
        result2.Duplicates.ShouldBe(3);

        // The saved mapping shows up in the next preview.
        var preview2Response = await client.PostAsync("/api/imports/csv/preview", CsvForm(GenericCsv));
        var preview2 = (await preview2Response.Content.ReadFromJsonAsync<ImportPreviewDto>(Json))!;
        preview2.SavedMappings.ShouldContain(m => m.Name == "My generic broker" && m.Venue == Venue.Generic);
    }

    [Fact]
    public async Task Imported_fills_land_in_inbox_and_match_into_trades()
    {
        var (client, accountId) = await SetUpAsync();

        var previewResponse = await client.PostAsync("/api/imports/csv/preview", CsvForm(GenericCsv));
        var preview = (await previewResponse.Content.ReadFromJsonAsync<ImportPreviewDto>(Json))!;
        var mappingJson = System.Text.Json.JsonSerializer.Serialize(preview.SuggestedMapping, Json);
        await client.PostAsync("/api/imports/csv/commit", CsvForm(GenericCsv,
            ("mapping", mappingJson), ("accountId", accountId.ToString()), ("venue", "generic")));

        // Inbox groups by instrument+account with a FIFO trade preview.
        var inbox = (await client.GetFromJsonAsync<List<InboxGroupDto>>("/api/fills/inbox", Json))!;
        inbox.Count.ShouldBe(2); // BTC-USD and ETH-USD groups
        var btcGroup = inbox.Single(g => g.Instrument!.Symbol == "BTC-USD");
        btcGroup.Fills.Count.ShouldBe(2);
        btcGroup.TradePreview.Count.ShouldBe(1); // buy + sell collapse into one round trip
        btcGroup.TradePreview[0].RealisedPnlMinor.ShouldBe(50_000); // 0.5 x 1000 => R500.00

        // Match the ETH buy into a standalone trade (3-tap flow, option 3).
        var ethFill = inbox.Single(g => g.Instrument!.Symbol == "ETH-USD").Fills.Single();
        var matchResponse = await client.PostAsJsonAsync($"/api/fills/{ethFill.Id}/match",
            new MatchFillRequest(null, null, CreateTrade: true), Json);
        matchResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var matched = (await matchResponse.Content.ReadFromJsonAsync<FillDto>(Json))!;
        matched.MatchStatus.ShouldBe(MatchStatus.Matched);
        matched.TradeId.ShouldNotBeNull();

        // The inbox count shrank accordingly.
        var countBody = await client.GetFromJsonAsync<Dictionary<string, int>>("/api/fills/inbox/count", Json);
        countBody!["count"].ShouldBe(2);

        // Rebuild the BTC trades from matched fills after matching both BTC fills to a trade.
        var btcFills = btcGroup.Fills.OrderBy(f => f.At).ToList();
        var first = (await (await client.PostAsJsonAsync($"/api/fills/{btcFills[0].Id}/match",
                new MatchFillRequest(null, null, CreateTrade: true), Json))
            .Content.ReadFromJsonAsync<FillDto>(Json))!;
        await client.PostAsJsonAsync($"/api/fills/{btcFills[1].Id}/match",
            new MatchFillRequest(null, first.TradeId, CreateTrade: false), Json);

        var instrumentId = btcGroup.InstrumentId;
        var rebuildResponse = await client.PostAsJsonAsync("/api/trades/rebuild",
            new RebuildTradesRequest(accountId, instrumentId), Json);
        rebuildResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var rebuild = (await rebuildResponse.Content.ReadFromJsonAsync<RebuildTradesResponse>(Json))!;
        rebuild.Created.ShouldBe(1);
        rebuild.Removed.ShouldBe(1);

        // The rebuilt BTC trade is closed with the FIFO PnL.
        var trades = await client.GetFromJsonAsync<Contracts.Common.PagedResult<TradeListItemDto>>(
            $"/api/trades?instrumentId={instrumentId}", Json);
        var rebuilt = trades!.Items.ShouldHaveSingleItem();
        rebuilt.Status.ShouldBe(TradeStatus.Closed);
        rebuilt.RealisedPnlMinor.ShouldBe(50_000);
    }
}
