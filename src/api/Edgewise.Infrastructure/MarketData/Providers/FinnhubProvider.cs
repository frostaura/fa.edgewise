using System.Globalization;
using System.Text.Json;
using Edgewise.Domain.Entities;

namespace Edgewise.Infrastructure.MarketData.Providers;

/// <summary>
/// Finnhub adapter: earnings calendar, economic calendar and company news.
/// The API key comes from the FINNHUB_API_KEY environment variable; when absent
/// every call is a silent no-op returning empty results.
/// </summary>
public sealed class FinnhubProvider(ProviderHttp http, string? apiKey)
    : ICalendarProvider, ICompanyNewsProvider
{
    private const string BaseUrl = "https://finnhub.io/api/v1";

    public string Name => ProviderNames.Finnhub;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(apiKey);

    public async Task<IReadOnlyList<FetchedCalendarEvent>> GetEarningsAsync(
        DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (!IsConfigured)
        {
            return [];
        }

        var url = $"{BaseUrl}/calendar/earnings?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&token={apiKey}";
        var body = await http.GetStringAsync(Name, url, ct);
        using var doc = JsonDocument.Parse(body);
        var events = new List<FetchedCalendarEvent>();
        if (!doc.RootElement.TryGetProperty("earningsCalendar", out var calendar)
            || calendar.ValueKind != JsonValueKind.Array)
        {
            return events;
        }

        foreach (var entry in calendar.EnumerateArray())
        {
            var symbol = entry.GetProperty("symbol").GetString();
            var dateText = entry.GetProperty("date").GetString();
            if (string.IsNullOrEmpty(symbol) || !TryParseDate(dateText, out var date))
            {
                continue;
            }

            events.Add(new FetchedCalendarEvent(
                CatalystKind.Earnings,
                symbol,
                $"{symbol} earnings",
                date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                CatalystSeverity.Red,
                $"finnhub:earnings:{symbol}:{date:yyyy-MM-dd}"));
        }

        return events;
    }

    public async Task<IReadOnlyList<FetchedCalendarEvent>> GetEconomicEventsAsync(
        DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (!IsConfigured)
        {
            return [];
        }

        var url = $"{BaseUrl}/calendar/economic?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&token={apiKey}";
        var body = await http.GetStringAsync(Name, url, ct);
        using var doc = JsonDocument.Parse(body);
        var events = new List<FetchedCalendarEvent>();
        if (!doc.RootElement.TryGetProperty("economicCalendar", out var calendar)
            || calendar.ValueKind != JsonValueKind.Array)
        {
            return events;
        }

        foreach (var entry in calendar.EnumerateArray())
        {
            var impact = entry.TryGetProperty("impact", out var impactElement)
                ? impactElement.GetString()?.ToLowerInvariant()
                : null;
            var severity = impact switch
            {
                "high" => (CatalystSeverity?)CatalystSeverity.Red,
                "medium" => CatalystSeverity.Amber,
                _ => null, // low/unknown-impact prints are noise; skip them.
            };
            var name = entry.TryGetProperty("event", out var eventElement) ? eventElement.GetString() : null;
            var timeText = entry.TryGetProperty("time", out var timeElement) ? timeElement.GetString() : null;
            if (severity is null || string.IsNullOrEmpty(name)
                || !DateTime.TryParse(
                    timeText, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
            {
                continue;
            }

            var country = entry.TryGetProperty("country", out var countryElement)
                ? countryElement.GetString()
                : null;
            var title = string.IsNullOrEmpty(country) ? name : $"{country}: {name}";
            events.Add(new FetchedCalendarEvent(
                CatalystKind.Macro,
                Symbol: null,
                title,
                at,
                severity.Value,
                $"finnhub:econ:{country}:{name}:{at:yyyy-MM-ddTHH:mm}"));
        }

        return events;
    }

    public async Task<IReadOnlyList<FetchedNewsItem>> GetCompanyNewsAsync(
        string providerSymbol, DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (!IsConfigured)
        {
            return [];
        }

        var url = $"{BaseUrl}/company-news?symbol={Uri.EscapeDataString(providerSymbol)}" +
            $"&from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&token={apiKey}";
        var body = await http.GetStringAsync(Name, url, ct);
        using var doc = JsonDocument.Parse(body);
        var items = new List<FetchedNewsItem>();
        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            var headline = entry.GetProperty("headline").GetString();
            var link = entry.GetProperty("url").GetString();
            if (string.IsNullOrEmpty(headline) || string.IsNullOrEmpty(link))
            {
                continue;
            }

            var publishedAt = DateTimeOffset.FromUnixTimeSeconds(entry.GetProperty("datetime").GetInt64()).UtcDateTime;
            var source = entry.TryGetProperty("source", out var sourceElement)
                ? sourceElement.GetString() ?? "finnhub"
                : "finnhub";
            var summary = entry.TryGetProperty("summary", out var summaryElement)
                ? summaryElement.GetString()
                : null;
            items.Add(new FetchedNewsItem(headline, link, source, publishedAt, summary));
        }

        return items;
    }

    private static bool TryParseDate(string? text, out DateOnly date) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
}
