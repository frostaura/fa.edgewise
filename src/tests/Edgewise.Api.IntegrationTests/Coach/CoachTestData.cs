using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Data;
using Edgewise.Infrastructure.Llm;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Edgewise.Api.IntegrationTests.Coach;

/// <summary>Direct-DB seeding helpers for the coach vertical tests.</summary>
public static class CoachTestData
{
    /// <summary>A user-scoped DbContext plus the DI scope keeping its options alive.</summary>
    public sealed class ScopedDb(IServiceScope scope, EdgewiseDbContext db) : IAsyncDisposable
    {
        public EdgewiseDbContext Db { get; } = db;

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            scope.Dispose();
        }
    }

    /// <summary>Opens a DbContext scoped to <paramref name="userId"/> (query filters apply as that user).</summary>
    public static ScopedDb OpenDb(TestAppFactory factory, Guid userId)
    {
        var scope = factory.Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<EdgewiseDbContext>>();
        return new ScopedDb(scope, new EdgewiseDbContext(options, new FixedCurrentUser(userId)));
    }

    public static async Task<Instrument> SeedInstrumentAsync(EdgewiseDbContext db, string? symbol = null)
    {
        var instrument = new Instrument
        {
            Id = Guid.NewGuid(),
            Symbol = symbol ?? $"TST{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}",
            Name = "Test instrument",
            AssetClass = AssetClass.Crypto,
            Currency = "USD",
        };
        db.Instruments.Add(instrument);
        await db.SaveChangesAsync();
        return instrument;
    }

    public sealed record SeededTrade(Trade Trade, TradePlan Plan, AdherenceResult Adherence);

    /// <summary>Closed trade with a plan, an adherence result carrying one deduction, and a journal note.</summary>
    public static async Task<SeededTrade> SeedClosedTradeAsync(
        EdgewiseDbContext db,
        Guid userId,
        Guid instrumentId,
        DateTime? closedAt = null,
        decimal rRealised = -1.0m,
        long pnlMinor = -15000,
        int adherenceScore = 78,
        string deductionCode = "EARLY_EXIT")
    {
        var closed = closedAt ?? DateTime.UtcNow.AddHours(-2);
        var bucketId = Guid.NewGuid();

        var plan = new TradePlan
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            InstrumentId = instrumentId,
            BucketId = bucketId,
            RiskProfileId = Guid.NewGuid(),
            Direction = TradeDirection.Long,
            SetupTag = "breakout",
            TriggerText = "Break of resistance with volume",
            StopPrice = 95m,
            SizeQty = 1m,
            Status = TradePlanStatus.Promoted,
            CreatedAt = closed.AddHours(-6),
        };
        db.TradePlans.Add(plan);

        var trade = new Trade
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            PlanId = plan.Id,
            InstrumentId = instrumentId,
            BucketId = bucketId,
            Direction = TradeDirection.Long,
            Status = TradeStatus.Closed,
            OpenedAt = closed.AddHours(-4),
            ClosedAt = closed,
            Qty = 1m,
            AvgEntryPrice = 100m,
            AvgExitPrice = 98m,
            RealisedPnlMinor = pnlMinor,
            Currency = "USD",
            RRealised = rRealised,
            HoldingSeconds = 4 * 3600,
            EmotionTag = EmotionTag.Rushed,
        };
        db.Trades.Add(trade);

        db.Fills.AddRange(
            new Fill
            {
                Id = Guid.NewGuid(),
                AccountId = Guid.NewGuid(),
                UserId = userId,
                InstrumentId = instrumentId,
                TradeId = trade.Id,
                Side = FillSide.Buy,
                Qty = 1m,
                Price = 100m,
                FeeCurrency = "USD",
                At = trade.OpenedAt,
                Source = FillSource.Manual,
                SourceHash = Guid.NewGuid().ToString("N"),
            },
            new Fill
            {
                Id = Guid.NewGuid(),
                AccountId = Guid.NewGuid(),
                UserId = userId,
                InstrumentId = instrumentId,
                TradeId = trade.Id,
                Side = FillSide.Sell,
                Qty = 1m,
                Price = 98m,
                FeeCurrency = "USD",
                At = closed,
                Source = FillSource.Manual,
                SourceHash = Guid.NewGuid().ToString("N"),
            });

        var adherence = new AdherenceResult
        {
            Id = Guid.NewGuid(),
            TradeId = trade.Id,
            RubricVersion = 1,
            Score = adherenceScore,
            Grade = adherenceScore >= 90 ? "A" : adherenceScore >= 75 ? "B" : "C",
            DeductionsJson = adherenceScore >= 100
                ? "[]"
                : $$"""[{"code":"{{deductionCode}}","points":{{100 - adherenceScore}},"evidence":"Exited at 0.6R against a 2R rule"}]""",
            ComputedAt = closed.AddMinutes(5),
        };
        db.AdherenceResults.Add(adherence);

        db.JournalEntries.Add(new JournalEntry
        {
            Id = Guid.NewGuid(),
            TradeId = trade.Id,
            NotesMd = "Felt rushed at the exit; did not wait for the rule.",
            At = closed.AddMinutes(10),
        });

        await db.SaveChangesAsync();
        return new SeededTrade(trade, plan, adherence);
    }
}
