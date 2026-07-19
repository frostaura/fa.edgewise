using System.ComponentModel;
using Edgewise.Api.Services.Journal;
using Edgewise.Infrastructure.Auth;
using ModelContextProtocol.Server;

namespace Edgewise.Api.Mcp;

/// <summary>
/// Journal analytics tools over the user's closed trades (expectancy, adherence
/// trend, plan-then-trade rate). User scoping happens via the DbContext's global
/// query filters bound to the authenticated MCP request.
/// </summary>
[McpServerToolType]
public sealed class AnalyticsTools(JournalAnalyticsService analytics)
{
    private static readonly string[] GroupByValues = ["setup", "instrument", "emotion", "dayOfWeek", "hourOfDay"];

    private static readonly AnalyticsFilter AllTrades = new(From: null, To: null, IsPaper: null);

    [McpServerTool(Name = "analytics_expectancy")]
    [Description("Grouped expectancy over the user's closed trades: per group the trade count, win rate, " +
        "average win/loss R and expectancy (in R). Use it to find which setups, instruments, emotions or " +
        "times of day make or lose money.")]
    public Task<string> Expectancy(
        [Description("Grouping key: 'setup' (default), 'instrument', 'emotion', 'dayOfWeek' or 'hourOfDay'.")]
        string groupBy = "setup",
        CancellationToken ct = default) => McpToolSupport.RunAsync(async () =>
    {
        var key = groupBy?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            key = "setup";
        }

        var match = GroupByValues.FirstOrDefault(v => string.Equals(v, key, StringComparison.OrdinalIgnoreCase))
            ?? throw ApiException.BadRequest(
                "invalid_groupBy", $"'{groupBy}' is not a valid groupBy. Accepted values: {string.Join(", ", GroupByValues)}.");

        var report = await analytics.ExpectancyAsync(match, AllTrades, ct);
        return new { groupBy = match, report };
    });

    [McpServerTool(Name = "analytics_adherence_trend")]
    [Description("Weekly plan-adherence trend: for each week the number of closed trades and the average " +
        "adherence score (0-100, how faithfully plans were executed). An empty list means no closed trades yet.")]
    public Task<string> AdherenceTrend(CancellationToken ct = default) => McpToolSupport.RunAsync(async () =>
        new { weeks = await analytics.AdherenceTrendAsync(AllTrades, ct) });

    [McpServerTool(Name = "analytics_ptr")]
    [Description("Weekly plan-then-trade rate (PTR): per week, the share of closed trades that had a plan " +
        "before entry. The core discipline metric — higher is better. An empty list means no closed trades yet.")]
    public Task<string> Ptr(CancellationToken ct = default) => McpToolSupport.RunAsync(async () =>
        new { weeks = await analytics.PtrAsync(AllTrades, ct) });
}
