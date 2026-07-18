namespace Edgewise.Infrastructure.Jobs.Coach;

/// <summary>
/// Per-user unit of the nightly coach batch. Implemented in the API layer (where the coach
/// orchestrator lives) and resolved defensively by <see cref="NightlyCoachBatch"/>.
/// </summary>
public interface ICoachNightlyWorker
{
    Task RunForUserAsync(Guid userId, DateTime utcNow, CancellationToken ct);
}
