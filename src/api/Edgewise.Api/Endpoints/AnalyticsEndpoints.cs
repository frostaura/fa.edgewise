using Edgewise.Api.Services.Journal;

namespace Edgewise.Api.Endpoints;

/// <summary>
/// R-analytics over closed trades. Thin pass-throughs to
/// <see cref="JournalAnalyticsService"/>, which is also the Coach chat tool substrate.
/// </summary>
public sealed class AnalyticsEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder app)
    {
        var analytics = app.MapGroup("/api/analytics").RequireAuthorization();

        analytics.MapGet("/expectancy", (
                string? groupBy, DateTime? from, DateTime? to, bool? isPaper,
                JournalAnalyticsService service, CancellationToken ct) =>
            service.ExpectancyAsync(groupBy ?? "setup", new AnalyticsFilter(from, to, isPaper), ct));

        analytics.MapGet("/r-distribution", (
                DateTime? from, DateTime? to, bool? isPaper,
                JournalAnalyticsService service, CancellationToken ct) =>
            service.RDistributionAsync(new AnalyticsFilter(from, to, isPaper), ct));

        analytics.MapGet("/drawdown", (
                Guid? bucketId, DateTime? from, DateTime? to, bool? isPaper,
                JournalAnalyticsService service, CancellationToken ct) =>
            service.DrawdownAsync(bucketId, new AnalyticsFilter(from, to, isPaper), ct));

        analytics.MapGet("/calendar-heatmap", (
                int? year, DateTime? from, DateTime? to, bool? isPaper,
                JournalAnalyticsService service, CancellationToken ct) =>
            service.CalendarHeatmapAsync(year, new AnalyticsFilter(from, to, isPaper), ct));

        analytics.MapGet("/sessions", (
                DateTime? from, DateTime? to, bool? isPaper,
                JournalAnalyticsService service, CancellationToken ct) =>
            service.SessionsAsync(new AnalyticsFilter(from, to, isPaper), ct));

        analytics.MapGet("/tilt", (
                DateTime? from, DateTime? to, bool? isPaper,
                JournalAnalyticsService service, CancellationToken ct) =>
            service.TiltAsync(new AnalyticsFilter(from, to, isPaper), ct));

        analytics.MapGet("/ptr", (
                DateTime? from, DateTime? to, bool? isPaper,
                JournalAnalyticsService service, CancellationToken ct) =>
            service.PtrAsync(new AnalyticsFilter(from, to, isPaper), ct));

        analytics.MapGet("/adherence-trend", (
                DateTime? from, DateTime? to, bool? isPaper,
                JournalAnalyticsService service, CancellationToken ct) =>
            service.AdherenceTrendAsync(new AnalyticsFilter(from, to, isPaper), ct));

        analytics.MapGet("/bias-cards", (
                DateTime? from, DateTime? to, bool? isPaper,
                JournalAnalyticsService service, CancellationToken ct) =>
            service.BiasCardsAsync(new AnalyticsFilter(from, to, isPaper), ct));
    }
}
