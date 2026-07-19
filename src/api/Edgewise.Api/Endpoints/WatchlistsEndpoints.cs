using System.Text.Json;
using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Auth;
using Edgewise.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Edgewise.Api.Endpoints;

/// <summary>
/// Watchlists CRUD + items. Quotes are aggregated defensively from QuoteCache
/// (stale-flagged); promote-to-plan returns a prefill payload the frontend
/// routes to /journal/plans/new.
/// </summary>
public sealed class WatchlistsEndpoints : IEndpointModule
{
    /// <summary>A quote older than this is flagged stale.</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);

    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/watchlists").RequireAuthorization();

        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync);
        group.MapPatch("/{id:guid}", RenameAsync);
        group.MapDelete("/{id:guid}", DeleteAsync);

        group.MapPost("/{id:guid}/items", AddItemAsync);
        group.MapPatch("/items/{itemId:guid}", UpdateItemAsync);
        group.MapDelete("/items/{itemId:guid}", DeleteItemAsync);
        group.MapPost("/items/{itemId:guid}/promote-to-plan", PromoteAsync);
    }

    private static async Task<IResult> ListAsync(EdgewiseDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var lists = await db.Watchlists.OrderBy(w => w.Name).ToListAsync(ct);
        var items = await db.WatchlistItems.ToListAsync(ct);

        var instrumentIds = items.Select(i => i.InstrumentId).Distinct().ToList();
        var instruments = instrumentIds.Count == 0
            ? []
            : await db.Instruments.Where(i => instrumentIds.Contains(i.Id))
                .ToDictionaryAsync(i => i.Id, ct);
        var quotes = instrumentIds.Count == 0
            ? []
            : await db.QuoteCaches.Where(q => instrumentIds.Contains(q.InstrumentId))
                .ToDictionaryAsync(q => q.InstrumentId, ct);
        var alertCounts = (await db.Alerts
                .Where(a => a.InstrumentId != null && instrumentIds.Contains(a.InstrumentId.Value))
                .GroupBy(a => a.InstrumentId!.Value)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToListAsync(ct))
            .ToDictionary(x => x.Key, x => x.Count);

        var now = DateTime.UtcNow;
        var dtos = lists.Select(list => new WatchlistDto(
            list.Id,
            list.Name,
            items.Where(i => i.WatchlistId == list.Id)
                .Select(i => ToItemDto(i, instruments, quotes, alertCounts, now))
                .ToList()))
            .ToList();

        return Results.Ok(dtos);
    }

    private static async Task<IResult> CreateAsync(
        SaveWatchlistRequest request, EdgewiseDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            throw ApiException.BadRequest("name_required", "A watchlist name is required.");
        }

        var list = new Watchlist
        {
            Id = Guid.NewGuid(),
            UserId = RequireUserId(currentUser),
            Name = name,
        };
        db.Watchlists.Add(list);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/watchlists/{list.Id}", new WatchlistDto(list.Id, list.Name, []));
    }

    private static async Task<IResult> RenameAsync(
        Guid id, SaveWatchlistRequest request, EdgewiseDbContext db, CancellationToken ct)
    {
        var list = await db.Watchlists.SingleOrDefaultAsync(w => w.Id == id, ct)
            ?? throw ApiException.NotFound("watchlist_not_found", "Watchlist not found.");

        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            throw ApiException.BadRequest("name_required", "A watchlist name is required.");
        }

        list.Name = name;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new WatchlistDto(list.Id, list.Name, []));
    }

    private static async Task<IResult> DeleteAsync(Guid id, EdgewiseDbContext db, CancellationToken ct)
    {
        var list = await db.Watchlists.SingleOrDefaultAsync(w => w.Id == id, ct)
            ?? throw ApiException.NotFound("watchlist_not_found", "Watchlist not found.");

        var items = await db.WatchlistItems.Where(i => i.WatchlistId == id).ToListAsync(ct);
        db.WatchlistItems.RemoveRange(items);
        db.Watchlists.Remove(list);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> AddItemAsync(
        Guid id, SaveWatchlistItemRequest request, EdgewiseDbContext db, CancellationToken ct)
    {
        var list = await db.Watchlists.SingleOrDefaultAsync(w => w.Id == id, ct)
            ?? throw ApiException.NotFound("watchlist_not_found", "Watchlist not found.");

        if (request.InstrumentId == Guid.Empty)
        {
            throw ApiException.BadRequest("instrument_required", "instrumentId is required.");
        }

        var instrumentExists = await db.Instruments.AnyAsync(i => i.Id == request.InstrumentId, ct);
        if (!instrumentExists)
        {
            throw ApiException.BadRequest("instrument_not_found", "Unknown instrument.");
        }

        ValidateJson(request.AlertLevelsJson, "alertLevelsJson");

        var item = new WatchlistItem
        {
            Id = Guid.NewGuid(),
            WatchlistId = list.Id,
            InstrumentId = request.InstrumentId,
            Note = NullIfEmpty(request.Note),
            WhyWatching = NullIfEmpty(request.WhyWatching),
            AlertLevelsJson = NullIfEmpty(request.AlertLevelsJson),
        };
        db.WatchlistItems.Add(item);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/watchlists/{list.Id}/items/{item.Id}", await ItemDtoAsync(db, item, ct));
    }

    private static async Task<IResult> UpdateItemAsync(
        Guid itemId, SaveWatchlistItemRequest request, EdgewiseDbContext db, CancellationToken ct)
    {
        var item = await db.WatchlistItems.SingleOrDefaultAsync(i => i.Id == itemId, ct)
            ?? throw ApiException.NotFound("item_not_found", "Watchlist item not found.");

        if (request.Note is not null)
        {
            item.Note = NullIfEmpty(request.Note);
        }

        if (request.WhyWatching is not null)
        {
            item.WhyWatching = NullIfEmpty(request.WhyWatching);
        }

        if (request.AlertLevelsJson is not null)
        {
            ValidateJson(request.AlertLevelsJson, "alertLevelsJson");
            item.AlertLevelsJson = NullIfEmpty(request.AlertLevelsJson);
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(await ItemDtoAsync(db, item, ct));
    }

    private static async Task<IResult> DeleteItemAsync(Guid itemId, EdgewiseDbContext db, CancellationToken ct)
    {
        var item = await db.WatchlistItems.SingleOrDefaultAsync(i => i.Id == itemId, ct)
            ?? throw ApiException.NotFound("item_not_found", "Watchlist item not found.");

        db.WatchlistItems.Remove(item);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>Returns the prefill payload for /journal/plans/new (does not create a plan).</summary>
    private static async Task<IResult> PromoteAsync(Guid itemId, EdgewiseDbContext db, CancellationToken ct)
    {
        var item = await db.WatchlistItems.SingleOrDefaultAsync(i => i.Id == itemId, ct)
            ?? throw ApiException.NotFound("item_not_found", "Watchlist item not found.");

        var instrument = await db.Instruments.SingleOrDefaultAsync(i => i.Id == item.InstrumentId, ct);
        var note = string.Join(
            "\n\n",
            new[] { item.WhyWatching, item.Note }.Where(s => !string.IsNullOrWhiteSpace(s)));

        return Results.Ok(new PromoteToPlanDto(
            item.InstrumentId,
            instrument?.Symbol,
            NullIfEmpty(note)));
    }

    // --------------------------------------------------------------- helpers

    private static async Task<WatchlistItemDto> ItemDtoAsync(
        EdgewiseDbContext db, WatchlistItem item, CancellationToken ct)
    {
        var instrument = await db.Instruments.Where(i => i.Id == item.InstrumentId)
            .ToDictionaryAsync(i => i.Id, ct);
        var quote = await db.QuoteCaches.Where(q => q.InstrumentId == item.InstrumentId)
            .ToDictionaryAsync(q => q.InstrumentId, ct);
        var alertCount = await db.Alerts.CountAsync(a => a.InstrumentId == item.InstrumentId, ct);
        return ToItemDto(
            item, instrument, quote,
            new Dictionary<Guid, int> { [item.InstrumentId] = alertCount },
            DateTime.UtcNow);
    }

    private static WatchlistItemDto ToItemDto(
        WatchlistItem item,
        Dictionary<Guid, Instrument> instruments,
        Dictionary<Guid, QuoteCache> quotes,
        Dictionary<Guid, int> alertCounts,
        DateTime nowUtc)
    {
        instruments.TryGetValue(item.InstrumentId, out var instrument);
        quotes.TryGetValue(item.InstrumentId, out var quote);
        return new WatchlistItemDto(
            item.Id,
            item.WatchlistId,
            item.InstrumentId,
            instrument?.Symbol,
            instrument?.Name,
            item.Note,
            item.WhyWatching,
            item.AlertLevelsJson,
            quote is null
                ? null
                : new QuoteDto(quote.Price, quote.AsOf, quote.Provider, nowUtc - quote.AsOf > StaleAfter),
            alertCounts.GetValueOrDefault(item.InstrumentId));
    }

    private static void ValidateJson(string? json, string field)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            using var _ = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw ApiException.BadRequest("invalid_json", $"{field} must be valid JSON.");
        }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Guid RequireUserId(ICurrentUser currentUser) =>
        currentUser.UserId ?? throw ApiException.Unauthorized("unauthorized", "Authentication required.");

    // ------------------------------------------------------------------ DTOs

    public sealed record SaveWatchlistRequest(string? Name);

    public sealed record SaveWatchlistItemRequest(
        Guid InstrumentId, string? Note, string? WhyWatching, string? AlertLevelsJson);

    public sealed record WatchlistDto(Guid Id, string Name, IReadOnlyList<WatchlistItemDto> Items);

    public sealed record WatchlistItemDto(
        Guid Id,
        Guid WatchlistId,
        Guid InstrumentId,
        string? Symbol,
        string? InstrumentName,
        string? Note,
        string? WhyWatching,
        string? AlertLevelsJson,
        QuoteDto? Quote,
        int AlertCount);

    public sealed record QuoteDto(decimal Price, DateTime AsOf, string Provider, bool Stale);

    public sealed record PromoteToPlanDto(Guid InstrumentId, string? Symbol, string? Note);
}
