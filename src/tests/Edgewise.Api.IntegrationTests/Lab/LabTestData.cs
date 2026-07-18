using System.Text.Json;
using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Auth;
using Edgewise.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Edgewise.Api.IntegrationTests.Lab;

/// <summary>Direct-database seeding helpers for Lab tests (instruments, bars, trades).</summary>
public static class LabTestData
{
    public sealed class StubCurrentUser(Guid? userId) : ICurrentUser
    {
        public Guid? UserId => userId;
    }

    public static EdgewiseDbContext CreateDbContext(TestAppFactory factory, Guid? userId = null)
    {
        var options = new DbContextOptionsBuilder<EdgewiseDbContext>()
            .UseNpgsql(factory.ConnectionString)
            .Options;
        return new EdgewiseDbContext(options, new StubCurrentUser(userId));
    }

    /// <summary>Inserts a fresh instrument (global table).</summary>
    public static async Task<Guid> SeedInstrumentAsync(TestAppFactory factory)
    {
        await using var db = CreateDbContext(factory);
        var instrument = new Instrument
        {
            Id = Guid.NewGuid(),
            Symbol = $"LAB{Guid.NewGuid():N}"[..12].ToUpperInvariant(),
            Name = "Lab test instrument",
            AssetClass = AssetClass.Crypto,
            Currency = "USD",
            Exchange = "TEST",
        };
        db.Instruments.Add(instrument);
        await db.SaveChangesAsync();
        return instrument.Id;
    }

    /// <summary>
    /// Seeds <paramref name="count"/> synthetic daily bars: a steady uptrend with a sine
    /// wave overlaid, so MA-cross style strategies fire several trades.
    /// </summary>
    public static async Task SeedDailyBarsAsync(TestAppFactory factory, Guid instrumentId, int count = 300)
    {
        await using var db = CreateDbContext(factory);
        var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var bars = new List<PriceBar>(count);
        for (var i = 0; i < count; i++)
        {
            var close = 100m + (0.15m * i) + (8m * (decimal)Math.Sin(i / 8.0));
            var open = i == 0 ? close : bars[i - 1].C;
            var high = Math.Max(open, close) + 1.2m;
            var low = Math.Min(open, close) - 1.2m;
            bars.Add(new PriceBar
            {
                InstrumentId = instrumentId,
                Timeframe = Timeframe.D1,
                Ts = start.AddDays(i),
                O = open,
                H = high,
                L = low,
                C = close,
                V = 1000m + (i % 7) * 50m,
                Provider = "test",
            });
        }

        db.PriceBars.AddRange(bars);
        await db.SaveChangesAsync();
    }

    /// <summary>Seeds closed trades tagged to a strategy, each with an adherence score.</summary>
    public static async Task SeedStrategyTradesAsync(
        TestAppFactory factory,
        Guid userId,
        Guid strategyId,
        Guid instrumentId,
        int count,
        bool isPaper,
        int adherenceScore,
        decimal rRealised)
    {
        await using var db = CreateDbContext(factory, userId);
        var bucketId = await db.Buckets.Select(b => b.Id).FirstAsync();
        var at = DateTime.UtcNow.AddDays(-count - 1);

        for (var i = 0; i < count; i++)
        {
            var trade = new Trade
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                InstrumentId = instrumentId,
                BucketId = bucketId,
                Direction = TradeDirection.Long,
                Status = TradeStatus.Closed,
                OpenedAt = at.AddDays(i),
                ClosedAt = at.AddDays(i).AddHours(6),
                Qty = 1m,
                AvgEntryPrice = 100m,
                AvgExitPrice = 100m + rRealised,
                Currency = "USD",
                RRealised = rRealised,
                IsPaper = isPaper,
                StrategyId = strategyId,
                StrategyStateAtEntry = isPaper ? "paper" : "tinyLive",
                PositionMethod = PositionMethod.Manual,
            };
            db.Trades.Add(trade);
            db.AdherenceResults.Add(new AdherenceResult
            {
                Id = Guid.NewGuid(),
                TradeId = trade.Id,
                RubricVersion = 1,
                Score = adherenceScore,
                Grade = adherenceScore >= 90 ? "A" : "C",
                ComputedAt = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync();
    }

    /// <summary>A minimal valid rule tree: MA20 cross with a time stop.</summary>
    public const string SimpleRuleTree = """
        {
          "direction": "long",
          "combinator": "and",
          "conditions": [
            { "indicator": "priceVsMa", "params": { "period": 20 }, "operator": "crossAbove", "operand": 0, "enabled": true }
          ],
          "exits": { "trailMethod": "none", "timeStopBars": 8, "stopAtrMult": 2 },
          "risk": { "riskPctPerTrade": 1 }
        }
        """;

    public static JsonElement RuleTree(string json = SimpleRuleTree)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
