using System.Globalization;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace Edgewise.Infrastructure.MarketData.Providers;

/// <summary>
/// Curated RSS/Atom headline aggregator, parsed manually with XDocument
/// (System.ServiceModel.Syndication is not referenced). Individual feed failures
/// are logged and skipped so one dead feed never sinks the poll.
/// </summary>
public sealed class RssNewsProvider(ProviderHttp http, ILogger<RssNewsProvider> logger) : INewsFeedProvider
{
    /// <summary>(source label, feed url) pairs. SA markets + crypto skew, per product focus.</summary>
    internal static readonly (string Source, string Url)[] DefaultFeeds =
    [
        ("Moneyweb", "https://www.moneyweb.co.za/feed/"),
        ("BusinessTech", "https://businesstech.co.za/news/feed/"),
        ("MyBroadband", "https://mybroadband.co.za/news/feed"),
        ("Daily Maverick Business", "https://www.dailymaverick.co.za/dmrss/?category=Business%20Maverick"),
        ("CoinDesk", "https://www.coindesk.com/arc/outboundfeeds/rss/"),
        ("Cointelegraph", "https://cointelegraph.com/rss"),
    ];

    public string Name => ProviderNames.Rss;

    public async Task<IReadOnlyList<FetchedNewsItem>> GetLatestAsync(CancellationToken ct)
    {
        var items = new List<FetchedNewsItem>();
        foreach (var (source, url) in DefaultFeeds)
        {
            try
            {
                var xml = await http.GetStringAsync(Name, url, ct);
                items.AddRange(ParseFeed(xml, source));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "RSS feed '{Source}' ({Url}) failed; skipping.", source, url);
            }
        }

        return items;
    }

    /// <summary>Parses RSS 2.0 items or Atom entries out of a feed document.</summary>
    internal static List<FetchedNewsItem> ParseFeed(string xml, string source)
    {
        var doc = XDocument.Parse(xml, LoadOptions.None);
        var items = new List<FetchedNewsItem>();
        var root = doc.Root;
        if (root is null)
        {
            return items;
        }

        if (root.Name.LocalName == "rss")
        {
            foreach (var item in root.Descendants().Where(e => e.Name.LocalName == "item"))
            {
                var title = ElementValue(item, "title");
                var link = ElementValue(item, "link");
                if (title is null || link is null)
                {
                    continue;
                }

                items.Add(new FetchedNewsItem(
                    title,
                    link,
                    source,
                    ParseDate(ElementValue(item, "pubDate")),
                    Truncate(StripHtml(ElementValue(item, "description")), 500)));
            }
        }
        else if (root.Name.LocalName == "feed")
        {
            foreach (var entry in root.Elements().Where(e => e.Name.LocalName == "entry"))
            {
                var title = ElementValue(entry, "title");
                var link = entry.Elements().FirstOrDefault(e => e.Name.LocalName == "link")?
                    .Attribute("href")?.Value;
                if (title is null || string.IsNullOrWhiteSpace(link))
                {
                    continue;
                }

                items.Add(new FetchedNewsItem(
                    title,
                    link,
                    source,
                    ParseDate(ElementValue(entry, "published") ?? ElementValue(entry, "updated")),
                    Truncate(StripHtml(ElementValue(entry, "summary")), 500)));
            }
        }

        return items;
    }

    private static string? ElementValue(XElement parent, string localName)
    {
        var value = parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static DateTime ParseDate(string? text)
    {
        if (text is not null && DateTimeOffset.TryParse(
                text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed.UtcDateTime;
        }

        // RFC 822 with textual zones ("EST") that DateTimeOffset rejects — retry without the zone.
        if (text is not null)
        {
            var trimmed = text.Trim();
            var lastSpace = trimmed.LastIndexOf(' ');
            if (lastSpace > 0 && DateTimeOffset.TryParse(
                    trimmed[..lastSpace], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var partial))
            {
                return partial.UtcDateTime;
            }
        }

        return DateTime.UtcNow;
    }

    private static string? StripHtml(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var chars = new char[text.Length];
        var length = 0;
        var insideTag = false;
        foreach (var c in text)
        {
            if (c == '<')
            {
                insideTag = true;
            }
            else if (c == '>')
            {
                insideTag = false;
            }
            else if (!insideTag)
            {
                chars[length++] = c;
            }
        }

        var stripped = new string(chars, 0, length).Trim();
        return stripped.Length == 0 ? null : stripped;
    }

    private static string? Truncate(string? text, int maxLength) =>
        text is null || text.Length <= maxLength ? text : text[..maxLength];
}
