using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;

namespace Edgewise.Infrastructure.Ingestion.Connectors;

// ------------------------------------------------------------------ models

/// <summary>One trade row from the Polymarket data API (/trades?user=…).</summary>
public sealed class PolymarketTrade
{
    [JsonPropertyName("proxyWallet")] public string? ProxyWallet { get; set; }
    [JsonPropertyName("side")] public string Side { get; set; } = "BUY";
    [JsonPropertyName("asset")] public string? Asset { get; set; }
    [JsonPropertyName("conditionId")] public string ConditionId { get; set; } = string.Empty;
    [JsonPropertyName("size")] public decimal Size { get; set; }
    [JsonPropertyName("price")] public decimal Price { get; set; }
    [JsonPropertyName("timestamp")] public long Timestamp { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("slug")] public string? Slug { get; set; }
    [JsonPropertyName("outcome")] public string? Outcome { get; set; }
    [JsonPropertyName("outcomeIndex")] public int OutcomeIndex { get; set; }
    [JsonPropertyName("transactionHash")] public string? TransactionHash { get; set; }

    /// <summary>The raw JSON element, stored verbatim in Fill.RawPayloadJson.</summary>
    [JsonIgnore] public string RawJson { get; set; } = "{}";
}

public sealed class PolymarketPosition
{
    [JsonPropertyName("asset")] public string? Asset { get; set; }
    [JsonPropertyName("conditionId")] public string ConditionId { get; set; } = string.Empty;
    [JsonPropertyName("size")] public decimal Size { get; set; }
    [JsonPropertyName("avgPrice")] public decimal AvgPrice { get; set; }
    [JsonPropertyName("curPrice")] public decimal CurPrice { get; set; }
    [JsonPropertyName("redeemable")] public bool Redeemable { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("slug")] public string? Slug { get; set; }
    [JsonPropertyName("outcome")] public string? Outcome { get; set; }
    [JsonPropertyName("outcomeIndex")] public int OutcomeIndex { get; set; }
    [JsonPropertyName("endDate")] public string? EndDate { get; set; }
}

/// <summary>Market metadata from the gamma API (/markets?condition_ids=…).</summary>
public sealed class PolymarketMarket
{
    [JsonPropertyName("conditionId")] public string ConditionId { get; set; } = string.Empty;
    [JsonPropertyName("question")] public string? Question { get; set; }
    [JsonPropertyName("closed")] public bool Closed { get; set; }
    [JsonPropertyName("endDate")] public string? EndDate { get; set; }

    /// <summary>Gamma encodes these as JSON-in-a-string, e.g. "[\"Yes\",\"No\"]".</summary>
    [JsonPropertyName("outcomes")] public JsonElement Outcomes { get; set; }
    [JsonPropertyName("outcomePrices")] public JsonElement OutcomePrices { get; set; }

    public IReadOnlyList<string> OutcomeNames() => DecodeStringArray(Outcomes);

    public IReadOnlyList<decimal> Prices() =>
        DecodeStringArray(OutcomePrices)
            .Select(p => decimal.TryParse(p, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0m)
            .ToList();

    private static IReadOnlyList<string> DecodeStringArray(JsonElement element)
    {
        try
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                return JsonSerializer.Deserialize<List<string>>(element.GetString() ?? "[]") ?? [];
            }

            if (element.ValueKind == JsonValueKind.Array)
            {
                return element.EnumerateArray()
                    .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.GetRawText())
                    .ToList();
            }
        }
        catch (JsonException)
        {
        }

        return [];
    }
}

// ------------------------------------------------------------------ client

/// <summary>
/// Read-only Polymarket client. The data API needs no auth; trades/positions are
/// keyed by the user's proxy (deposit) wallet address, NOT their EOA — the
/// connect endpoint surfaces that gotcha as a warning when nothing is found.
/// </summary>
public sealed class PolymarketClient(IIntegrationHttpClientFactory httpFactory, IConfiguration config)
{
    private readonly string _dataApiBase =
        (config["Integrations:Polymarket:DataApiBaseUrl"] ?? "https://data-api.polymarket.com").TrimEnd('/');
    private readonly string _gammaApiBase =
        (config["Integrations:Polymarket:GammaApiBaseUrl"] ?? "https://gamma-api.polymarket.com").TrimEnd('/');

    public async Task<IReadOnlyList<PolymarketTrade>> GetTradesAsync(
        string wallet, int limit, long offset, CancellationToken ct)
    {
        using var client = httpFactory.CreateClient("polymarket");
        var url = $"{_dataApiBase}/trades?user={Uri.EscapeDataString(wallet)}&limit={limit}&offset={offset}";
        using var response = await client.GetAsync(url, ct);
        await EnsureOkAsync(response, "Polymarket");

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var trades = new List<PolymarketTrade>();
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            var trade = element.Deserialize<PolymarketTrade>();
            if (trade is null || string.IsNullOrEmpty(trade.ConditionId))
            {
                continue;
            }

            trade.RawJson = element.GetRawText();
            trades.Add(trade);
        }

        return trades;
    }

    public async Task<IReadOnlyList<PolymarketPosition>> GetPositionsAsync(string wallet, CancellationToken ct)
    {
        using var client = httpFactory.CreateClient("polymarket");
        var url = $"{_dataApiBase}/positions?user={Uri.EscapeDataString(wallet)}";
        using var response = await client.GetAsync(url, ct);
        await EnsureOkAsync(response, "Polymarket");
        var positions = await response.Content.ReadFromJsonAsync<List<PolymarketPosition>>(cancellationToken: ct);
        return positions ?? [];
    }

    /// <summary>Count of recent on-chain activity rows — used only for the wrong-wallet check.</summary>
    public async Task<int> GetActivityCountAsync(string wallet, CancellationToken ct)
    {
        using var client = httpFactory.CreateClient("polymarket");
        var url = $"{_dataApiBase}/activity?user={Uri.EscapeDataString(wallet)}&limit=10";
        using var response = await client.GetAsync(url, ct);
        await EnsureOkAsync(response, "Polymarket");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : 0;
    }

    public async Task<IReadOnlyList<PolymarketMarket>> GetMarketsByConditionIdsAsync(
        IReadOnlyCollection<string> conditionIds, CancellationToken ct)
    {
        if (conditionIds.Count == 0)
        {
            return [];
        }

        using var client = httpFactory.CreateClient("polymarket-gamma");
        var query = string.Join("&", conditionIds.Select(id => $"condition_ids={Uri.EscapeDataString(id)}"));
        var url = $"{_gammaApiBase}/markets?{query}&limit={Math.Max(conditionIds.Count, 20)}";
        using var response = await client.GetAsync(url, ct);
        await EnsureOkAsync(response, "Polymarket");
        var markets = await response.Content.ReadFromJsonAsync<List<PolymarketMarket>>(cancellationToken: ct);
        return markets ?? [];
    }

    private static async Task EnsureOkAsync(HttpResponseMessage response, string provider)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        // Drain without echoing the body anywhere user-visible.
        _ = await response.Content.ReadAsStringAsync();
        throw new IntegrationException(
            "polymarket_unavailable",
            $"{provider} returned HTTP {(int)response.StatusCode} — the service may be down; we will retry on the next sync.");
    }
}
