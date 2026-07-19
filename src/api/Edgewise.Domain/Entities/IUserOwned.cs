namespace Edgewise.Domain.Entities;

/// <summary>
/// Marker for entities directly owned by a single user. Drives per-user query
/// filters and audit logging.
/// </summary>
public interface IUserOwned
{
    Guid UserId { get; }
}
