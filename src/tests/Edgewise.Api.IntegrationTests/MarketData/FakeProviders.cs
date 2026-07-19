using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.MarketData;

namespace Edgewise.Api.IntegrationTests.MarketData;

/// <summary>Scriptable bar provider. A null handler simulates a hard provider failure.</summary>
public sealed class FakeBarProvider(string name) : IBarProvider
{
    private int _calls;

    public string Name { get; } = name;

    public int Calls => Volatile.Read(ref _calls);

    public Func<string, Timeframe, DateTime, DateTime, IReadOnlyList<ProviderBar>>? Handler { get; set; }

    public Task<IReadOnlyList<ProviderBar>> GetBarsAsync(
        string providerSymbol, Timeframe timeframe, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        return Handler is null
            ? throw new InvalidOperationException($"Fake bar provider '{Name}' set to fail.")
            : Task.FromResult(Handler(providerSymbol, timeframe, fromUtc, toUtc));
    }

    public void Reset()
    {
        _calls = 0;
        Handler = null;
    }
}

/// <summary>Scriptable quote provider. A null handler simulates a hard provider failure.</summary>
public sealed class FakeQuoteProvider(string name) : IQuoteProvider
{
    private int _calls;

    public string Name { get; } = name;

    public int Calls => Volatile.Read(ref _calls);

    public Func<string, decimal>? Handler { get; set; }

    public Task<decimal> GetQuoteAsync(string providerSymbol, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        return Handler is null
            ? throw new InvalidOperationException($"Fake quote provider '{Name}' set to fail.")
            : Task.FromResult(Handler(providerSymbol));
    }

    public void Reset()
    {
        _calls = 0;
        Handler = null;
    }
}

/// <summary>Scriptable FX provider. A null handler simulates a hard provider failure.</summary>
public sealed class FakeFxProvider(string name) : IFxProvider
{
    private int _calls;

    public string Name { get; } = name;

    public int Calls => Volatile.Read(ref _calls);

    public Func<string, string, DateOnly, decimal?>? Handler { get; set; }

    public Task<decimal?> GetDailyRateAsync(
        string baseCurrency, string quoteCurrency, DateOnly date, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        return Handler is null
            ? throw new InvalidOperationException($"Fake FX provider '{Name}' set to fail.")
            : Task.FromResult(Handler(baseCurrency, quoteCurrency, date));
    }

    public void Reset()
    {
        _calls = 0;
        Handler = null;
    }
}

/// <summary>Scriptable news feed. Defaults to no items.</summary>
public sealed class FakeNewsFeedProvider : INewsFeedProvider
{
    public string Name => "rss";

    public List<FetchedNewsItem> Items { get; } = [];

    public Task<IReadOnlyList<FetchedNewsItem>> GetLatestAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<FetchedNewsItem>>(Items.ToList());

    public void Reset() => Items.Clear();
}

/// <summary>Inert sentiment/calendar/company-news stand-ins so nothing in tests can reach the network.</summary>
public sealed class FakeAuxProviders : ISentimentProvider, ICalendarProvider, ICompanyNewsProvider
{
    public string Name => "fake-aux";

    public Task<IReadOnlyList<SentimentPoint>> GetRecentAsync(int days, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SentimentPoint>>([]);

    public Task<IReadOnlyList<FetchedCalendarEvent>> GetEarningsAsync(
        DateOnly from, DateOnly to, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<FetchedCalendarEvent>>([]);

    public Task<IReadOnlyList<FetchedCalendarEvent>> GetEconomicEventsAsync(
        DateOnly from, DateOnly to, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<FetchedCalendarEvent>>([]);

    public Task<IReadOnlyList<FetchedNewsItem>> GetCompanyNewsAsync(
        string providerSymbol, DateOnly from, DateOnly to, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<FetchedNewsItem>>([]);
}
