using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;

namespace Edgewise.Infrastructure.Ingestion.Connectors;

// ------------------------------------------------------------------ models

public sealed class BinanceApiRestrictions
{
    [JsonPropertyName("ipRestrict")] public bool IpRestrict { get; set; }
    [JsonPropertyName("enableReading")] public bool EnableReading { get; set; }
    [JsonPropertyName("enableSpotAndMarginTrading")] public bool EnableSpotAndMarginTrading { get; set; }
    [JsonPropertyName("enableWithdrawals")] public bool EnableWithdrawals { get; set; }
    [JsonPropertyName("enableMargin")] public bool EnableMargin { get; set; }
    [JsonPropertyName("enableFutures")] public bool EnableFutures { get; set; }
    [JsonPropertyName("enableInternalTransfer")] public bool EnableInternalTransfer { get; set; }
    [JsonPropertyName("permitsUniversalTransfer")] public bool PermitsUniversalTransfer { get; set; }
}

public sealed class BinanceMyTrade
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("symbol")] public string Symbol { get; set; } = string.Empty;
    [JsonPropertyName("orderId")] public long OrderId { get; set; }
    [JsonPropertyName("price")] public string Price { get; set; } = "0";
    [JsonPropertyName("qty")] public string Qty { get; set; } = "0";
    [JsonPropertyName("quoteQty")] public string QuoteQty { get; set; } = "0";
    [JsonPropertyName("commission")] public string Commission { get; set; } = "0";
    [JsonPropertyName("commissionAsset")] public string CommissionAsset { get; set; } = string.Empty;
    [JsonPropertyName("time")] public long TimeMs { get; set; }
    [JsonPropertyName("isBuyer")] public bool IsBuyer { get; set; }
    [JsonPropertyName("isMaker")] public bool IsMaker { get; set; }

    [JsonIgnore] public string RawJson { get; set; } = "{}";
}

public sealed record BinanceSymbolInfo(string Symbol, string BaseAsset, string QuoteAsset);

// -------------------------------------------------------------- rate budget

/// <summary>
/// Conservative token bucket for Binance request weight (1200/min budget shared
/// process-wide). Registered as a singleton.
/// </summary>
public sealed class BinanceRateBudget
{
    private const int WeightPerMinute = 1200;
    private readonly object _lock = new();
    private DateTime _windowStart = DateTime.UtcNow;
    private int _used;

    public async Task ConsumeAsync(int weight, CancellationToken ct)
    {
        TimeSpan wait;
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if (now - _windowStart >= TimeSpan.FromMinutes(1))
            {
                _windowStart = now;
                _used = 0;
            }

            if (_used + weight <= WeightPerMinute)
            {
                _used += weight;
                return;
            }

            wait = _windowStart.AddMinutes(1) - now;
        }

        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, ct);
        }

        lock (_lock)
        {
            _windowStart = DateTime.UtcNow;
            _used = weight;
        }
    }
}

// ------------------------------------------------------------------ client

/// <summary>
/// Read-only Binance spot client. Signed endpoints use HMAC-SHA256 over the query
/// string with the X-MBX-APIKEY header. API secrets are used in-memory only and
/// never logged or echoed in errors.
/// </summary>
public sealed class BinanceClient(
    IIntegrationHttpClientFactory httpFactory, BinanceRateBudget budget, IConfiguration config)
{
    private readonly string _baseUrl =
        (config["Integrations:Binance:BaseUrl"] ?? "https://api.binance.com").TrimEnd('/');

    public async Task<BinanceApiRestrictions> GetApiRestrictionsAsync(
        string apiKey, string apiSecret, CancellationToken ct)
    {
        await budget.ConsumeAsync(1, ct);
        var json = await SignedGetAsync("/sapi/v1/account/apiRestrictions", [], apiKey, apiSecret, ct);
        return JsonSerializer.Deserialize<BinanceApiRestrictions>(json)
            ?? throw new IntegrationException("binance_bad_response", "Binance returned an unexpected response.");
    }

    /// <summary>Assets with a nonzero free+locked balance.</summary>
    public async Task<IReadOnlyList<string>> GetNonZeroBalanceAssetsAsync(
        string apiKey, string apiSecret, CancellationToken ct)
    {
        await budget.ConsumeAsync(20, ct);
        var json = await SignedGetAsync(
            "/api/v3/account", new() { ["omitZeroBalances"] = "true" }, apiKey, apiSecret, ct);

        using var doc = JsonDocument.Parse(json);
        var assets = new List<string>();
        if (doc.RootElement.TryGetProperty("balances", out var balances))
        {
            foreach (var balance in balances.EnumerateArray())
            {
                var free = ParseDecimal(balance, "free");
                var locked = ParseDecimal(balance, "locked");
                if (free + locked > 0 && balance.TryGetProperty("asset", out var asset))
                {
                    assets.Add(asset.GetString() ?? string.Empty);
                }
            }
        }

        return assets.Where(a => a.Length > 0).ToList();
    }

    /// <summary>All symbols currently trading, for candidate-symbol validation.</summary>
    public async Task<IReadOnlyDictionary<string, BinanceSymbolInfo>> GetExchangeSymbolsAsync(CancellationToken ct)
    {
        await budget.ConsumeAsync(20, ct);
        using var client = httpFactory.CreateClient("binance");
        using var response = await client.GetAsync($"{_baseUrl}/api/v3/exchangeInfo", ct);
        var json = await ReadOkAsync(response, ct);

        using var doc = JsonDocument.Parse(json);
        var symbols = new Dictionary<string, BinanceSymbolInfo>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.TryGetProperty("symbols", out var array))
        {
            foreach (var element in array.EnumerateArray())
            {
                if (element.TryGetProperty("status", out var status) && status.GetString() == "TRADING"
                    && element.TryGetProperty("symbol", out var symbol))
                {
                    var name = symbol.GetString() ?? string.Empty;
                    symbols[name] = new BinanceSymbolInfo(
                        name,
                        element.TryGetProperty("baseAsset", out var b) ? b.GetString() ?? "" : "",
                        element.TryGetProperty("quoteAsset", out var q) ? q.GetString() ?? "" : "");
                }
            }
        }

        return symbols;
    }

    public async Task<IReadOnlyList<BinanceMyTrade>> GetMyTradesAsync(
        string apiKey, string apiSecret, string symbol, long? fromId, int limit, CancellationToken ct)
    {
        await budget.ConsumeAsync(20, ct);
        var query = new Dictionary<string, string>
        {
            ["symbol"] = symbol,
            ["limit"] = limit.ToString(CultureInfo.InvariantCulture),
        };
        if (fromId is not null)
        {
            query["fromId"] = fromId.Value.ToString(CultureInfo.InvariantCulture);
        }

        var json = await SignedGetAsync("/api/v3/myTrades", query, apiKey, apiSecret, ct);
        using var doc = JsonDocument.Parse(json);
        var trades = new List<BinanceMyTrade>();
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            var trade = element.Deserialize<BinanceMyTrade>();
            if (trade is not null)
            {
                trade.RawJson = element.GetRawText();
                trades.Add(trade);
            }
        }

        return trades;
    }

    // ---------------------------------------------------------------- http

    private async Task<string> SignedGetAsync(
        string path, Dictionary<string, string> query, string apiKey, string apiSecret, CancellationToken ct)
    {
        query["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        query["recvWindow"] = "10000";
        var queryString = string.Join("&", query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        var signature = Sign(queryString, apiSecret);

        using var client = httpFactory.CreateClient("binance");
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"{_baseUrl}{path}?{queryString}&signature={signature}");
        request.Headers.Add("X-MBX-APIKEY", apiKey);
        using var response = await client.SendAsync(request, ct);
        return await ReadOkAsync(response, ct);
    }

    internal static string Sign(string queryString, string apiSecret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(apiSecret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(queryString))).ToLowerInvariant();
    }

    private static async Task<string> ReadOkAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if (response.IsSuccessStatusCode)
        {
            return body;
        }

        var status = (int)response.StatusCode;
        if (status is 401 or 403)
        {
            throw new IntegrationException(
                "binance_invalid_credentials",
                "Binance rejected the API key — check the key and secret, and any IP allow-list on the key.");
        }

        // Binance error bodies are {"code":-XXXX,"msg":"..."} — msg is safe to surface.
        var message = TryReadBinanceError(body);
        if (status == 429 || status == 418)
        {
            throw new IntegrationException(
                "binance_rate_limited", "Binance rate limit reached — sync will resume on the next run.");
        }

        throw new IntegrationException(
            "binance_error",
            message is null
                ? $"Binance returned HTTP {status}; we will retry on the next sync."
                : $"Binance error: {message}");
    }

    private static string? TryReadBinanceError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("msg", out var msg) ? msg.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static decimal ParseDecimal(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
        && decimal.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0m;
}
