using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Edgewise.Api.Endpoints.Market;
using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Data;
using Edgewise.Infrastructure.Jobs;
using Edgewise.Infrastructure.MarketData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Edgewise.Api.IntegrationTests.MarketData;

[Collection(MarketDataCollection.Name)]
public sealed class MarketDataTests
{
    private static readonly JsonSerializerOptions Json = CreateJsonOptions();

    private readonly MarketDataFactory _factory;

    public MarketDataTests(MarketDataFactory factory)
    {
        _factory = factory;
        _factory.ResetFakes();
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private sealed record QuotesResponse(List<QuoteDto> Quotes);

    private sealed record NewsListResponse(List<NewsItemDto> Items);

    // ---------------------------------------------------------------- helpers

    private async Task<HttpClient> AuthedClientAsync()
    {
        var client = _factory.CreateClient();
        var auth = await client.RegisterAsync(ApiClientHelpers.UniqueEmail());
        client.UseBearer(auth.AccessToken!);
        return client;
    }

    private static async Task<InstrumentDto> CreateInstrumentAsync(
        HttpClient client, AssetClass assetClass, Dictionary<string, string>? providerSymbols = null)
    {
        var symbol = $"T{Guid.NewGuid():N}"[..7].ToUpperInvariant();
        var response = await client.PostAsJsonAsync(
            "/api/market/instruments",
            new CreateInstrumentRequest(symbol, $"Test {symbol}", assetClass, null, "USD", providerSymbols),
            Json);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var dto = await response.Content.ReadFromJsonAsync<InstrumentDto>(Json);
        dto.ShouldNotBeNull();
        return dto;
    }

    /// <summary>N closed daily bars ending yesterday (UTC), ascending, deterministic prices.</summary>
    private static List<ProviderBar> ClosedDailyBars(int count)
    {
        var bars = new List<ProviderBar>(count);
        var today = DateTime.UtcNow.Date;
        for (var i = count; i >= 1; i--)
        {
            var ts = today.AddDays(-i);
            var close = 100m + ((count - i) * 0.5m);
            bars.Add(new ProviderBar(ts, close - 1m, close + 2m, close - 2m, close, 1000m + i));
        }

        return bars;
    }

    private async Task<T> WithDbAsync<T>(Func<EdgewiseDbContext, Task<T>> action)
    {
        using var scope = _factory.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<EdgewiseDbContext>());
    }

    // ------------------------------------------------------------------ tests

    [Fact]
    public async Task Bars_are_served_from_db_after_first_fetch()
    {
        var client = await AuthedClientAsync();
        var instrument = await CreateInstrumentAsync(client, AssetClass.Crypto);
        _factory.BinanceBars.Handler = (_, _, _, _) => ClosedDailyBars(10);

        var url = $"/api/market/instruments/{instrument.Id}/bars?timeframe=d1";
        var first = await client.GetFromJsonAsync<BarsResponse>(url, Json);
        first.ShouldNotBeNull();
        first.Bars.Count.ShouldBe(10);
        first.Stale.ShouldBeNull(); // fresh through the last closed daily bar
        first.Bars.Zip(first.Bars.Skip(1)).ShouldAllBe(pair => pair.First.Ts < pair.Second.Ts);
        _factory.BinanceBars.Calls.ShouldBe(1);

        var second = await client.GetFromJsonAsync<BarsResponse>(url, Json);
        second.ShouldNotBeNull();
        second.Bars.Count.ShouldBe(10);
        _factory.BinanceBars.Calls.ShouldBe(1); // tail already stored: no refetch
    }

    [Fact]
    public async Task Quote_falls_back_to_cache_with_stale_flag_when_providers_fail()
    {
        var client = await AuthedClientAsync();
        var instrument = await CreateInstrumentAsync(client, AssetClass.Crypto);
        _factory.BinanceQuotes.Handler = _ => 123.45m;

        var url = $"/api/market/quotes?instrumentIds={instrument.Id}";
        var fresh = await client.GetFromJsonAsync<QuotesResponse>(url, Json);
        fresh.ShouldNotBeNull();
        var quote = fresh.Quotes.ShouldHaveSingleItem();
        quote.Price.ShouldBe(123.45m);
        quote.Stale.ShouldBeFalse();
        quote.Provider.ShouldBe("binance");

        // Age the cache beyond the 15-minute freshness window, then fail every provider.
        var past = DateTime.UtcNow.AddMinutes(-31);
        await WithDbAsync(db => db.Database.ExecuteSqlAsync(
            $"""UPDATE "QuoteCaches" SET "AsOf" = {past} WHERE "InstrumentId" = {instrument.Id}"""));
        _factory.BinanceQuotes.Handler = null;

        var stale = await client.GetFromJsonAsync<QuotesResponse>(url, Json);
        stale.ShouldNotBeNull();
        var staleQuote = stale.Quotes.ShouldHaveSingleItem();
        staleQuote.Price.ShouldBe(123.45m); // last cached price still served
        staleQuote.Stale.ShouldBeTrue();
        staleQuote.AgeMinutes.ShouldNotBeNull();
        staleQuote.AgeMinutes.Value.ShouldBeGreaterThanOrEqualTo(30);
    }

    [Fact]
    public async Task Bar_fallback_chain_tries_yahoo_then_stooq()
    {
        var client = await AuthedClientAsync();
        var instrument = await CreateInstrumentAsync(client, AssetClass.Equity);
        _factory.YahooBars.Handler = null; // hard failure
        _factory.StooqBars.Handler = (_, _, _, _) => ClosedDailyBars(5);

        var result = await client.GetFromJsonAsync<BarsResponse>(
            $"/api/market/instruments/{instrument.Id}/bars?timeframe=d1", Json);
        result.ShouldNotBeNull();
        result.Bars.Count.ShouldBe(5);
        _factory.YahooBars.Calls.ShouldBe(1); // primary was tried first...
        _factory.StooqBars.Calls.ShouldBe(1); // ...then the fallback served

        var providers = await WithDbAsync(db => db.PriceBars
            .Where(b => b.InstrumentId == instrument.Id)
            .Select(b => b.Provider)
            .Distinct()
            .ToListAsync());
        providers.ShouldBe(["stooq"]);
    }

    [Fact]
    public async Task Indicators_endpoint_returns_arrays_aligned_with_bars()
    {
        var client = await AuthedClientAsync();
        var instrument = await CreateInstrumentAsync(client, AssetClass.Crypto);
        _factory.BinanceBars.Handler = (_, _, _, _) => ClosedDailyBars(60);

        var response = await client.GetAsync(
            $"/api/market/instruments/{instrument.Id}/indicators?timeframe=d1&set=sma20,rsi14,macd,bb20,vwap");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var ts = doc.RootElement.GetProperty("ts");
        ts.GetArrayLength().ShouldBe(60);
        var series = doc.RootElement.GetProperty("series");

        var sma = series.GetProperty("sma20");
        sma.GetArrayLength().ShouldBe(60);
        for (var i = 0; i < 19; i++)
        {
            sma[i].ValueKind.ShouldBe(JsonValueKind.Null); // warmup slots
        }

        sma[19].ValueKind.ShouldBe(JsonValueKind.Number);

        series.GetProperty("rsi14").GetArrayLength().ShouldBe(60);
        series.GetProperty("vwap").GetArrayLength().ShouldBe(60);
        var macd = series.GetProperty("macd");
        macd.GetProperty("macd").GetArrayLength().ShouldBe(60);
        macd.GetProperty("signal").GetArrayLength().ShouldBe(60);
        macd.GetProperty("hist").GetArrayLength().ShouldBe(60);
        var bb = series.GetProperty("bb20");
        bb.GetProperty("mid").GetArrayLength().ShouldBe(60);
        bb.GetProperty("upper").GetArrayLength().ShouldBe(60);
        bb.GetProperty("lower").GetArrayLength().ShouldBe(60);
    }

    [Fact]
    public async Task News_poll_deduplicates_by_url_hash()
    {
        var client = await AuthedClientAsync();
        var marker = Guid.NewGuid().ToString("N");
        var urlA = $"https://news.test/{marker}/a";
        var urlB = $"https://news.test/{marker}/b";
        var now = DateTime.UtcNow;
        _factory.NewsFeed.Items.AddRange(
        [
            new FetchedNewsItem($"Story A {marker}", urlA, "TestFeed", now, "First copy"),
            new FetchedNewsItem($"Story A duplicate {marker}", urlA, "TestFeed", now, "Same URL again"),
            new FetchedNewsItem($"Story B {marker}", urlB, "TestFeed", now.AddMinutes(-1), null),
        ]);

        // Two full polls: the same URLs must never produce extra rows.
        for (var run = 0; run < 2; run++)
        {
            using var scope = _factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<NewsPollJob>().RunAsync(CancellationToken.None);
        }

        var stored = await WithDbAsync(db => db.NewsItems
            .Where(n => n.Url == urlA || n.Url == urlB)
            .ToListAsync());
        stored.Count.ShouldBe(2);
        stored.Select(n => n.Url).ShouldBe([urlA, urlB], ignoreOrder: true);

        var feed = await client.GetFromJsonAsync<NewsListResponse>("/api/market/news?limit=100", Json);
        feed.ShouldNotBeNull();
        feed.Items.Count(i => i.Url == urlA).ShouldBe(1);
        feed.Items.Count(i => i.Url == urlB).ShouldBe(1);
    }

    [Fact]
    public async Task Instrument_search_matches_symbol_and_name()
    {
        var client = await AuthedClientAsync();
        var created = await CreateInstrumentAsync(client, AssetClass.Equity);

        var bySymbol = await client.GetFromJsonAsync<List<InstrumentDto>>(
            $"/api/market/instruments/search?q={created.Symbol[..5].ToLowerInvariant()}", Json);
        bySymbol.ShouldNotBeNull();
        bySymbol.ShouldContain(i => i.Id == created.Id);

        var byName = await client.GetFromJsonAsync<List<InstrumentDto>>(
            $"/api/market/instruments/search?q=Test {created.Symbol}", Json);
        byName.ShouldNotBeNull();
        byName.ShouldContain(i => i.Id == created.Id);

        var none = await client.GetFromJsonAsync<List<InstrumentDto>>(
            "/api/market/instruments/search?q=zzz-does-not-exist-zzz", Json);
        none.ShouldNotBeNull();
        none.ShouldBeEmpty();

        var missing = await client.GetAsync("/api/market/instruments/search");
        missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Fx_rate_is_fetched_once_then_served_from_db()
    {
        var client = await AuthedClientAsync();
        _factory.FrankfurterFx.Handler = (_, _, _) => 18.5m;

        var first = await client.GetFromJsonAsync<FxRateDto>("/api/market/fx?base=USD&quote=ZAR", Json);
        first.ShouldNotBeNull();
        first.Rate.ShouldBe(18.5m);
        first.Provider.ShouldBe("frankfurter");
        _factory.FrankfurterFx.Calls.ShouldBe(1);

        var second = await client.GetFromJsonAsync<FxRateDto>("/api/market/fx?base=USD&quote=ZAR", Json);
        second.ShouldNotBeNull();
        second.Rate.ShouldBe(18.5m);
        _factory.FrankfurterFx.Calls.ShouldBe(1); // stored rate reused

        // The stored direct rate also serves the inverted pair without a fetch.
        var inverted = await client.GetFromJsonAsync<FxRateDto>("/api/market/fx?base=ZAR&quote=USD", Json);
        inverted.ShouldNotBeNull();
        inverted.Rate.ShouldBe(1m / 18.5m);
        _factory.FrankfurterFx.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Calendar_supports_user_events_and_owner_only_delete()
    {
        var client = await AuthedClientAsync();
        var at = DateTime.UtcNow.AddDays(3);
        var createResponse = await client.PostAsJsonAsync(
            "/api/market/calendar",
            new CreateCatalystEventRequest("CPI print watch", at, null, null, null),
            Json);
        createResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        var created = await createResponse.Content.ReadFromJsonAsync<CatalystEventDto>(Json);
        created.ShouldNotBeNull();
        created.Kind.ShouldBe(CatalystKind.User);
        created.Own.ShouldBeTrue();

        using var calendarDoc = JsonDocument.Parse(
            await (await client.GetAsync("/api/market/calendar")).Content.ReadAsStringAsync());
        calendarDoc.RootElement.GetProperty("events").EnumerateArray()
            .Any(e => e.GetProperty("id").GetGuid() == created.Id)
            .ShouldBeTrue();

        // Another user can neither see nor delete it.
        var stranger = await AuthedClientAsync();
        (await stranger.DeleteAsync($"/api/market/calendar/{created.Id}"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await client.DeleteAsync($"/api/market/calendar/{created.Id}"))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }
}
