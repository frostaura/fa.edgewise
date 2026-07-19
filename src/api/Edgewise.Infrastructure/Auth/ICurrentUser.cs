namespace Edgewise.Infrastructure.Auth;

/// <summary>
/// The authenticated user for the current scope. Backs the per-user global
/// query filters in EdgewiseDbContext; null means "no user" and user-owned
/// tables appear empty.
/// </summary>
public interface ICurrentUser
{
    Guid? UserId { get; }
}
