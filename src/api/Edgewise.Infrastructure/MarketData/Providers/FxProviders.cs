using System.Globalization;
using System.Text.Json;

namespace Edgewise.Infrastructure.MarketData.Providers;

/// <summary>
/// Frankfurter (ECB reference rates) adapter. Supports historical dates; weekends
/// and holidays resolve to the closest previous business day server-side.
/// </summary>
public sealed class FrankfurterFxProvider(ProviderHttp http) : IFxProvider
{
    public string Name => ProviderNames.Frankfurter;

    public async Task<decimal?> GetDailyRateAsync(
        string baseCurrency, string quoteCurrency, DateOnly date, CancellationToken ct)
    {
        var url = $"https://api.frankfurter.dev/v1/{date:yyyy-MM-dd}" +
            $"?base={Uri.EscapeDataString(baseCurrency)}&symbols={Uri.EscapeDataString(quoteCurrency)}";
        var body = await http.GetStringAsync(Name, url, ct);
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("rates", out var rates)
            && rates.TryGetProperty(quoteCurrency.ToUpperInvariant(), out var rate))
        {
            return rate.GetDecimal();
        }

        return null;
    }
}

/// <summary>
/// open.er-api.com fallback adapter. The free endpoint only serves the latest
/// snapshot, so any request older than yesterday returns null and lets the chain
/// (or the walk-back) move on.
/// </summary>
public sealed class ExchangeRateApiFxProvider(ProviderHttp http) : IFxProvider
{
    public string Name => ProviderNames.ExchangeRateApi;

    public async Task<decimal?> GetDailyRateAsync(
        string baseCurrency, string quoteCurrency, DateOnly date, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (date < today.AddDays(-1))
        {
            return null;
        }

        var url = $"https://open.er-api.com/v6/latest/{Uri.EscapeDataString(baseCurrency.ToUpperInvariant())}";
        var body = await http.GetStringAsync(Name, url, ct);
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("result", out var result) && result.GetString() != "success")
        {
            return null;
        }

        if (doc.RootElement.TryGetProperty("rates", out var rates)
            && rates.TryGetProperty(quoteCurrency.ToUpperInvariant(), out var rate))
        {
            // Rates arrive as doubles; go through invariant string to keep decimal parsing lenient.
            return decimal.Parse(
                rate.GetDouble().ToString("R", CultureInfo.InvariantCulture),
                NumberStyles.Float,
                CultureInfo.InvariantCulture);
        }

        return null;
    }
}
