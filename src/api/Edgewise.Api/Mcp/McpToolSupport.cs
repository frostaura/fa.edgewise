using System.Text.Json;
using System.Text.Json.Serialization;
using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Auth;
using Edgewise.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Edgewise.Api.Mcp;

/// <summary>
/// Shared plumbing for the MCP tool surface: one JSON contract (camelCase,
/// enums as strings), expected-failure translation into helpful error payloads
/// instead of exception dumps, and symbol/id resolution helpers.
/// </summary>
internal static class McpToolSupport
{
    /// <summary>Mirrors the REST API's JSON contract so agents see one shape everywhere.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// Runs a tool body and serializes its result. Expected failures
    /// (<see cref="ApiException"/>) become an {"error","message"} payload with the
    /// service's human-readable message — never a stack trace.
    /// </summary>
    public static async Task<string> RunAsync(Func<Task<object?>> action)
    {
        try
        {
            return JsonSerializer.Serialize(await action(), Json);
        }
        catch (ApiException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Code, message = ex.Message }, Json);
        }
    }

    /// <summary>Resolves a catalogue instrument by symbol (case-insensitive, exchange-agnostic).</summary>
    public static async Task<Instrument> ResolveInstrumentAsync(
        EdgewiseDbContext db, string? symbol, CancellationToken ct)
    {
        var normalized = symbol?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(normalized))
        {
            throw ApiException.BadRequest("missing_symbol", "An instrument symbol is required (e.g. BTC-USD).");
        }

        return await db.Instruments.AsNoTracking()
                .OrderBy(i => i.Exchange == null ? 0 : 1)
                .FirstOrDefaultAsync(i => i.Symbol == normalized, ct)
            ?? throw ApiException.NotFound(
                "instrument_not_found",
                $"No instrument with symbol '{normalized}' exists in the catalogue. " +
                "Use the exact catalogue symbol (e.g. BTC-USD, ETH-USD or STX500.JO), " +
                "or create the instrument first via the API.");
    }

    /// <summary>Parses a GUID tool argument, failing with a helpful message instead of a format exception.</summary>
    public static Guid ParseId(string? value, string paramName) =>
        Guid.TryParse(value?.Trim(), out var id)
            ? id
            : throw ApiException.BadRequest(
                $"invalid_{paramName}", $"'{value}' is not a valid {paramName}; pass the GUID exactly as returned by other tools.");

    /// <summary>Case-insensitive enum parse with an error message that lists the accepted values.</summary>
    public static TEnum ParseEnum<TEnum>(string? value, string paramName)
        where TEnum : struct, Enum
    {
        if (Enum.TryParse<TEnum>(value?.Trim(), ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        var accepted = string.Join(", ", Enum.GetNames<TEnum>().Select(n => char.ToLowerInvariant(n[0]) + n[1..]));
        throw ApiException.BadRequest(
            $"invalid_{paramName}", $"'{value}' is not a valid {paramName}. Accepted values: {accepted}.");
    }

    /// <summary>Parses an optional ISO-8601 timestamp, defaulting to now (UTC assumed when unzoned).</summary>
    public static DateTime ParseTimestamp(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DateTime.UtcNow;
        }

        if (DateTime.TryParse(
                value.Trim(),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            return parsed;
        }

        throw ApiException.BadRequest(
            $"invalid_{paramName}", $"'{value}' is not a valid ISO-8601 timestamp (e.g. 2026-07-18T09:30:00Z).");
    }
}
