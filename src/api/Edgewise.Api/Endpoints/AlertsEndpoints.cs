using System.Text.Json;
using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Auth;
using Edgewise.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Edgewise.Api.Endpoints;

/// <summary>
/// Alert CRUD. Kinds + params:
///   priceCross  { level, direction: "above"|"below" }   (instrumentId required)
///   pctMove     { pct, windowMinutes }                   (instrumentId required)
///   zoneTouch   { zoneId }                               (instrument inferred from zone)
///   fundingRate { thresholdPct? }                        (instrumentId required; evaluated when a funding source exists)
///   fgExtreme   { min, max }                             (global sentiment gauge)
///   catalystT24 { }                                      (red catalysts in next 24h)
/// Cooldown default 60 minutes. Evaluation runs in AlertEvaluationJob (Hangfire, every minute).
/// </summary>
public sealed class AlertsEndpoints : IEndpointModule
{
    public const int DefaultCooldownMinutes = 60;

    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/alerts").RequireAuthorization();

        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync);
        group.MapPatch("/{id:guid}", UpdateAsync);
        group.MapDelete("/{id:guid}", DeleteAsync);
    }

    private static async Task<IResult> ListAsync(EdgewiseDbContext db, CancellationToken ct)
    {
        var alerts = await db.Alerts.OrderByDescending(a => a.Enabled).ThenBy(a => a.Kind).ToListAsync(ct);
        var instrumentIds = alerts.Where(a => a.InstrumentId != null).Select(a => a.InstrumentId!.Value).Distinct().ToList();
        var symbols = instrumentIds.Count == 0
            ? []
            : await db.Instruments.Where(i => instrumentIds.Contains(i.Id))
                .ToDictionaryAsync(i => i.Id, i => i.Symbol, ct);

        return Results.Ok(alerts.Select(a => ToDto(a, symbols)).ToList());
    }

    private static async Task<IResult> CreateAsync(
        SaveAlertRequest request, EdgewiseDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var kind = ParseKind(request.Kind);
        var (instrumentId, paramsJson) = await ValidateAsync(db, kind, request.InstrumentId, request.Params, ct);

        var alert = new Alert
        {
            Id = Guid.NewGuid(),
            UserId = currentUser.UserId
                ?? throw ApiException.Unauthorized("unauthorized", "Authentication required."),
            Kind = kind,
            InstrumentId = instrumentId,
            ParamsJson = paramsJson,
            Enabled = request.Enabled ?? true,
            CooldownMinutes = NormaliseCooldown(request.CooldownMinutes),
        };
        db.Alerts.Add(alert);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/alerts/{alert.Id}", ToDto(alert, await SymbolsAsync(db, alert, ct)));
    }

    private static async Task<IResult> UpdateAsync(
        Guid id, SaveAlertRequest request, EdgewiseDbContext db, CancellationToken ct)
    {
        var alert = await db.Alerts.SingleOrDefaultAsync(a => a.Id == id, ct)
            ?? throw ApiException.NotFound("alert_not_found", "Alert not found.");

        if (request.Kind is not null && ParseKind(request.Kind) != alert.Kind)
        {
            throw ApiException.BadRequest("kind_immutable", "An alert's kind cannot change; create a new alert.");
        }

        if (request.Params is not null || request.InstrumentId is not null)
        {
            var (instrumentId, paramsJson) = await ValidateAsync(
                db, alert.Kind,
                request.InstrumentId ?? alert.InstrumentId,
                request.Params ?? ParseStored(alert.ParamsJson),
                ct);
            alert.InstrumentId = instrumentId;
            alert.ParamsJson = paramsJson;
        }

        if (request.Enabled is not null)
        {
            alert.Enabled = request.Enabled.Value;
        }

        if (request.CooldownMinutes is not null)
        {
            alert.CooldownMinutes = NormaliseCooldown(request.CooldownMinutes);
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(alert, await SymbolsAsync(db, alert, ct)));
    }

    private static async Task<IResult> DeleteAsync(Guid id, EdgewiseDbContext db, CancellationToken ct)
    {
        var alert = await db.Alerts.SingleOrDefaultAsync(a => a.Id == id, ct)
            ?? throw ApiException.NotFound("alert_not_found", "Alert not found.");
        db.Alerts.Remove(alert);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    // ----------------------------------------------------------- validation

    private static AlertKind ParseKind(string? kind)
    {
        if (!Enum.TryParse<AlertKind>(kind, ignoreCase: true, out var parsed))
        {
            throw ApiException.BadRequest(
                "invalid_kind",
                "kind must be one of: priceCross, pctMove, zoneTouch, fundingRate, fgExtreme, catalystT24.");
        }

        return parsed;
    }

    /// <summary>Validates kind-specific params and returns (instrumentId, canonical paramsJson).</summary>
    private static async Task<(Guid? InstrumentId, string ParamsJson)> ValidateAsync(
        EdgewiseDbContext db, AlertKind kind, Guid? instrumentId, JsonElement? @params, CancellationToken ct)
    {
        var p = @params is { ValueKind: JsonValueKind.Object } value ? value : default;

        switch (kind)
        {
            case AlertKind.PriceCross:
                {
                    await RequireInstrumentAsync(db, instrumentId, ct);
                    var level = GetDecimal(p, "level")
                        ?? throw ApiException.BadRequest("invalid_params", "priceCross requires params.level > 0.");
                    if (level <= 0)
                    {
                        throw ApiException.BadRequest("invalid_params", "priceCross requires params.level > 0.");
                    }

                    var direction = GetString(p, "direction")?.ToLowerInvariant();
                    if (direction is not ("above" or "below"))
                    {
                        throw ApiException.BadRequest("invalid_params", "priceCross direction must be \"above\" or \"below\".");
                    }

                    return (instrumentId, Serialize(new { level, direction }));
                }

            case AlertKind.PctMove:
                {
                    await RequireInstrumentAsync(db, instrumentId, ct);
                    var pct = GetDecimal(p, "pct") ?? 0;
                    if (pct <= 0)
                    {
                        throw ApiException.BadRequest("invalid_params", "pctMove requires params.pct > 0.");
                    }

                    var window = (int)(GetDecimal(p, "windowMinutes") ?? 60);
                    if (window <= 0)
                    {
                        throw ApiException.BadRequest("invalid_params", "pctMove windowMinutes must be > 0.");
                    }

                    return (instrumentId, Serialize(new { pct, windowMinutes = window }));
                }

            case AlertKind.ZoneTouch:
                {
                    var zoneIdText = GetString(p, "zoneId");
                    if (!Guid.TryParse(zoneIdText, out var zoneId))
                    {
                        throw ApiException.BadRequest("invalid_params", "zoneTouch requires params.zoneId (a zone you own).");
                    }

                    var zone = await db.Zones.SingleOrDefaultAsync(z => z.Id == zoneId && !z.Archived, ct)
                        ?? throw ApiException.BadRequest("zone_not_found", "Zone not found (or archived).");
                    return (zone.InstrumentId, Serialize(new { zoneId }));
                }

            case AlertKind.FundingRate:
                {
                    await RequireInstrumentAsync(db, instrumentId, ct);
                    var threshold = GetDecimal(p, "thresholdPct") ?? 0.05m;
                    return (instrumentId, Serialize(new { thresholdPct = threshold }));
                }

            case AlertKind.FgExtreme:
                {
                    var min = (int)(GetDecimal(p, "min") ?? 20);
                    var max = (int)(GetDecimal(p, "max") ?? 80);
                    if (min < 0 || max > 100 || min >= max)
                    {
                        throw ApiException.BadRequest("invalid_params", "fgExtreme requires 0 <= min < max <= 100.");
                    }

                    return (null, Serialize(new { min, max }));
                }

            case AlertKind.CatalystT24:
                return (instrumentId, "{}");

            default:
                throw ApiException.BadRequest("invalid_kind", "Unsupported alert kind.");
        }
    }

    private static async Task RequireInstrumentAsync(EdgewiseDbContext db, Guid? instrumentId, CancellationToken ct)
    {
        if (instrumentId is null || instrumentId == Guid.Empty
            || !await db.Instruments.AnyAsync(i => i.Id == instrumentId, ct))
        {
            throw ApiException.BadRequest("instrument_required", "A valid instrumentId is required for this alert kind.");
        }
    }

    // --------------------------------------------------------------- helpers

    private static decimal? GetDecimal(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number
            ? el.GetDecimal()
            : null;

    private static string? GetString(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static string Serialize(object value) =>
        JsonSerializer.Serialize(value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    private static JsonElement? ParseStored(string? paramsJson)
    {
        if (string.IsNullOrWhiteSpace(paramsJson))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(paramsJson);
        return doc.RootElement.Clone();
    }

    private static int NormaliseCooldown(int? cooldownMinutes) =>
        cooldownMinutes is > 0 ? Math.Min(cooldownMinutes.Value, 7 * 24 * 60) : DefaultCooldownMinutes;

    private static async Task<Dictionary<Guid, string>> SymbolsAsync(
        EdgewiseDbContext db, Alert alert, CancellationToken ct) =>
        alert.InstrumentId is { } id
            ? await db.Instruments.Where(i => i.Id == id).ToDictionaryAsync(i => i.Id, i => i.Symbol, ct)
            : [];

    private static AlertDto ToDto(Alert alert, Dictionary<Guid, string> symbols) =>
        new(
            alert.Id,
            char.ToLowerInvariant(alert.Kind.ToString()[0]) + alert.Kind.ToString()[1..],
            alert.InstrumentId,
            alert.InstrumentId is { } id && symbols.TryGetValue(id, out var symbol) ? symbol : null,
            ParseStored(alert.ParamsJson),
            alert.Enabled,
            alert.LastTriggeredAt,
            alert.CooldownMinutes);

    // ------------------------------------------------------------------ DTOs

    public sealed record SaveAlertRequest(
        string? Kind,
        Guid? InstrumentId,
        JsonElement? Params,
        bool? Enabled,
        int? CooldownMinutes);

    public sealed record AlertDto(
        Guid Id,
        string Kind,
        Guid? InstrumentId,
        string? Symbol,
        JsonElement? Params,
        bool Enabled,
        DateTime? LastTriggeredAt,
        int CooldownMinutes);
}
