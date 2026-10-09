using Hangfire.Dashboard;

namespace ContentManagement.Server.Auth;

/// <summary>
/// Restricts the Hangfire dashboard to signed-in administrators.
/// </summary>
public sealed class AdminDashboardAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();
        return httpContext.User.Identity?.IsAuthenticated == true &&
            httpContext.User.IsInRole("Administrator");
    }
}
