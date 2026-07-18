namespace Edgewise.Domain.Entities;

public class Watchlist : IUserOwned
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class WatchlistItem
{
    public Guid Id { get; set; }
    public Guid WatchlistId { get; set; }
    public Watchlist Watchlist { get; set; } = null!;
    public Guid InstrumentId { get; set; }
    public string? Note { get; set; }
    public string? WhyWatching { get; set; }
    public string? AlertLevelsJson { get; set; }
}

public class Alert : IUserOwned
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public AlertKind Kind { get; set; }
    public Guid? InstrumentId { get; set; }
    public string? ParamsJson { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTime? LastTriggeredAt { get; set; }
    public int CooldownMinutes { get; set; }
}

public class Notification : IUserOwned
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid? AlertId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string? DeepLink { get; set; }
    public string? Channels { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ReadAt { get; set; }
}

public class PushSubscription : IUserOwned
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string EndpointJson { get; set; } = "{}";
}

public class Zone : IUserOwned
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid InstrumentId { get; set; }
    public decimal PriceLow { get; set; }
    public decimal PriceHigh { get; set; }
    public int Strength { get; set; }
    public Timeframe SourceTimeframe { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool Archived { get; set; }
}
