using System.Text.Json;
using Edgewise.Domain.Engines.Indicators;
using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Auth;
using Edgewise.Infrastructure.Data;
using Edgewise.Infrastructure.MarketData;
using Microsoft.EntityFrameworkCore;

// Dedicated sub-namespace so market DTO names never collide with other endpoint modules.
namespace Edgewise.Api.Endpoints.Market;

public sealed record InstrumentDto(
    Guid Id,
    string Symbol,
    string Name,
    AssetClass AssetClass,
    string? Exchange,
    string Currency,
    Dictionary<string, string>? ProviderSymbols);

public sealed record CreateInstrumentRequest(
    string Symbol,
    string Name,
    AssetClass AssetClass,
    string? Exchange,
    string Currency,
    Dictionary<string, string>? ProviderSymbols);

public sealed record BarDto(DateTime Ts, decimal O, decimal H, decimal L, decimal C, decimal V);

public sealed record BarsResponse(IReadOnlyList<BarDto> Bars, bool? Stale);

public sealed record MacdSeriesDto(decimal?[] Macd, decimal?[] Signal, decimal?[] Hist);

public sealed record BollingerSeriesDto(decimal?[] Mid, decimal?[] Upper, decimal?[] Lower);

public sealed record IndicatorsResponse(
    Timeframe Timeframe, IReadOnlyList<DateTime> Ts, Dictionary<string, object> Series);

public sealed record QuoteDto(
    Guid InstrumentId, decimal? Price, DateTime? AsOf, string? Provider, bool Stale, int? AgeMinutes);

public sealed record FxRateDto(string Base, string Quote, DateOnly Date, decimal Rate, string Provider);

public sealed record SentimentPointDto(DateOnly Date, int Value, string Label);

public sealed record SentimentResponse(SentimentPointDto? Latest, IReadOnlyList<SentimentPointDto> History);

public sealed record NewsItemDto(
    Guid Id, string Title, string Url, string Source, DateTime PublishedAt, string? Summary,
    IReadOnlyList<Guid>? InstrumentIds);

public sealed record CatalystEventDto(
    Guid Id, CatalystKind Kind, Guid? InstrumentId, string Title, DateTime At,
    CatalystSeverity Severity, string? SourceRef, bool Own);

public sealed record CreateCatalystEventRequest(
    string Title, DateTime At, CatalystKind? Kind, Guid? InstrumentId, CatalystSeverity? Severity);

/// <summary>Market data: instrument catalogue, bars, indicators, quotes, FX, sentiment, news and catalysts.</summary>
public sealed class MarketEndpoints : IEndpointModule
{
    private static readonly TimeSpan InlineFetchBudget = TimeSpan.FromSeconds(5);
    private const int MaxQuoteBatch = 50;

    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/market").RequireAuthorization();

        group.MapGet("/instruments/search", SearchInstruments);
        group.MapPost("/instruments", CreateInstrument);
        group.MapGet("/instruments/{id:guid}", GetInstrument);
        group.MapGet("/instruments/{id:guid}/bars", GetBars);
        group.MapGet("/instruments/{id:guid}/indicators", GetIndicators);
        group.MapGet("/quotes", GetQuotes);
        group.MapGet("/fx", GetFxRate);
        group.MapGet("/sentiment", GetSentiment);
        group.MapGet("/news", GetNews);
        group.MapGet("/calendar", GetCalendar);
        group.MapPost("/calendar", CreateCalendarEvent);
        group.MapDelete("/calendar/{id:guid}", DeleteCalendarEvent);
    }

    // ----------------------------------------------------------- instruments

    private static async Task<IResult> SearchInstruments(string? q, EdgewiseDbContext db, CancellationToken ct)
    {
        var query = q?.Trim();
        if (string.IsNullOrEmpty(query))
        {
            throw ApiException.BadRequest("missing_query", "Query parameter 'q' is required.");
        }

        var pattern = $"%{EscapeLike(query)}%";
        var results = await db.Instruments
            .Where(i => EF.Functions.ILike(i.Symbol, pattern) || EF.Functions.ILike(i.Name, pattern))
            .OrderBy(i => i.Symbol)
            .Take(20)
            .ToListAsync(ct);
        return Results.Ok(results.Select(ToDto).ToList());
    }

    private static async Task<IResult> CreateInstrument(
        CreateInstrumentRequest request, EdgewiseDbContext db, CancellationToken ct)
    {
        var symbol = request.Symbol?.Trim().ToUpperInvariant();
        var name = request.Name?.Trim();
        var currency = request.Currency?.Trim().ToUpperInvariant();
        var exchange = string.IsNullOrWhiteSpace(request.Exchange) ? null : request.Exchange.Trim();
        if (string.IsNullOrEmpty(symbol) || string.IsNullOrEmpty(name))
        {
            throw ApiException.BadRequest("invalid_instrument", "symbol and name are required.");
        }

        if (currency is null || currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper))
        {
            throw ApiException.BadRequest("invalid_currency", "currency must be a 3-letter ISO code.");
        }

        var exists = await db.Instruments.AnyAsync(i => i.Symbol == symbol && i.Exchange == exchange, ct);
        if (exists)
        {
            throw ApiException.Conflict("instrument_exists", "An instrument with this symbol and exchange already exists.");
        }

        var instrument = new Instrument
        {
            Id = Guid.NewGuid(),
            Symbol = symbol,
            Name = name,
            AssetClass = request.AssetClass,
            Exchange = exchange,
            Currency = currency,
            ProviderSymbolsJson = request.ProviderSymbols is { Count: > 0 }
                ? JsonSerializer.Serialize(request.ProviderSymbols)
                : null,
        };
        db.Instruments.Add(instrument);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/market/instruments/{instrument.Id}", ToDto(instrument));
    }

    private static async Task<IResult> GetInstrument(Guid id, EdgewiseDbContext db, CancellationToken ct)
    {
        var instrument = await db.Instruments.FindAsync([id], ct)
            ?? throw ApiException.NotFound("instrument_not_found", "Instrument does not exist.");
        return Results.Ok(ToDto(instrument));
    }

    // ------------------------------------------------------ bars + indicators

    private static async Task<IResult> GetBars(
        Guid id, string? timeframe, DateTime? from, DateTime? to,
        MarketDataService market, CancellationToken ct)
    {
        var tf = ParseTimeframe(timeframe);
        var (fromUtc, toUtc) = ResolveRange(tf, from, to);
        var result = await market.GetBarsAsync(id, tf, fromUtc, toUtc, InlineFetchBudget, ct);
        return Results.Ok(new BarsResponse(
            result.Bars.Select(b => new BarDto(b.Ts, b.O, b.H, b.L, b.C, b.V)).ToList(),
            result.Stale ? true : null));
    }

    private static async Task<IResult> GetIndicators(
        Guid id, string? timeframe, string? set, DateTime? from, DateTime? to,
        MarketDataService market, CancellationToken ct)
    {
        var tf = ParseTimeframe(timeframe);
        var (fromUtc, toUtc) = ResolveRange(tf, from, to);
        var result = await market.GetBarsAsync(id, tf, fromUtc, toUtc, InlineFetchBudget, ct);
        var bars = result.Bars.Select(b => new Bar(b.Ts, b.O, b.H, b.L, b.C, b.V)).ToList();

        var tokens = (string.IsNullOrWhiteSpace(set) ? "sma20,sma50,sma200,ema20,rsi14,macd,atr14,bb20,vwap" : set)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var series = new Dictionary<string, object>();
        foreach (var token in tokens.Select(t => t.ToLowerInvariant()).Distinct())
        {
            series[token] = ComputeIndicator(token, bars);
        }

        return Results.Ok(new IndicatorsResponse(tf, bars.Select(b => b.Ts).ToList(), series));
    }

    /// <summary>Computes one indicator series aligned 1:1 with the bars (warmup slots null).</summary>
    private static object ComputeIndicator(string token, IReadOnlyList<Bar> bars)
    {
        if (token == "macd")
        {
            var (macd, signal, hist) = IndicatorLibrary.Macd(bars);
            return new MacdSeriesDto(macd, signal, hist);
        }

        if (token == "vwap")
        {
            return IndicatorLibrary.Vwap(bars);
        }

        var (prefix, period) = SplitToken(token);
        return prefix switch
        {
            "sma" => IndicatorLibrary.Sma(bars, period),
            "ema" => IndicatorLibrary.Ema(bars, period),
            "rsi" => IndicatorLibrary.Rsi(bars, period),
            "atr" => IndicatorLibrary.Atr(bars, period),
            "bb" => ToBollingerDto(bars, period),
            _ => throw ApiException.BadRequest("invalid_indicator", $"Unknown indicator '{token}'."),
        };
    }

    private static BollingerSeriesDto ToBollingerDto(IReadOnlyList<Bar> bars, int period)
    {
        var (mid, upper, lower) = IndicatorLibrary.Bollinger(bars, period);
        return new BollingerSeriesDto(mid, upper, lower);
    }

    private static (string Prefix, int Period) SplitToken(string token)
    {
        var digitStart = 0;
        while (digitStart < token.Length && !char.IsAsciiDigit(token[digitStart]))
        {
            digitStart++;
        }

        if (digitStart == 0 || digitStart == token.Length
            || !int.TryParse(token[digitStart..], out var period) || period <= 0 || period > 1000)
        {
            throw ApiException.BadRequest("invalid_indicator", $"Unknown indicator '{token}'.");
        }

        return (token[..digitStart], period);
    }

    // ---------------------------------------------------------------- quotes

    private static async Task<IResult> GetQuotes(
        string? instrumentIds, EdgewiseDbContext db, MarketDataService market, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(instrumentIds))
        {
            throw ApiException.BadRequest("missing_instrument_ids", "Query parameter 'instrumentIds' is required.");
        }

        var ids = new List<Guid>();
        foreach (var part in instrumentIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Guid.TryParse(part, out var parsed))
            {
                throw ApiException.BadRequest("invalid_instrument_ids", $"'{part}' is not a valid instrument id.");
            }

            ids.Add(parsed);
        }

        if (ids.Count > MaxQuoteBatch)
        {
            throw ApiException.BadRequest("too_many_instruments", $"At most {MaxQuoteBatch} instruments per request.");
        }

        var known = await db.Instruments.Where(i => ids.Contains(i.Id)).Select(i => i.Id).ToListAsync(ct);
        var knownSet = known.ToHashSet();
        var quotes = new List<QuoteDto>(ids.Count);
        foreach (var instrumentId in ids.Distinct())
        {
            if (!knownSet.Contains(instrumentId))
            {
                continue;
            }

            var quote = await market.GetQuoteAsync(instrumentId, ct);
            quotes.Add(quote is null
                ? new QuoteDto(instrumentId, null, null, null, Stale: true, null)
                : new QuoteDto(instrumentId, quote.Price, quote.AsOf, quote.Provider, quote.Stale, quote.AgeMinutes));
        }

        return Results.Ok(new { quotes });
    }

    // ------------------------------------------------------------------- fx

    private static async Task<IResult> GetFxRate(
        string? @base, string? quote, DateOnly? date, MarketDataService market, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(@base) || string.IsNullOrWhiteSpace(quote))
        {
            throw ApiException.BadRequest("missing_currency", "Query parameters 'base' and 'quote' are required.");
        }

        var day = date ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var result = await market.GetFxRateAsync(@base, quote, day, ct)
            ?? throw ApiException.NotFound("fx_unavailable", $"No rate available for {@base}/{quote}.");
        return Results.Ok(new FxRateDto(
            @base.Trim().ToUpperInvariant(), quote.Trim().ToUpperInvariant(), result.Date, result.Rate, result.Provider));
    }

    // ------------------------------------------------------------- sentiment

    private static async Task<IResult> GetSentiment(EdgewiseDbContext db, CancellationToken ct)
    {
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-30);
        var readings = await db.SentimentReadings
            .Where(s => s.Kind == SentimentKind.CryptoFG && s.Date >= cutoff)
            .OrderBy(s => s.Date)
            .Select(s => new SentimentPointDto(s.Date, s.Value, s.Label))
            .ToListAsync(ct);
        return Results.Ok(new SentimentResponse(readings.Count > 0 ? readings[^1] : null, readings));
    }

    // ------------------------------------------------------------------ news

    private static async Task<IResult> GetNews(
        Guid? instrumentId, int? limit, EdgewiseDbContext db, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? 30, 1, 100);
        var recent = await db.NewsItems.AsNoTracking()
            .OrderByDescending(n => n.PublishedAt)
            .Take(300)
            .ToListAsync(ct);

        List<NewsItem> filtered;
        if (instrumentId is { } filter)
        {
            var needle = filter.ToString();
            filtered = recent
                .Where(n => n.InstrumentIdsJson is not null
                    && n.InstrumentIdsJson.Contains(needle, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        else
        {
            // Personal feed: items mapped to held/watched instruments plus unmapped general headlines.
            var relevant = (await GetUserInstrumentIdsAsync(db, ct)).Select(g => g.ToString()).ToList();
            filtered = recent
                .Where(n => n.InstrumentIdsJson is null
                    || relevant.Any(idText => n.InstrumentIdsJson.Contains(idText, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        var items = filtered.Take(take).Select(n => new NewsItemDto(
            n.Id, n.Title, n.Url, n.Source, n.PublishedAt, n.Summary, ParseInstrumentIds(n.InstrumentIdsJson)));
        return Results.Ok(new { items });
    }

    // -------------------------------------------------------------- calendar

    private static async Task<IResult> GetCalendar(
        DateTime? from, DateTime? to, EdgewiseDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var userId = RequireUserId(currentUser);
        var fromUtc = AsUtc(from ?? DateTime.UtcNow.AddDays(-7));
        var toUtc = AsUtc(to ?? DateTime.UtcNow.AddDays(30));
        var relevant = await GetUserInstrumentIdsAsync(db, ct);

        // Query filter already narrows to global rows + the user's own rows.
        var events = await db.CatalystEvents
            .Where(c => c.At >= fromUtc && c.At <= toUtc
                && (c.UserId == userId || c.InstrumentId == null || relevant.Contains(c.InstrumentId.Value)))
            .OrderBy(c => c.At)
            .ToListAsync(ct);
        return Results.Ok(new
        {
            events = events
                .Select(c => new CatalystEventDto(
                    c.Id, c.Kind, c.InstrumentId, c.Title, c.At, c.Severity, c.SourceRef, c.UserId == userId))
                .ToList(),
        });
    }

    private static async Task<IResult> CreateCalendarEvent(
        CreateCatalystEventRequest request, EdgewiseDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var userId = RequireUserId(currentUser);
        var title = request.Title?.Trim();
        if (string.IsNullOrEmpty(title))
        {
            throw ApiException.BadRequest("invalid_event", "title is required.");
        }

        if (request.At == default)
        {
            throw ApiException.BadRequest("invalid_event", "at is required.");
        }

        var entity = new CatalystEvent
        {
            Id = Guid.NewGuid(),
            Kind = request.Kind ?? CatalystKind.User,
            InstrumentId = request.InstrumentId,
            UserId = userId,
            Title = title,
            At = AsUtc(request.At),
            Severity = request.Severity ?? CatalystSeverity.Amber,
            SourceRef = null,
        };
        db.CatalystEvents.Add(entity);
        await db.SaveChangesAsync(ct);
        return Results.Created(
            $"/api/market/calendar/{entity.Id}",
            new CatalystEventDto(
                entity.Id, entity.Kind, entity.InstrumentId, entity.Title, entity.At,
                entity.Severity, entity.SourceRef, Own: true));
    }

    private static async Task<IResult> DeleteCalendarEvent(
        Guid id, EdgewiseDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var userId = RequireUserId(currentUser);
        var entity = await db.CatalystEvents.FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId, ct)
            ?? throw ApiException.NotFound("event_not_found", "Calendar event not found or not owned by you.");
        db.CatalystEvents.Remove(entity);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    // -------------------------------------------------------------- helpers

    private static InstrumentDto ToDto(Instrument instrument) => new(
        instrument.Id,
        instrument.Symbol,
        instrument.Name,
        instrument.AssetClass,
        instrument.Exchange,
        instrument.Currency,
        ProviderSymbols.Parse(instrument.ProviderSymbolsJson));

    private static Timeframe ParseTimeframe(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Timeframe.D1;
        }

        if (Enum.TryParse<Timeframe>(value.Trim(), ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        throw ApiException.BadRequest("invalid_timeframe", "timeframe must be one of h1, h4, d1, w1.");
    }

    private static (DateTime FromUtc, DateTime ToUtc) ResolveRange(Timeframe timeframe, DateTime? from, DateTime? to)
    {
        var toUtc = to is { } t ? AsUtc(t) : DateTime.UtcNow;
        var defaultSpan = timeframe switch
        {
            Timeframe.H1 => TimeSpan.FromDays(14),
            Timeframe.H4 => TimeSpan.FromDays(90),
            Timeframe.D1 => TimeSpan.FromDays(365),
            _ => TimeSpan.FromDays(1825),
        };
        var fromUtc = from is { } f ? AsUtc(f) : toUtc - defaultSpan;
        if (fromUtc >= toUtc)
        {
            throw ApiException.BadRequest("invalid_range", "'from' must be before 'to'.");
        }

        return (fromUtc, toUtc);
    }

    private static async Task<List<Guid>> GetUserInstrumentIdsAsync(EdgewiseDbContext db, CancellationToken ct)
    {
        // Global query filters scope both sets to the current user.
        var held = db.Holdings.Select(h => h.InstrumentId);
        var watched = db.WatchlistItems.Select(w => w.InstrumentId);
        return await held.Union(watched).ToListAsync(ct);
    }

    private static IReadOnlyList<Guid>? ParseInstrumentIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var texts = JsonSerializer.Deserialize<List<string>>(json);
            var ids = texts?
                .Select(t => Guid.TryParse(t, out var g) ? g : (Guid?)null)
                .Where(g => g.HasValue)
                .Select(g => g!.Value)
                .ToList();
            return ids is { Count: > 0 } ? ids : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Guid RequireUserId(ICurrentUser currentUser) =>
        currentUser.UserId ?? throw ApiException.Unauthorized("unauthorized", "Authentication required.");

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static string EscapeLike(string value) =>
        value.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
}
