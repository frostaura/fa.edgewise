namespace Edgewise.Domain.Engines.Indicators;

/// <summary>
/// A single OHLCV bar. <paramref name="Ts"/> is the bar's opening timestamp.
/// Prices are decimal (minor-unit agnostic — the engines never mix price units with
/// account currency except through explicit position sizing).
/// </summary>
public readonly record struct Bar(DateTime Ts, decimal O, decimal H, decimal L, decimal C, decimal V);
