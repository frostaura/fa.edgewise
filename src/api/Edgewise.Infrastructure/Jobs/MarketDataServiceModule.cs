using Edgewise.Infrastructure.MarketData;
using Edgewise.Infrastructure.MarketData.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Edgewise.Infrastructure.Jobs;

/// <summary>
/// Auto-discovered DI wiring for the market-data layer: the shared HTTP gateway,
/// every provider adapter (registered under its capability interfaces so tests can
/// swap the collections), the MarketDataService facade and the Hangfire job classes.
/// </summary>
public sealed class MarketDataServiceModule : IServiceModule
{
    public void Configure(IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton<ProviderHttp>();

        // Concrete adapters.
        services.AddSingleton<YahooChartProvider>();
        services.AddSingleton<StooqProvider>();
        services.AddSingleton<BinanceKlinesProvider>();
        services.AddSingleton<CoinbaseCandlesProvider>();
        services.AddSingleton<CoinGeckoQuoteProvider>();
        services.AddSingleton<FrankfurterFxProvider>();
        services.AddSingleton<ExchangeRateApiFxProvider>();
        services.AddSingleton<AlternativeMeFgProvider>();
        services.AddSingleton(sp => new FinnhubProvider(
            sp.GetRequiredService<ProviderHttp>(), config["FINNHUB_API_KEY"]));
        services.AddSingleton(sp => new RssNewsProvider(
            sp.GetRequiredService<ProviderHttp>(), sp.GetRequiredService<ILogger<RssNewsProvider>>()));

        // Capability interfaces (integration tests replace these collections with fakes).
        services.AddSingleton<IBarProvider>(sp => sp.GetRequiredService<YahooChartProvider>());
        services.AddSingleton<IBarProvider>(sp => sp.GetRequiredService<StooqProvider>());
        services.AddSingleton<IBarProvider>(sp => sp.GetRequiredService<BinanceKlinesProvider>());
        services.AddSingleton<IBarProvider>(sp => sp.GetRequiredService<CoinbaseCandlesProvider>());
        services.AddSingleton<IQuoteProvider>(sp => sp.GetRequiredService<YahooChartProvider>());
        services.AddSingleton<IQuoteProvider>(sp => sp.GetRequiredService<StooqProvider>());
        services.AddSingleton<IQuoteProvider>(sp => sp.GetRequiredService<BinanceKlinesProvider>());
        services.AddSingleton<IQuoteProvider>(sp => sp.GetRequiredService<CoinbaseCandlesProvider>());
        services.AddSingleton<IQuoteProvider>(sp => sp.GetRequiredService<CoinGeckoQuoteProvider>());
        services.AddSingleton<IFxProvider>(sp => sp.GetRequiredService<FrankfurterFxProvider>());
        services.AddSingleton<IFxProvider>(sp => sp.GetRequiredService<ExchangeRateApiFxProvider>());
        services.AddSingleton<ISentimentProvider>(sp => sp.GetRequiredService<AlternativeMeFgProvider>());
        services.AddSingleton<INewsFeedProvider>(sp => sp.GetRequiredService<RssNewsProvider>());
        services.AddSingleton<ICompanyNewsProvider>(sp => sp.GetRequiredService<FinnhubProvider>());
        services.AddSingleton<ICalendarProvider>(sp => sp.GetRequiredService<FinnhubProvider>());

        // Facade + jobs (scoped: they own a DbContext per unit of work).
        services.AddScoped<ProviderHealthWriter>();
        services.AddScoped<MarketDataService>();
        services.AddScoped<PriceSyncJob>();
        services.AddScoped<FxSyncJob>();
        services.AddScoped<SentimentJob>();
        services.AddScoped<NewsPollJob>();
        services.AddScoped<CalendarSyncJob>();
    }
}
