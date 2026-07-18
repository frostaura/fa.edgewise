using Edgewise.Api.Services.Journal;
using Edgewise.Domain.Entities;

namespace Edgewise.Api.Endpoints;

/// <summary>The trade log: filtered list, composite detail, close pipeline, rebuild.</summary>
public sealed class TradesEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder app)
    {
        var trades = app.MapGroup("/api/trades").RequireAuthorization();

        trades.MapGet("/", (
                string? status,
                Guid? instrumentId,
                Guid? bucketId,
                string? setupTag,
                string? emotion,
                bool? isPaper,
                bool? hasPlan,
                string? grade,
                DateTime? from,
                DateTime? to,
                string? search,
                int? page,
                int? pageSize,
                TradeService service,
                CancellationToken ct) =>
            service.ListAsync(
                new TradeListFilter(
                    JournalCommon.ParseEnum<TradeStatus>(status, "status"),
                    instrumentId, bucketId, setupTag,
                    JournalCommon.ParseEnum<EmotionTag>(emotion, "emotion"),
                    isPaper, hasPlan, grade, from, to, search, page ?? 1, pageSize ?? 25),
                ct));

        trades.MapGet("/{id:guid}", (Guid id, TradeService service, CancellationToken ct) =>
            service.GetDetailAsync(id, ct));

        trades.MapPatch("/{id:guid}", (Guid id, PatchTradeRequest request, TradeService service, CancellationToken ct) =>
            service.PatchAsync(id, request, ct));

        trades.MapPost("/{id:guid}/close", (Guid id, CloseTradeRequest request, TradeService service, CancellationToken ct) =>
            service.CloseAsync(id, request, ct));

        trades.MapPost("/{id:guid}/reopen", (Guid id, TradeService service, CancellationToken ct) =>
            service.ReopenAsync(id, ct));

        trades.MapPost("/rebuild", (RebuildTradesRequest request, TradeService service, CancellationToken ct) =>
            service.RebuildAsync(request, ct));

        trades.MapPost("/bulk/tags", async (BulkTagsRequest request, TradeService service, CancellationToken ct) =>
            Results.Ok(new { updated = await service.BulkTagsAsync(request, ct) }));
    }
}
