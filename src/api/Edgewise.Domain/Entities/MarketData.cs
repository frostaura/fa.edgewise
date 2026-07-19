namespace Edgewise.Domain.Entities;

/// <summary>Global OHLCV bar; composite PK (InstrumentId, Timeframe, Ts).</summary>
public class PriceBar
{
    public Guid InstrumentId { get; set; }
    public Timeframe Timeframe { get; set; }
    public DateTime Ts { get; set; }
    public decimal O { get; set; }
    public decimal H { get; set; }
    public decimal L { get; set; }
    public decimal C { get; set; }
    public decimal V { get; set; }
    public string Provider { get; set; } = string.Empty;
}

/// <summary>Global latest-quote cache; PK is InstrumentId.</summary>
public class QuoteCache
{
    public Guid InstrumentId { get; set; }
    public decimal Price { get; set; }
    public DateTime AsOf { get; set; }
    public string Provider { get; set; } = string.Empty;
}

/// <summary>Global FX rate; composite PK (Base, Quote, Date).</summary>
public class FxRate
{
    public string Base { get; set; } = string.Empty;
    public string Quote { get; set; } = string.Empty;
    public DateOnly Date { get; set; }
    public decimal Rate { get; set; }
    public string Provider { get; set; } = string.Empty;
}

public class NewsItem
{
    public Guid Id { get; set; }
    public string UrlHash { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public DateTime PublishedAt { get; set; }
    public string? Summary { get; set; }
    public string? InstrumentIdsJson { get; set; }
}

/// <summary>Global when <see cref="UserId"/> is null; user-created otherwise.</summary>
public class CatalystEvent
{
    public Guid Id { get; set; }
    public CatalystKind Kind { get; set; }
    public Guid? InstrumentId { get; set; }
    public Guid? UserId { get; set; }
    public string Title { get; set; } = string.Empty;
    public DateTime At { get; set; }
    public CatalystSeverity Severity { get; set; }
    public string? SourceRef { get; set; }
}

/// <summary>Global sentiment gauge; composite PK (Kind, Date).</summary>
public class SentimentReading
{
    public SentimentKind Kind { get; set; }
    public DateOnly Date { get; set; }
    public int Value { get; set; }
    public string Label { get; set; } = string.Empty;
}

/// <summary>Global provider status; PK is Provider.</summary>
public class ProviderHealth
{
    public string Provider { get; set; } = string.Empty;
    public DateTime? LastSuccessAt { get; set; }
    public DateTime? LastErrorAt { get; set; }
    public string? ErrorNote { get; set; }
}
