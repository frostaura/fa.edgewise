using Edgewise.Domain.Engines.Analytics;

namespace Edgewise.Domain.Tests.Analytics;

internal static class AnalyticsTestData
{
    public static readonly DateTime T0 = new(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc);

    public static TradeStat Trade(
        DateTime closedAt,
        decimal? r = null,
        bool isWin = false,
        string setup = "S1",
        string instrument = "ES",
        string emotion = "calm",
        bool hadPlan = true,
        int? adherence = null,
        long? holdingSeconds = 600)
        => new()
        {
            ClosedAt = closedAt,
            RRealised = r,
            RPlanned = 2m,
            PnlMinor = r is null ? 0 : (long)(r.Value * 10_000m),
            SetupTag = setup,
            InstrumentKey = instrument,
            EmotionTag = emotion,
            HadPlan = hadPlan,
            AdherenceScore = adherence,
            HoldingSeconds = holdingSeconds,
            IsWin = isWin,
            DayOfWeek = closedAt.DayOfWeek,
            HourOfDay = closedAt.Hour,
        };
}
