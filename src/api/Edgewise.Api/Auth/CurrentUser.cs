using System.Security.Claims;
using Edgewise.Infrastructure.Auth;

namespace Edgewise.Api.Auth;

/// <summary>Resolves the current user id from the authenticated principal's "sub" claim.</summary>
public sealed class CurrentUser(IHttpContextAccessor httpContextAccessor) : ICurrentUser
{
    public Guid? UserId
    {
        get
        {
            var principal = httpContextAccessor.HttpContext?.User;
            var sub = principal?.FindFirstValue("sub") ?? principal?.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(sub, out var id) ? id : null;
        }
    }
}
