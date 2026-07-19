using Edgewise.Infrastructure.MarketData;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Edgewise.Api.IntegrationTests.MarketData;

/// <summary>
/// Boots the real API against a per-run PostgreSQL database with every external
/// market-data provider replaced by in-memory fakes, so no test can hit the real
/// network. Mirrors TestAppFactory's database bootstrap (that type is sealed).
/// </summary>
public sealed class MarketDataFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private string? _adminConnectionString;

    public string ConnectionString { get; private set; } = string.Empty;

    // Bar chain fakes (equity: yahoo -> stooq; crypto: binance -> coinbase).
    public FakeBarProvider YahooBars { get; } = new(ProviderNames.Yahoo);
    public FakeBarProvider StooqBars { get; } = new(ProviderNames.Stooq);
    public FakeBarProvider BinanceBars { get; } = new(ProviderNames.Binance);
    public FakeBarProvider CoinbaseBars { get; } = new(ProviderNames.Coinbase);

    // Quote chain fakes.
    public FakeQuoteProvider YahooQuotes { get; } = new(ProviderNames.Yahoo);
    public FakeQuoteProvider StooqQuotes { get; } = new(ProviderNames.Stooq);
    public FakeQuoteProvider BinanceQuotes { get; } = new(ProviderNames.Binance);
    public FakeQuoteProvider CoinbaseQuotes { get; } = new(ProviderNames.Coinbase);
    public FakeQuoteProvider CoinGeckoQuotes { get; } = new(ProviderNames.CoinGecko);

    // FX chain fakes.
    public FakeFxProvider FrankfurterFx { get; } = new(ProviderNames.Frankfurter);
    public FakeFxProvider ExchangeRateApiFx { get; } = new(ProviderNames.ExchangeRateApi);

    public FakeNewsFeedProvider NewsFeed { get; } = new();

    /// <summary>Puts every fake back to its failing/empty default. Called by each test's constructor.</summary>
    public void ResetFakes()
    {
        YahooBars.Reset();
        StooqBars.Reset();
        BinanceBars.Reset();
        CoinbaseBars.Reset();
        YahooQuotes.Reset();
        StooqQuotes.Reset();
        BinanceQuotes.Reset();
        CoinbaseQuotes.Reset();
        CoinGeckoQuotes.Reset();
        FrankfurterFx.Reset();
        ExchangeRateApiFx.Reset();
        NewsFeed.Reset();
    }

    async Task IAsyncLifetime.InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable("EDGEWISE_TEST_DB");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            _adminConnectionString = configured;
            var builder = new NpgsqlConnectionStringBuilder(configured)
            {
                Database = $"edgewise_test_{Guid.NewGuid():N}",
            };
            ConnectionString = builder.ConnectionString;
        }
        else
        {
            _container = new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase($"edgewise_test_{Guid.NewGuid():N}")
                .Build();
            await _container.StartAsync();
            ConnectionString = _container.GetConnectionString();
        }

        // Force host creation (and thereby migration + seed) up front.
        _ = Server;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Default", ConnectionString);
        builder.UseSetting("DisableHangfire", "true");
        builder.UseSetting("EDGEWISE_JWT_SECRET", "integration-test-jwt-secret-0123456789abcdef");
        builder.UseSetting("EDGEWISE_ENCRYPTION_KEY", Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()));

        builder.ConfigureServices(services =>
        {
            // Swap every real provider adapter for the fakes; the MarketDataService
            // chains resolve providers by Name, so the fakes slot straight in.
            services.RemoveAll<IBarProvider>();
            services.RemoveAll<IQuoteProvider>();
            services.RemoveAll<IFxProvider>();
            services.RemoveAll<ISentimentProvider>();
            services.RemoveAll<INewsFeedProvider>();
            services.RemoveAll<ICompanyNewsProvider>();
            services.RemoveAll<ICalendarProvider>();

            services.AddSingleton<IBarProvider>(YahooBars);
            services.AddSingleton<IBarProvider>(StooqBars);
            services.AddSingleton<IBarProvider>(BinanceBars);
            services.AddSingleton<IBarProvider>(CoinbaseBars);
            services.AddSingleton<IQuoteProvider>(YahooQuotes);
            services.AddSingleton<IQuoteProvider>(StooqQuotes);
            services.AddSingleton<IQuoteProvider>(BinanceQuotes);
            services.AddSingleton<IQuoteProvider>(CoinbaseQuotes);
            services.AddSingleton<IQuoteProvider>(CoinGeckoQuotes);
            services.AddSingleton<IFxProvider>(FrankfurterFx);
            services.AddSingleton<IFxProvider>(ExchangeRateApiFx);
            services.AddSingleton<INewsFeedProvider>(NewsFeed);

            var aux = new FakeAuxProviders();
            services.AddSingleton<ISentimentProvider>(aux);
            services.AddSingleton<ICompanyNewsProvider>(aux);
            services.AddSingleton<ICalendarProvider>(aux);
        });
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync();

        if (_adminConnectionString is not null)
        {
            // Best-effort cleanup of the per-run database on the shared server.
            try
            {
                var dbName = new NpgsqlConnectionStringBuilder(ConnectionString).Database;
                var admin = new NpgsqlConnectionStringBuilder(_adminConnectionString) { Database = "postgres" };
                await using var connection = new NpgsqlConnection(admin.ConnectionString);
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = $"DROP DATABASE IF EXISTS \"{dbName}\" WITH (FORCE)";
                await command.ExecuteNonQueryAsync();
            }
            catch (NpgsqlException)
            {
                // Leave the database behind rather than failing the run.
            }
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class MarketDataCollection : ICollectionFixture<MarketDataFactory>
{
    public const string Name = "market-data";
}
