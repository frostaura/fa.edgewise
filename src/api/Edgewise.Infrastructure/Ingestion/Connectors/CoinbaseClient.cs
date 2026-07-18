using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;

namespace Edgewise.Infrastructure.Ingestion.Connectors;

// ------------------------------------------------------------------ models

public sealed class CoinbaseFill
{
    [JsonPropertyName("entry_id")] public string EntryId { get; set; } = string.Empty;
    [JsonPropertyName("trade_id")] public string TradeId { get; set; } = string.Empty;
    [JsonPropertyName("order_id")] public string OrderId { get; set; } = string.Empty;
    [JsonPropertyName("trade_time")] public DateTime TradeTime { get; set; }
    [JsonPropertyName("trade_type")] public string TradeType { get; set; } = "FILL";
    [JsonPropertyName("price")] public string Price { get; set; } = "0";
    [JsonPropertyName("size")] public string Size { get; set; } = "0";
    [JsonPropertyName("commission")] public string Commission { get; set; } = "0";
    [JsonPropertyName("product_id")] public string ProductId { get; set; } = string.Empty;
    [JsonPropertyName("sequence_timestamp")] public string? SequenceTimestamp { get; set; }
    [JsonPropertyName("size_in_quote")] public bool SizeInQuote { get; set; }
    [JsonPropertyName("side")] public string Side { get; set; } = "BUY";

    [JsonIgnore] public string RawJson { get; set; } = "{}";
}

public sealed record CoinbaseFillsPage(IReadOnlyList<CoinbaseFill> Fills, string? Cursor);

// ------------------------------------------------------------------ client

/// <summary>
/// Coinbase Advanced Trade (brokerage v3) client authenticated with per-request
/// CDP JWTs (see <see cref="CoinbaseJwtGenerator"/>). Read-only usage.
/// </summary>
public sealed class CoinbaseClient(IIntegrationHttpClientFactory httpFactory, IConfiguration config)
{
    private readonly string _baseUrl =
        (config["Integrations:Coinbase:BaseUrl"] ?? "https://api.coinbase.com").TrimEnd('/');

    /// <summary>JWT "uri" claim host — always the canonical API host per the CDP spec.</summary>
    private const string JwtHost = "api.coinbase.com";

    /// <summary>Credential validation call; throws IntegrationException on 401.</summary>
    public async Task ValidateCredentialsAsync(string keyName, string privateKeyPem, CancellationToken ct)
    {
        const string path = "/api/v3/brokerage/accounts";
        _ = await GetAsync(keyName, privateKeyPem, path, "limit=1", ct);
    }

    public async Task<CoinbaseFillsPage> GetFillsAsync(
        string keyName, string privateKeyPem, string? cursor, string? startSequenceTimestamp,
        int limit, CancellationToken ct)
    {
        const string path = "/api/v3/brokerage/orders/historical/fills";
        var query = $"limit={limit}";
        if (!string.IsNullOrEmpty(cursor))
        {
            query += $"&cursor={Uri.EscapeDataString(cursor)}";
        }

        if (!string.IsNullOrEmpty(startSequenceTimestamp))
        {
            query += $"&start_sequence_timestamp={Uri.EscapeDataString(startSequenceTimestamp)}";
        }

        var json = await GetAsync(keyName, privateKeyPem, path, query, ct);
        using var doc = JsonDocument.Parse(json);
        var fills = new List<CoinbaseFill>();
        if (doc.RootElement.TryGetProperty("fills", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in array.EnumerateArray())
            {
                var fill = element.Deserialize<CoinbaseFill>();
                if (fill is not null && fill.ProductId.Length > 0)
                {
                    fill.RawJson = element.GetRawText();
                    fills.Add(fill);
                }
            }
        }

        var nextCursor = doc.RootElement.TryGetProperty("cursor", out var cursorElement)
            ? cursorElement.GetString()
            : null;
        return new CoinbaseFillsPage(fills, string.IsNullOrEmpty(nextCursor) ? null : nextCursor);
    }

    private async Task<string> GetAsync(
        string keyName, string privateKeyPem, string path, string query, CancellationToken ct)
    {
        var jwt = CoinbaseJwtGenerator.Generate(keyName, privateKeyPem, "GET", JwtHost, path);

        using var client = httpFactory.CreateClient("coinbase");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}{path}?{query}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        using var response = await client.SendAsync(request, ct);

        var body = await response.Content.ReadAsStringAsync(ct);
        if (response.IsSuccessStatusCode)
        {
            return body;
        }

        var status = (int)response.StatusCode;
        if (status is 401 or 403)
        {
            throw new IntegrationException(
                "coinbase_invalid_credentials",
                "Coinbase rejected the API key — check the key name and private key from the CDP portal.");
        }

        throw new IntegrationException(
            "coinbase_error", $"Coinbase returned HTTP {status}; we will retry on the next sync.");
    }
}
