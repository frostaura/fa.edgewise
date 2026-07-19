using Hangfire.Dashboard;

namespace Edgewise.Api.Auth;

/// <summary>Allows the Hangfire dashboard only for authenticated admins (admin claim on the JWT/PAT).</summary>
public sealed class HangfireDashboardAdminFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var user = context.GetHttpContext().User;
        return user.Identity?.IsAuthenticated == true
            && user.FindFirst("admin")?.Value == "true";
    }
}
