using System.Reflection;
using Edgewise.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Edgewise.Infrastructure.Jobs.Portfolio;

/// <summary>
/// Best-effort quote and FX resolution for portfolio valuation.
///
/// The dedicated market-data feature (Edgewise.Infrastructure.MarketData.MarketDataService)
/// may or may not be present in this build; it is therefore resolved defensively via DI +
/// reflection. When it is missing or fails, we fall back to the shared QuoteCache / FxRate
/// tables, and callers fall back further to lot cost when even those are empty.
/// </summary>
public sealed class PortfolioMarketData(
    EdgewiseDbContext db,
    IServiceProvider serviceProvider,
    ILogger<PortfolioMarketData> logger)
{
    private static readonly Type? ExternalServiceType = FindExternalServiceType();

    private static Type? FindExternalServiceType()
    {
        try
        {
            return typeof(PortfolioMarketData).Assembly
                .GetType("Edgewise.Infrastructure.MarketData.MarketDataService", throwOnError: false);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Latest known quote for an instrument (instrument currency, major units), or null.</summary>
    public async Task<QuoteInput?> GetQuoteAsync(Guid instrumentId, string instrumentCurrency, CancellationToken ct)
    {
        var external = await TryExternalAsync("GetQuote", [instrumentId], ct);
        if (external is not null)
        {
            var price = ReadProperty<decimal?>(external, "Price");
            var asOf = ReadProperty<DateTime?>(external, "AsOf");
            if (price.HasValue)
            {
                return new QuoteInput(
                    price.Value,
                    asOf ?? DateTime.UtcNow,
                    instrumentCurrency,
                    ReadProperty<bool?>(external, "Stale") ?? false);
            }
        }

        var cached = await db.QuoteCaches.AsNoTracking()
            .FirstOrDefaultAsync(q => q.InstrumentId == instrumentId, ct);
        return cached is null
            ? null
            : new QuoteInput(cached.Price, cached.AsOf, instrumentCurrency, Stale: false);
    }

    /// <summary>FX rate converting one unit of <paramref name="from"/> into <paramref name="to"/>, or null.</summary>
    public async Task<decimal?> GetFxRateAsync(string from, string to, DateOnly date, CancellationToken ct)
    {
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
        {
            return 1m;
        }

        var external = await TryExternalAsync("GetFxRate", [from, to, date], ct);
        if (external is not null)
        {
            var rate = external as decimal? ?? ReadProperty<decimal?>(external, "Rate");
            if (rate is > 0m)
            {
                return rate;
            }
        }

        var direct = await db.FxRates.AsNoTracking()
            .Where(f => f.Base == from && f.Quote == to && f.Date <= date)
            .OrderByDescending(f => f.Date)
            .FirstOrDefaultAsync(ct);
        if (direct is not null && direct.Rate > 0m)
        {
            return direct.Rate;
        }

        var inverse = await db.FxRates.AsNoTracking()
            .Where(f => f.Base == to && f.Quote == from && f.Date <= date)
            .OrderByDescending(f => f.Date)
            .FirstOrDefaultAsync(ct);
        return inverse is not null && inverse.Rate > 0m ? 1m / inverse.Rate : null;
    }

    /// <summary>
    /// Invokes a method on the optional MarketDataService by name, tolerating sync/async
    /// signatures and an optional trailing CancellationToken. Returns null on any failure.
    /// </summary>
    private async Task<object?> TryExternalAsync(string methodName, object[] args, CancellationToken ct)
    {
        if (ExternalServiceType is null)
        {
            return null;
        }

        try
        {
            var service = serviceProvider.GetService(ExternalServiceType);
            if (service is null)
            {
                return null;
            }

            var method = ExternalServiceType
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m =>
                    (m.Name == methodName || m.Name == methodName + "Async")
                    && m.GetParameters().Length >= args.Length
                    && m.GetParameters().Length <= args.Length + 1);
            if (method is null)
            {
                return null;
            }

            var callArgs = method.GetParameters().Length == args.Length + 1
                ? [.. args, ct]
                : args;

            var result = method.Invoke(service, callArgs);
            if (result is Task task)
            {
                await task.ConfigureAwait(false);
                result = task.GetType().GetProperty("Result")?.GetValue(task);
            }

            return result;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "MarketDataService.{Method} unavailable; using cached market data.", methodName);
            return null;
        }
    }

    private static T? ReadProperty<T>(object source, string name)
    {
        try
        {
            var value = source.GetType().GetProperty(name)?.GetValue(source);
            return value is T typed ? typed : default;
        }
        catch
        {
            return default;
        }
    }
}
