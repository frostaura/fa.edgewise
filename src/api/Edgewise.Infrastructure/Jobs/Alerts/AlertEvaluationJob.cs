using System.Text.Json;
using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Edgewise.Infrastructure.Jobs.Alerts;

/// <summary>
/// Evaluates enabled alerts against QuoteCache / SentimentReading / CatalystEvent /
/// Zone data every minute (Hangfire). Runs without an authenticated user, so all
/// queries ignore the per-user filters and scope by Alert.UserId explicitly.
///
/// On trigger (respecting CooldownMinutes via LastTriggeredAt): creates a
/// Notification, sends Web Push to the user's subscriptions (skipped when VAPID
/// keys are unset; dead endpoints pruned) and email via MailKit (skipped when
/// Smtp__Host is unset).
///
/// FundingRate alerts are stored but not evaluated yet — no funding-rate data
/// source exists in this deployment.
/// </summary>
public class AlertEvaluationJob(
    EdgewiseDbContext db,
    WebPushSender push,
    EmailSender email,
    ILogger<AlertEvaluationJob> logger)
{
    private sealed record Trigger(string Title, string Body, string? DeepLink);

    public async Task RunAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var alerts = await db.Alerts.IgnoreQueryFilters().Where(a => a.Enabled).ToListAsync(ct);
        var due = alerts
            .Where(a => a.LastTriggeredAt is null
                || a.LastTriggeredAt <= now.AddMinutes(-Math.Max(a.CooldownMinutes, 1)))
            .ToList();
        if (due.Count == 0)
        {
            return;
        }

        var context = await LoadContextAsync(due, now, ct);
        var triggeredCount = 0;

        foreach (var alert in due)
        {
            Trigger? trigger;
            try
            {
                trigger = await EvaluateAsync(alert, context, now, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Alert {AlertId} ({Kind}) evaluation failed", alert.Id, alert.Kind);
                continue;
            }

            if (trigger is null)
            {
                continue;
            }

            alert.LastTriggeredAt = now;
            triggeredCount++;

            var channels = new List<string> { "inapp" };
            if (await push.SendAsync(db, alert.UserId, trigger.Title, trigger.Body, trigger.DeepLink, ct))
            {
                channels.Add("push");
            }

            if (context.Users.TryGetValue(alert.UserId, out var user)
                && await email.SendAsync(user.Email, $"Edgewise alert: {trigger.Title}", trigger.Body, ct))
            {
                channels.Add("email");
            }

            db.Notifications.Add(new Notification
            {
                Id = Guid.NewGuid(),
                UserId = alert.UserId,
                AlertId = alert.Id,
                Title = trigger.Title,
                Body = trigger.Body,
                DeepLink = trigger.DeepLink,
                Channels = string.Join(",", channels),
                CreatedAt = now,
            });
        }

        await db.SaveChangesAsync(ct);
        if (triggeredCount > 0)
        {
            logger.LogInformation("Alert evaluation triggered {Count} notification(s)", triggeredCount);
        }
    }

    // ------------------------------------------------------------ evaluation

    private async Task<Trigger?> EvaluateAsync(Alert alert, EvalContext context, DateTime now, CancellationToken ct)
    {
        var p = ParseParams(alert.ParamsJson);
        switch (alert.Kind)
        {
            case AlertKind.PriceCross:
                {
                    if (alert.InstrumentId is not { } instrumentId
                        || !context.Quotes.TryGetValue(instrumentId, out var quote))
                    {
                        return null;
                    }

                    var level = GetDecimal(p, "level") ?? 0;
                    var direction = GetString(p, "direction") ?? "above";
                    if (level <= 0)
                    {
                        return null;
                    }

                    var crossed = direction == "below" ? quote.Price <= level : quote.Price >= level;
                    if (!crossed)
                    {
                        return null;
                    }

                    var symbol = context.Symbol(instrumentId);
                    return new Trigger(
                        $"{symbol} crossed {direction} {level}",
                        $"{symbol} is at {quote.Price} (as of {quote.AsOf:HH:mm} UTC), {direction} your {level} level.",
                        $"/portfolio/assets/{instrumentId}");
                }

            case AlertKind.PctMove:
                {
                    if (alert.InstrumentId is not { } instrumentId
                        || !context.Quotes.TryGetValue(instrumentId, out var quote))
                    {
                        return null;
                    }

                    var pct = GetDecimal(p, "pct") ?? 0;
                    var windowMinutes = (int)(GetDecimal(p, "windowMinutes") ?? 60);
                    if (pct <= 0 || windowMinutes <= 0)
                    {
                        return null;
                    }

                    // Reference: the earliest bar close inside the window (H1 bars are the finest we store).
                    var windowStart = now.AddMinutes(-windowMinutes);
                    var reference = await db.PriceBars
                        .Where(b => b.InstrumentId == instrumentId && b.Ts >= windowStart && b.Ts <= now)
                        .OrderBy(b => b.Ts)
                        .Select(b => (decimal?)b.C)
                        .FirstOrDefaultAsync(ct);
                    if (reference is not > 0)
                    {
                        return null;
                    }

                    var movePct = (quote.Price - reference.Value) / reference.Value * 100m;
                    if (Math.Abs(movePct) < pct)
                    {
                        return null;
                    }

                    var symbol = context.Symbol(instrumentId);
                    return new Trigger(
                        $"{symbol} moved {Math.Round(movePct, 2)}% in {windowMinutes}m",
                        $"{symbol} moved {Math.Round(movePct, 2)}% over the last {windowMinutes} minutes (now {quote.Price}).",
                        $"/portfolio/assets/{instrumentId}");
                }

            case AlertKind.ZoneTouch:
                {
                    var zoneIdText = GetString(p, "zoneId");
                    if (!Guid.TryParse(zoneIdText, out var zoneId)
                        || !context.Zones.TryGetValue(zoneId, out var zone)
                        || zone.UserId != alert.UserId
                        || !context.Quotes.TryGetValue(zone.InstrumentId, out var quote))
                    {
                        return null;
                    }

                    if (quote.Price < zone.PriceLow || quote.Price > zone.PriceHigh)
                    {
                        return null;
                    }

                    var symbol = context.Symbol(zone.InstrumentId);
                    return new Trigger(
                        $"{symbol} touched your zone",
                        $"{symbol} at {quote.Price} is inside your {zone.PriceLow}–{zone.PriceHigh} zone. Write the plan before you act.",
                        $"/portfolio/assets/{zone.InstrumentId}");
                }

            case AlertKind.FgExtreme:
                {
                    if (context.LatestSentiment is not { } reading)
                    {
                        return null;
                    }

                    var min = (int)(GetDecimal(p, "min") ?? 20);
                    var max = (int)(GetDecimal(p, "max") ?? 80);
                    if (reading.Value > min && reading.Value < max)
                    {
                        return null;
                    }

                    var side = reading.Value <= min ? "fear" : "greed";
                    return new Trigger(
                        $"Fear & Greed extreme: {reading.Value} ({reading.Label})",
                        $"Crypto Fear & Greed is at {reading.Value} ({reading.Label}) — extreme {side} territory.",
                        "/radar?tab=sentiment");
                }

            case AlertKind.CatalystT24:
                {
                    var relevant = alert.InstrumentId is { } instrumentId
                        ? context.RedCatalysts24h
                            .Where(c => c.InstrumentId == instrumentId)
                            .ToList()
                        : context.RedCatalysts24h
                            .Where(c => (c.UserId == null || c.UserId == alert.UserId)
                                && (c.InstrumentId == null
                                    || context.RelevantInstruments(alert.UserId).Contains(c.InstrumentId.Value)))
                            .ToList();
                    if (relevant.Count == 0)
                    {
                        return null;
                    }

                    var first = relevant.OrderBy(c => c.At).First();
                    var extra = relevant.Count > 1 ? $" (+{relevant.Count - 1} more)" : string.Empty;
                    return new Trigger(
                        $"Red catalyst inside 24h: {first.Title}{extra}",
                        $"{first.Title} at {first.At:yyyy-MM-dd HH:mm} UTC is inside the next 24 hours.",
                        "/radar?tab=calendar");
                }

            case AlertKind.FundingRate:
                // No funding-rate data source available yet — never triggers.
                return null;

            default:
                return null;
        }
    }

    // -------------------------------------------------------------- context

    private sealed class EvalContext(
        Dictionary<Guid, QuoteCache> quotes,
        Dictionary<Guid, string> symbols,
        Dictionary<Guid, Zone> zones,
        SentimentReading? latestSentiment,
        List<CatalystEvent> redCatalysts,
        Dictionary<Guid, User> users,
        Dictionary<Guid, HashSet<Guid>> relevantByUser)
    {
        public Dictionary<Guid, QuoteCache> Quotes { get; } = quotes;
        public Dictionary<Guid, Zone> Zones { get; } = zones;
        public SentimentReading? LatestSentiment { get; } = latestSentiment;
        public List<CatalystEvent> RedCatalysts24h { get; } = redCatalysts;
        public Dictionary<Guid, User> Users { get; } = users;

        public string Symbol(Guid instrumentId) =>
            symbols.TryGetValue(instrumentId, out var symbol) ? symbol : "Instrument";

        public HashSet<Guid> RelevantInstruments(Guid userId) =>
            relevantByUser.TryGetValue(userId, out var set) ? set : [];
    }

    private async Task<EvalContext> LoadContextAsync(List<Alert> due, DateTime now, CancellationToken ct)
    {
        var zoneIds = due
            .Where(a => a.Kind == AlertKind.ZoneTouch)
            .Select(a => Guid.TryParse(GetString(ParseParams(a.ParamsJson), "zoneId"), out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        var zones = zoneIds.Count == 0
            ? []
            : await db.Zones.IgnoreQueryFilters()
                .Where(z => zoneIds.Contains(z.Id) && !z.Archived)
                .ToDictionaryAsync(z => z.Id, ct);

        var instrumentIds = due.Where(a => a.InstrumentId != null).Select(a => a.InstrumentId!.Value)
            .Concat(zones.Values.Select(z => z.InstrumentId))
            .Distinct()
            .ToList();
        var quotes = instrumentIds.Count == 0
            ? []
            : await db.QuoteCaches.Where(q => instrumentIds.Contains(q.InstrumentId))
                .ToDictionaryAsync(q => q.InstrumentId, ct);

        var latestSentiment = due.Any(a => a.Kind == AlertKind.FgExtreme)
            ? await db.SentimentReadings.Where(s => s.Kind == SentimentKind.CryptoFG)
                .OrderByDescending(s => s.Date)
                .FirstOrDefaultAsync(ct)
            : null;

        var horizon = now.AddHours(24);
        var redCatalysts = due.Any(a => a.Kind == AlertKind.CatalystT24)
            ? await db.CatalystEvents.IgnoreQueryFilters()
                .Where(c => c.Severity == CatalystSeverity.Red && c.At >= now && c.At < horizon)
                .ToListAsync(ct)
            : [];

        // Held/watched instruments per user, for instrument-less catalystT24 alerts.
        var catalystUserIds = due
            .Where(a => a.Kind == AlertKind.CatalystT24 && a.InstrumentId == null)
            .Select(a => a.UserId)
            .Distinct()
            .ToList();
        var relevantByUser = new Dictionary<Guid, HashSet<Guid>>();
        if (catalystUserIds.Count > 0)
        {
            var holdings = await db.Holdings.IgnoreQueryFilters()
                .Where(h => catalystUserIds.Contains(h.UserId))
                .Select(h => new { h.UserId, h.InstrumentId })
                .ToListAsync(ct);
            var openTrades = await db.Trades.IgnoreQueryFilters()
                .Where(t => catalystUserIds.Contains(t.UserId) && t.Status == TradeStatus.Open)
                .Select(t => new { t.UserId, t.InstrumentId })
                .ToListAsync(ct);
            var watchItems = await db.WatchlistItems.IgnoreQueryFilters()
                .Where(w => catalystUserIds.Contains(w.Watchlist.UserId))
                .Select(w => new { w.Watchlist.UserId, w.InstrumentId })
                .ToListAsync(ct);
            foreach (var row in holdings.Concat(openTrades).Concat(watchItems))
            {
                if (!relevantByUser.TryGetValue(row.UserId, out var set))
                {
                    relevantByUser[row.UserId] = set = [];
                }

                set.Add(row.InstrumentId);
            }
        }

        var symbolIds = instrumentIds
            .Concat(redCatalysts.Where(c => c.InstrumentId != null).Select(c => c.InstrumentId!.Value))
            .Distinct()
            .ToList();
        var symbols = symbolIds.Count == 0
            ? []
            : await db.Instruments.Where(i => symbolIds.Contains(i.Id))
                .ToDictionaryAsync(i => i.Id, i => i.Symbol, ct);

        var userIds = due.Select(a => a.UserId).Distinct().ToList();
        var users = await db.Users.IgnoreQueryFilters()
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, ct);

        return new EvalContext(quotes, symbols, zones, latestSentiment, redCatalysts, users, relevantByUser);
    }

    // --------------------------------------------------------------- helpers

    private static JsonElement ParseParams(string? paramsJson)
    {
        if (string.IsNullOrWhiteSpace(paramsJson))
        {
            return default;
        }

        try
        {
            using var doc = JsonDocument.Parse(paramsJson);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static decimal? GetDecimal(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number
            ? el.GetDecimal()
            : null;

    private static string? GetString(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
}
