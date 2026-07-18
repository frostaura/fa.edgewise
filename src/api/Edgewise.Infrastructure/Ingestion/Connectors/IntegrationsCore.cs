using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Edgewise.Infrastructure.Ingestion.Connectors;

/// <summary>
/// Creates HttpClients for exchange connectors. Abstracted (instead of using
/// IHttpClientFactory, which is not referenced by this project) so integration
/// tests can substitute fake handlers per upstream host. Names: "polymarket",
/// "polymarket-gamma", "binance", "coinbase".
/// </summary>
public interface IIntegrationHttpClientFactory
{
    HttpClient CreateClient(string name);
}

public sealed class DefaultIntegrationHttpClientFactory : IIntegrationHttpClientFactory
{
    private static readonly SocketsHttpHandler Handler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    };

    public HttpClient CreateClient(string name) =>
        new(Handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(60) };
}

/// <summary>
/// Connector failure with a plain-language, secret-free message that is safe to
/// show to the user (INT-005) and to store in Account.LastSyncStatsJson.
/// </summary>
public sealed class IntegrationException(string code, string userMessage, Exception? inner = null)
    : Exception(userMessage, inner)
{
    public string Code { get; } = code;
}

public static class SourceHasher
{
    /// <summary>sha256 hex of a deterministic per-record key, e.g. "binance:BTCUSDT:12345".</summary>
    public static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public static class IntegrationJson
{
    /// <summary>camelCase, nulls omitted — matches the API's wire contract for stats blobs.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>
/// The persisted shape of Account.LastSyncStatsJson. Doubles as progress state
/// for resumable backfills (Binance per-symbol trade ids, Polymarket offset,
/// Coinbase sequence cursor) and as the payload behind the frontend progress UI.
/// </summary>
public sealed class AccountSyncStats
{
    public DateTime? LastRunAt { get; set; }

    /// <summary>"ok" | "error".</summary>
    public string? LastRunStatus { get; set; }

    /// <summary>Plain-language error from the last failed sync. Never contains secrets.</summary>
    public string? Error { get; set; }

    /// <summary>Non-fatal notice (e.g. the Polymarket wrong-wallet hint).</summary>
    public string? Warning { get; set; }

    public bool Syncing { get; set; }

    public int LastRunFills { get; set; }

    public long TotalFills { get; set; }

    // ------------------------------------------------ Binance backfill state
    public int? SymbolsTotal { get; set; }
    public int? SymbolsDone { get; set; }
    public string? CurrentSymbol { get; set; }
    public Dictionary<string, long>? LastTradeIdPerSymbol { get; set; }

    // ------------------------------------------------- Polymarket state
    public long? PolymarketOffset { get; set; }

    // -------------------------------------------------- Coinbase state
    public string? CoinbaseLastSequenceTimestamp { get; set; }

    public static AccountSyncStats Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new AccountSyncStats();
        }

        try
        {
            return JsonSerializer.Deserialize<AccountSyncStats>(json, IntegrationJson.Options)
                ?? new AccountSyncStats();
        }
        catch (JsonException)
        {
            return new AccountSyncStats();
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, IntegrationJson.Options);
}
