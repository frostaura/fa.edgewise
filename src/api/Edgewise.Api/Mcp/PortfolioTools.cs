using System.ComponentModel;
using Edgewise.Api.Services.Portfolio;
using ModelContextProtocol.Server;

namespace Edgewise.Api.Mcp;

/// <summary>
/// Portfolio read-model tools (summary and drawdown-ladder state) for the
/// authenticated user. Values are in minor units (cents) of the stated currency.
/// </summary>
[McpServerToolType]
public sealed class PortfolioTools(PortfolioService portfolio)
{
    [McpServerTool(Name = "portfolio_summary")]
    [Description("The user's portfolio summary: base currency, total value/cost/unrealised P&L (minor units), " +
        "today's change, per-bucket breakdown with target vs actual allocation and drift flags, and the " +
        "current drawdown-ladder state.")]
    public Task<string> Summary(CancellationToken ct = default) => McpToolSupport.RunAsync(async () =>
        await portfolio.GetSummaryAsync(ct));

    [McpServerTool(Name = "portfolio_ladder_state")]
    [Description("The trading bucket's drawdown-ladder state machine: current state (normal, riskHalved, paused " +
        "or paperProposed), drawdown fraction from the high-water mark, HWM and current equity (minor units), " +
        "and the configured thresholds. Use it before suggesting any new trading activity.")]
    public Task<string> LadderState(CancellationToken ct = default) => McpToolSupport.RunAsync(async () =>
        await portfolio.GetLadderStateAsync(ct));
}
