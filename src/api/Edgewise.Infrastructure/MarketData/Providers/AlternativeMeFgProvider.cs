using System.Globalization;
using System.Text.Json;

namespace Edgewise.Infrastructure.MarketData.Providers;

/// <summary>alternative.me crypto Fear &amp; Greed index adapter.</summary>
public sealed class AlternativeMeFgProvider(ProviderHttp http) : ISentimentProvider
{
    public string Name => ProviderNames.AlternativeMe;

    public async Task<IReadOnlyList<SentimentPoint>> GetRecentAsync(int days, CancellationToken ct)
    {
        var url = $"https://api.alternative.me/fng/?limit={days}";
        var body = await http.GetStringAsync(Name, url, ct);
        using var doc = JsonDocument.Parse(body);
        var points = new List<SentimentPoint>();
        foreach (var entry in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            if (!int.TryParse(entry.GetProperty("value").GetString(), out var value)
                || !long.TryParse(
                    entry.GetProperty("timestamp").GetString(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var unix))
            {
                continue;
            }

            var date = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime);
            var label = entry.GetProperty("value_classification").GetString() ?? string.Empty;
            points.Add(new SentimentPoint(date, value, label));
        }

        return points;
    }
}
