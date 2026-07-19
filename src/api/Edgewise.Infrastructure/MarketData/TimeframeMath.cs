using Edgewise.Domain.Entities;

namespace Edgewise.Infrastructure.MarketData;

/// <summary>Bar-boundary arithmetic shared by the service and the providers. All times UTC.</summary>
public static class TimeframeMath
{
    public static TimeSpan Duration(Timeframe timeframe) => timeframe switch
    {
        Timeframe.H1 => TimeSpan.FromHours(1),
        Timeframe.H4 => TimeSpan.FromHours(4),
        Timeframe.D1 => TimeSpan.FromDays(1),
        Timeframe.W1 => TimeSpan.FromDays(7),
        _ => throw new ArgumentOutOfRangeException(nameof(timeframe), timeframe, "Unknown timeframe."),
    };

    /// <summary>Floors a UTC instant to the open of the bar containing it (weeks anchor on Monday 00:00 UTC).</summary>
    public static DateTime FloorToBarOpen(DateTime utc, Timeframe timeframe)
    {
        utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return timeframe switch
        {
            Timeframe.H1 => new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc),
            Timeframe.H4 => new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour - (utc.Hour % 4), 0, 0, DateTimeKind.Utc),
            Timeframe.D1 => utc.Date,
            Timeframe.W1 => utc.Date.AddDays(-(((int)utc.DayOfWeek + 6) % 7)),
            _ => throw new ArgumentOutOfRangeException(nameof(timeframe), timeframe, "Unknown timeframe."),
        };
    }

    /// <summary>Open time of the most recent fully closed bar as of <paramref name="nowUtc"/>.</summary>
    public static DateTime LastClosedBarOpen(DateTime nowUtc, Timeframe timeframe) =>
        FloorToBarOpen(nowUtc, timeframe) - Duration(timeframe);

    /// <summary>True when the bar opening at <paramref name="barOpenUtc"/> has fully closed by <paramref name="nowUtc"/>.</summary>
    public static bool IsClosed(DateTime barOpenUtc, Timeframe timeframe, DateTime nowUtc) =>
        barOpenUtc + Duration(timeframe) <= nowUtc;

    /// <summary>
    /// Aggregates finer bars into <paramref name="target"/> bars aligned on the target grid.
    /// Input must be sorted ascending. Used by providers that lack a native H4/W1 interval.
    /// </summary>
    public static List<ProviderBar> Aggregate(IReadOnlyList<ProviderBar> bars, Timeframe target)
    {
        var result = new List<ProviderBar>();
        foreach (var bar in bars)
        {
            var open = FloorToBarOpen(bar.Ts, target);
            if (result.Count > 0 && result[^1].Ts == open)
            {
                var prev = result[^1];
                result[^1] = prev with
                {
                    H = Math.Max(prev.H, bar.H),
                    L = Math.Min(prev.L, bar.L),
                    C = bar.C,
                    V = prev.V + bar.V,
                };
            }
            else
            {
                result.Add(bar with { Ts = open });
            }
        }

        return result;
    }
}
