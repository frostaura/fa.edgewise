using Edgewise.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Edgewise.Infrastructure.Data;

/// <summary>
/// Idempotent seed data. <see cref="SeedSharedAsync"/> runs at startup and owns
/// global rows (shipped playbook templates, common instruments);
/// <see cref="SeedUserDefaultsAsync"/> runs once per new user (buckets and risk
/// profile presets). All queries bypass the per-user filters because seeding
/// runs without an authenticated user.
/// </summary>
public static class DataSeeder
{
    public static async Task SeedSharedAsync(EdgewiseDbContext db, CancellationToken ct = default)
    {
        await SeedShippedTemplatesAsync(db, ct);
        await SeedCommonInstrumentsAsync(db, ct);
        await db.SaveChangesAsync(ct);
    }

    private static async Task SeedShippedTemplatesAsync(EdgewiseDbContext db, CancellationToken ct)
    {
        var existing = await db.PlaybookTemplates.IgnoreQueryFilters()
            .Where(t => t.UserId == null)
            .Select(t => t.Name)
            .ToListAsync(ct);

        foreach (var template in ShippedTemplates())
        {
            if (!existing.Contains(template.Name))
            {
                db.PlaybookTemplates.Add(template);
            }
        }
    }

    private static async Task SeedCommonInstrumentsAsync(EdgewiseDbContext db, CancellationToken ct)
    {
        var existing = await db.Instruments
            .Select(i => new { i.Symbol, i.Exchange })
            .ToListAsync(ct);

        foreach (var instrument in CommonInstruments())
        {
            if (!existing.Any(e => e.Symbol == instrument.Symbol && e.Exchange == instrument.Exchange))
            {
                db.Instruments.Add(instrument);
            }
        }
    }

    /// <summary>Seeds default buckets and risk profiles for a newly registered user. Idempotent.</summary>
    public static async Task SeedUserDefaultsAsync(
        EdgewiseDbContext db, Guid userId, string baseCurrency = "ZAR", CancellationToken ct = default)
    {
        if (!await db.Buckets.IgnoreQueryFilters().AnyAsync(b => b.UserId == userId, ct))
        {
            db.Buckets.AddRange(
                NewBucket(userId, "Long-term", BucketKind.LongTerm, targetAllocPct: 60, contributionSplitPct: 60, baseCurrency),
                NewBucket(userId, "Trading", BucketKind.Trading, targetAllocPct: 25, contributionSplitPct: 25, baseCurrency),
                NewBucket(userId, "Prediction", BucketKind.Prediction, targetAllocPct: 5, contributionSplitPct: 5, baseCurrency),
                NewBucket(userId, "Cash", BucketKind.Cash, targetAllocPct: 10, contributionSplitPct: 10, baseCurrency));
        }

        if (!await db.RiskProfiles.IgnoreQueryFilters().AnyAsync(r => r.UserId == userId, ct))
        {
            db.RiskProfiles.AddRange(
                new RiskProfile
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    Name = "Conservative",
                    IsActive = false,
                    RiskPct = 0.5m,
                    HeatCapPct = 2m,
                    ClusterCapPct = 1.5m,
                    DailyStopPct = 2m,
                    DailyLossCountStop = 2,
                    WeeklyStopPct = 4m,
                    MaxLeverage = 1m,
                    MinRR = 2m,
                    MaxPositions = 3,
                    LadderThresholdsJson = DefaultLadderJson,
                },
                new RiskProfile
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    Name = "Standard",
                    IsActive = true,
                    RiskPct = 1m,
                    HeatCapPct = 4m,
                    ClusterCapPct = 2m,
                    DailyStopPct = 3m,
                    DailyLossCountStop = 3,
                    WeeklyStopPct = 6m,
                    MaxLeverage = 2m,
                    MinRR = 1.5m,
                    MaxPositions = 5,
                    LadderThresholdsJson = DefaultLadderJson,
                },
                new RiskProfile
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    Name = "Aggressive",
                    IsActive = false,
                    RiskPct = 2m,
                    HeatCapPct = 6m,
                    ClusterCapPct = 3m,
                    DailyStopPct = 5m,
                    DailyLossCountStop = 4,
                    WeeklyStopPct = 10m,
                    MaxLeverage = 3m,
                    MinRR = 1.2m,
                    MaxPositions = 8,
                    LadderThresholdsJson = DefaultLadderJson,
                });
        }

        await db.SaveChangesAsync(ct);
    }

    private const string DefaultLadderJson =
        """[{"atR":1.0,"action":"moveStopToBreakeven"},{"atR":2.0,"action":"takePartial","pct":50}]""";

    private static Bucket NewBucket(
        Guid userId, string name, BucketKind kind, decimal targetAllocPct, decimal contributionSplitPct, string currency) =>
        new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = name,
            Kind = kind,
            TargetAllocPct = targetAllocPct,
            ContributionSplitPct = contributionSplitPct,
            HighWaterMarkMinor = 0,
            Currency = currency,
        };

    private static IEnumerable<PlaybookTemplate> ShippedTemplates() =>
    [
        new PlaybookTemplate
        {
            Id = Guid.NewGuid(),
            UserId = null,
            Name = "Trend-Pullback",
            Description = "Enter with the higher-timeframe trend on a pullback to a moving average or demand zone.",
            PrefillJson = """
                {"direction":"long","setupTag":"trend-pullback","triggerText":"Price pulls back to 20EMA/zone in an uptrend and prints a reversal bar","targetRule":{"type":"rMultiple","levels":[{"atR":2,"closePct":50},{"atR":3,"closePct":50}]}}
                """,
            ChecklistJson = """
                [{"id":"htf-trend","label":"Higher-timeframe trend is up (HH/HL or above 50EMA)"},{"id":"pullback-depth","label":"Pullback is orderly (<50% of impulse leg)"},{"id":"zone-confluence","label":"Entry is at a marked zone or moving average"},{"id":"trigger-bar","label":"Reversal trigger bar has closed"},{"id":"no-catalyst","label":"No red catalyst inside the next 24h"}]
                """,
        },
        new PlaybookTemplate
        {
            Id = Guid.NewGuid(),
            UserId = null,
            Name = "Breakout-Retest",
            Description = "Trade the retest of a broken level after a range or consolidation breakout.",
            PrefillJson = """
                {"direction":"long","setupTag":"breakout-retest","triggerText":"Broken resistance is retested as support and holds on lower-timeframe close","targetRule":{"type":"measuredMove","fallback":{"type":"rMultiple","levels":[{"atR":2,"closePct":100}]}}}
                """,
            ChecklistJson = """
                [{"id":"clean-level","label":"Level was tested at least twice before breaking"},{"id":"volume-expansion","label":"Breakout happened on expanding volume"},{"id":"retest-hold","label":"Retest held: no close back inside the range"},{"id":"room-to-target","label":"Measured move target is clear of major resistance"},{"id":"no-catalyst","label":"No red catalyst inside the next 24h"}]
                """,
        },
        new PlaybookTemplate
        {
            Id = Guid.NewGuid(),
            UserId = null,
            Name = "Mean-Reversion",
            Description = "Fade an extended move back toward its mean in a ranging or overextended market.",
            PrefillJson = """
                {"direction":"long","setupTag":"mean-reversion","triggerText":"Price is >2 ATR from the 20EMA at range extreme and momentum diverges","targetRule":{"type":"meanTouch","mean":"ema20","levels":[{"closePct":100}]}}
                """,
            ChecklistJson = """
                [{"id":"range-context","label":"Market is ranging, not trending (ADX low / flat EMAs)"},{"id":"extension","label":"Price is at least 2 ATR from the mean"},{"id":"divergence","label":"Momentum divergence or exhaustion candle present"},{"id":"tight-stop","label":"Stop beyond the extreme; RR still >= 1.5"},{"id":"no-catalyst","label":"No red catalyst inside the next 24h"}]
                """,
        },
        new PlaybookTemplate
        {
            Id = Guid.NewGuid(),
            UserId = null,
            Name = "Prediction-Market",
            Description = "Buy probability mispricing in a prediction market where your estimate diverges from the market's.",
            PrefillJson = """
                {"direction":"long","setupTag":"prediction-market","triggerText":"My estimated probability differs from market price by >=10 points with clear resolution rules","targetRule":{"type":"holdToResolution"}}
                """,
            ChecklistJson = """
                [{"id":"rules-read","label":"Resolution rules read in full, edge cases noted"},{"id":"estimate-written","label":"Own probability estimate written down before looking at price"},{"id":"edge-threshold","label":"Edge >= 10 percentage points after fees"},{"id":"sizing","label":"Position sized to Kelly fraction cap in risk profile"},{"id":"resolution-date","label":"Resolution date noted and capital lock-up acceptable"}]
                """,
        },
    ];

    private static IEnumerable<Instrument> CommonInstruments() =>
    [
        new Instrument
        {
            Id = Guid.NewGuid(),
            Symbol = "BTC-USD",
            Name = "Bitcoin",
            AssetClass = AssetClass.Crypto,
            Exchange = "Binance",
            Currency = "USD",
            ProviderSymbolsJson = """{"binance":"BTCUSDT","coingecko":"bitcoin"}""",
        },
        new Instrument
        {
            Id = Guid.NewGuid(),
            Symbol = "ETH-USD",
            Name = "Ethereum",
            AssetClass = AssetClass.Crypto,
            Exchange = "Binance",
            Currency = "USD",
            ProviderSymbolsJson = """{"binance":"ETHUSDT","coingecko":"ethereum"}""",
        },
        new Instrument
        {
            Id = Guid.NewGuid(),
            Symbol = "STXNDQ.JO",
            Name = "Satrix Nasdaq 100 ETF",
            AssetClass = AssetClass.Etf,
            Exchange = "JSE",
            Currency = "ZAR",
            ProviderSymbolsJson = """{"eodhd":"STXNDQ.JSE","yahoo":"STXNDQ.JO"}""",
        },
        new Instrument
        {
            Id = Guid.NewGuid(),
            Symbol = "STX500.JO",
            Name = "Satrix S&P 500 ETF",
            AssetClass = AssetClass.Etf,
            Exchange = "JSE",
            Currency = "ZAR",
            ProviderSymbolsJson = """{"eodhd":"STX500.JSE","yahoo":"STX500.JO"}""",
        },
        new Instrument
        {
            Id = Guid.NewGuid(),
            Symbol = "SYG500.JO",
            Name = "Sygnia Itrix S&P 500 ETF",
            AssetClass = AssetClass.Etf,
            Exchange = "JSE",
            Currency = "ZAR",
            ProviderSymbolsJson = """{"eodhd":"SYG500.JSE","yahoo":"SYG500.JO"}""",
        },
        new Instrument
        {
            Id = Guid.NewGuid(),
            Symbol = "ZAR",
            Name = "South African Rand (cash)",
            AssetClass = AssetClass.Cash,
            Exchange = null,
            Currency = "ZAR",
            ProviderSymbolsJson = """{}""",
        },
        new Instrument
        {
            Id = Guid.NewGuid(),
            Symbol = "USD",
            Name = "US Dollar (cash)",
            AssetClass = AssetClass.Cash,
            Exchange = null,
            Currency = "USD",
            ProviderSymbolsJson = """{}""",
        },
    ];
}
