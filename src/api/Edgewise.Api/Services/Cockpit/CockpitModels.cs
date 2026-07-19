namespace Edgewise.Api.Services.Cockpit;

/// <summary>Traffic-light status for a cockpit read. Serialized camelCase ("green"|"amber"|"red").</summary>
public enum ReadStatus
{
    Green,
    Amber,
    Red,
}

/// <summary>
/// Cockpit status contract (GET /api/cockpit/status).
///
/// Locking policy (documented per PRD):
///  - Only a RED read blocks planning. Reds can occur on: heat (over cap),
///    dailyPnl (circuit breaker tripped: daily stop or loss-count reached) and
///    ladder (Paused rung, >= 15% drawdown). Calendar and state never go red.
///  - <see cref="NewPlanUnlocked"/> = no red reads, OR an active CockpitRed
///    override logged today.
///  - <see cref="AllGreen"/> is informational: all five reads green.
/// </summary>
public sealed record CockpitStatusDto(
    CockpitReadsDto Reads,
    bool AllGreen,
    bool NewPlanUnlocked,
    OverrideDto? ActiveOverride,
    TodayStripDto Today);

public sealed record CockpitReadsDto(
    HeatReadDto Heat,
    DailyPnlReadDto DailyPnl,
    LadderReadDto Ladder,
    CalendarReadDto Calendar,
    SelfStateReadDto State);

/// <summary>Open risk across open trades vs the active risk profile's heat cap.</summary>
public sealed record HeatReadDto(
    decimal OpenRiskMajor,
    decimal? HeatPct,
    decimal CapPct,
    bool Estimated,
    int OpenTradesCount,
    ReadStatus Status,
    string Detail);

/// <summary>Today's realised P&amp;L / loss count vs the daily stop. Red = circuit breaker tripped.</summary>
public sealed record DailyPnlReadDto(
    long PnlMinor,
    decimal RealisedR,
    int LossCount,
    int LossCountStop,
    long StopMinor,
    decimal ProgressPct,
    bool Tripped,
    ReadStatus Status,
    string Detail);

/// <summary>Drawdown ladder from the Trading bucket high-water mark (rungs at 5/10/15%).</summary>
public sealed record LadderReadDto(
    decimal DrawdownPct,
    string Rung,
    decimal? NextRungPct,
    bool Locked,
    ReadStatus Status,
    string Detail);

/// <summary>Next-24h catalysts touching held/watched instruments. Amber when any red catalyst; never red.</summary>
public sealed record CalendarReadDto(
    IReadOnlyList<CatalystItemDto> Events,
    ReadStatus Status,
    string Detail);

/// <summary>Last self-declared state today (calm|tired|tilted|rushed). Non-calm = amber; never red.</summary>
public sealed record SelfStateReadDto(
    string? State,
    DateTime? DeclaredAt,
    ReadStatus Status,
    string Detail);

public sealed record CatalystItemDto(
    Guid Id,
    string Kind,
    string Severity,
    string Title,
    DateTime At,
    Guid? InstrumentId,
    string? Symbol,
    bool Held,
    bool Watched);

public sealed record OverrideDto(Guid Id, string Kind, string Reason, DateTime At);

public sealed record TodayStripDto(
    decimal RealisedR,
    long PnlMinor,
    int TradesCount,
    decimal DailyStopProgressPct);
