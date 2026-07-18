using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Data;
using Edgewise.Infrastructure.MarketData;
using Hangfire;
using Microsoft.EntityFrameworkCore;

namespace Edgewise.Infrastructure.Jobs;

/// <summary>Daily: upserts the last 30 days of the alternative.me crypto Fear &amp; Greed index.</summary>
[AutomaticRetry(Attempts = 3)]
public sealed class SentimentJob(EdgewiseDbContext db, ISentimentProvider sentiment, ProviderHealthWriter health)
{
    public async Task RunAsync(CancellationToken ct)
    {
        IReadOnlyList<SentimentPoint> points;
        try
        {
            points = await sentiment.GetRecentAsync(30, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await health.ReportErrorAsync(sentiment.Name, ex.Message, ct);
            throw; // let Hangfire retry
        }

        foreach (var point in points)
        {
            await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO "SentimentReadings" ("Kind", "Date", "Value", "Label")
                VALUES ({(int)SentimentKind.CryptoFG}, {point.Date}, {point.Value}, {point.Label})
                ON CONFLICT ("Kind", "Date") DO UPDATE
                SET "Value" = EXCLUDED."Value", "Label" = EXCLUDED."Label"
                """,
                ct);
        }

        await health.ReportSuccessAsync(sentiment.Name, ct);
    }
}
