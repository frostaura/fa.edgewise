using System.Linq.Expressions;
using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Auth;
using Microsoft.EntityFrameworkCore;

namespace Edgewise.Infrastructure.Data;

/// <summary>
/// Application DbContext. Every user-owned table has a global query filter bound
/// to the scoped <see cref="ICurrentUser"/>: with no authenticated user those
/// tables appear empty. Global/shared tables (Instrument, PriceBar, QuoteCache,
/// FxRate, NewsItem, SentimentReading, ProviderHealth) are unfiltered;
/// PlaybookTemplate and CatalystEvent expose shipped/global rows (UserId == null)
/// plus the current user's own rows.
/// </summary>
public class EdgewiseDbContext(DbContextOptions<EdgewiseDbContext> options, ICurrentUser currentUser)
    : DbContext(options)
{
    private readonly ICurrentUser _currentUser = currentUser;

    /// <summary>Read per query by the global filters (EF re-binds this to the live context instance).</summary>
    private Guid? CurrentUserId => _currentUser.UserId;

    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();
    public DbSet<RiskProfile> RiskProfiles => Set<RiskProfile>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Bucket> Buckets => Set<Bucket>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Instrument> Instruments => Set<Instrument>();
    public DbSet<Holding> Holdings => Set<Holding>();
    public DbSet<Lot> Lots => Set<Lot>();
    public DbSet<CashFlow> CashFlows => Set<CashFlow>();
    public DbSet<Dividend> Dividends => Set<Dividend>();
    public DbSet<Snapshot> Snapshots => Set<Snapshot>();
    public DbSet<Forecast> Forecasts => Set<Forecast>();
    public DbSet<PlaybookTemplate> PlaybookTemplates => Set<PlaybookTemplate>();
    public DbSet<TradePlan> TradePlans => Set<TradePlan>();
    public DbSet<TradePlanVersion> TradePlanVersions => Set<TradePlanVersion>();
    public DbSet<Trade> Trades => Set<Trade>();
    public DbSet<Fill> Fills => Set<Fill>();
    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<AdherenceResult> AdherenceResults => Set<AdherenceResult>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<TradeTag> TradeTags => Set<TradeTag>();
    public DbSet<DecisionLog> DecisionLogs => Set<DecisionLog>();
    public DbSet<OverrideLog> OverrideLogs => Set<OverrideLog>();
    public DbSet<Insight> Insights => Set<Insight>();
    public DbSet<LlmRequestLog> LlmRequestLogs => Set<LlmRequestLog>();
    public DbSet<WeeklyReview> WeeklyReviews => Set<WeeklyReview>();
    public DbSet<BrierForecast> BrierForecasts => Set<BrierForecast>();
    public DbSet<PriceBar> PriceBars => Set<PriceBar>();
    public DbSet<QuoteCache> QuoteCaches => Set<QuoteCache>();
    public DbSet<FxRate> FxRates => Set<FxRate>();
    public DbSet<NewsItem> NewsItems => Set<NewsItem>();
    public DbSet<CatalystEvent> CatalystEvents => Set<CatalystEvent>();
    public DbSet<SentimentReading> SentimentReadings => Set<SentimentReading>();
    public DbSet<ProviderHealth> ProviderHealths => Set<ProviderHealth>();
    public DbSet<Watchlist> Watchlists => Set<Watchlist>();
    public DbSet<WatchlistItem> WatchlistItems => Set<WatchlistItem>();
    public DbSet<Alert> Alerts => Set<Alert>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<PushSubscription> PushSubscriptions => Set<PushSubscription>();
    public DbSet<Zone> Zones => Set<Zone>();
    public DbSet<Strategy> Strategies => Set<Strategy>();
    public DbSet<StrategyVersion> StrategyVersions => Set<StrategyVersion>();
    public DbSet<Backtest> Backtests => Set<Backtest>();
    public DbSet<PipelineStateChange> PipelineStateChanges => Set<PipelineStateChange>();
    public DbSet<ImportMapping> ImportMappings => Set<ImportMapping>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Prices, quantities and rates all share one generous precision.
        configurationBuilder.Properties<decimal>().HavePrecision(28, 10);
        configurationBuilder.Properties<decimal?>().HavePrecision(28, 10);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // ------------------------------------------------------------- keys
        modelBuilder.Entity<PriceBar>().HasKey(b => new { b.InstrumentId, b.Timeframe, b.Ts });
        modelBuilder.Entity<QuoteCache>().HasKey(q => q.InstrumentId);
        modelBuilder.Entity<FxRate>().HasKey(f => new { f.Base, f.Quote, f.Date });
        modelBuilder.Entity<SentimentReading>().HasKey(s => new { s.Kind, s.Date });
        modelBuilder.Entity<ProviderHealth>().HasKey(p => p.Provider);
        modelBuilder.Entity<TradeTag>().HasKey(t => new { t.TradeId, t.TagId });

        // --------------------------------------------------- unique indexes
        modelBuilder.Entity<User>().HasIndex(u => u.Email).IsUnique();
        modelBuilder.Entity<RefreshToken>().HasIndex(t => t.TokenHash).IsUnique();
        modelBuilder.Entity<ApiToken>().HasIndex(t => t.TokenHash).IsUnique();
        modelBuilder.Entity<Instrument>()
            .HasIndex(i => new { i.Symbol, i.Exchange })
            .IsUnique()
            .AreNullsDistinct(false);
        modelBuilder.Entity<Snapshot>()
            .HasIndex(s => new { s.UserId, s.BucketId, s.Date })
            .IsUnique()
            .AreNullsDistinct(false);
        modelBuilder.Entity<Fill>().HasIndex(f => new { f.AccountId, f.SourceHash }).IsUnique();
        modelBuilder.Entity<WeeklyReview>().HasIndex(w => new { w.UserId, w.WeekStartDate }).IsUnique();
        modelBuilder.Entity<NewsItem>().HasIndex(n => n.UrlHash).IsUnique();

        // ------------------------------------------------- lookup indexes
        modelBuilder.Entity<AuditLog>().HasIndex(a => new { a.UserId, a.At });
        modelBuilder.Entity<RefreshToken>().HasIndex(t => t.UserId);
        modelBuilder.Entity<Trade>().HasIndex(t => new { t.UserId, t.Status });
        modelBuilder.Entity<Notification>().HasIndex(n => new { n.UserId, n.CreatedAt });

        // ------------------------------------- per-user global query filters
        // Direct ownership (entity carries UserId).
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (typeof(IUserOwned).IsAssignableFrom(entityType.ClrType))
            {
                modelBuilder.Entity(entityType.ClrType).HasQueryFilter(BuildOwnedFilter(entityType.ClrType));
            }
        }

        // The user row itself.
        modelBuilder.Entity<User>().HasQueryFilter(u => CurrentUserId != null && u.Id == CurrentUserId);

        // Shipped/global rows plus the current user's own rows.
        modelBuilder.Entity<PlaybookTemplate>()
            .HasQueryFilter(p => p.UserId == null || p.UserId == CurrentUserId);
        modelBuilder.Entity<CatalystEvent>()
            .HasQueryFilter(c => c.UserId == null || c.UserId == CurrentUserId);

        // Ownership through a parent navigation.
        modelBuilder.Entity<Lot>()
            .HasQueryFilter(l => CurrentUserId != null && l.Holding.UserId == CurrentUserId);
        modelBuilder.Entity<Dividend>()
            .HasQueryFilter(d => CurrentUserId != null && d.Holding.UserId == CurrentUserId);
        modelBuilder.Entity<TradePlanVersion>()
            .HasQueryFilter(v => CurrentUserId != null && v.Plan.UserId == CurrentUserId);
        modelBuilder.Entity<JournalEntry>()
            .HasQueryFilter(j => CurrentUserId != null && j.Trade.UserId == CurrentUserId);
        modelBuilder.Entity<Attachment>()
            .HasQueryFilter(a => CurrentUserId != null && a.JournalEntry.Trade.UserId == CurrentUserId);
        modelBuilder.Entity<AdherenceResult>()
            .HasQueryFilter(r => CurrentUserId != null && r.Trade.UserId == CurrentUserId);
        modelBuilder.Entity<TradeTag>()
            .HasQueryFilter(t => CurrentUserId != null && t.Trade.UserId == CurrentUserId);
        modelBuilder.Entity<WatchlistItem>()
            .HasQueryFilter(w => CurrentUserId != null && w.Watchlist.UserId == CurrentUserId);
        modelBuilder.Entity<StrategyVersion>()
            .HasQueryFilter(v => CurrentUserId != null && v.Strategy.UserId == CurrentUserId);
        modelBuilder.Entity<PipelineStateChange>()
            .HasQueryFilter(p => CurrentUserId != null && p.Strategy.UserId == CurrentUserId);

        // -------------------------------------------------- jsonb columns
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(string) && property.Name.EndsWith("Json", StringComparison.Ordinal))
                {
                    property.SetColumnType("jsonb");
                }
            }
        }
    }

    /// <summary>Builds <c>e => CurrentUserId != null &amp;&amp; e.UserId == CurrentUserId</c> for a concrete entity type.</summary>
    private LambdaExpression BuildOwnedFilter(Type clrType)
    {
        var parameter = Expression.Parameter(clrType, "e");
        // Member access rooted at the context constant is re-bound to the live
        // context instance by EF at query time.
        var currentUserId = Expression.Property(Expression.Constant(this), nameof(CurrentUserId));
        var body = Expression.AndAlso(
            Expression.NotEqual(currentUserId, Expression.Constant(null, typeof(Guid?))),
            Expression.Equal(
                Expression.Convert(Expression.Property(parameter, nameof(IUserOwned.UserId)), typeof(Guid?)),
                currentUserId));
        return Expression.Lambda(body, parameter);
    }
}
