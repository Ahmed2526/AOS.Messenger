using Microsoft.AspNetCore.Mvc.Filters;

namespace AOS.Messenger.Web.Filters;

/// <summary>
/// Emits Cache-Control: no-store, no-cache and Pragma: no-cache on dynamic authenticated Razor views and JSON endpoints.
/// Does not apply to static assets served via MapStaticAssets().
/// </summary>
public sealed class AuthenticatedNoCacheFilter : IActionFilter, IResultFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
        ApplyCacheHeaders(context.HttpContext);
    }

    public void OnActionExecuted(ActionExecutedContext context)
    {
    }

    public void OnResultExecuting(ResultExecutingContext context)
    {
        ApplyCacheHeaders(context.HttpContext);
    }

    public void OnResultExecuted(ResultExecutedContext context)
    {
    }

    private static void ApplyCacheHeaders(HttpContext httpContext)
    {
        if (httpContext.User.Identity?.IsAuthenticated == true)
        {
            httpContext.Response.Headers.CacheControl = "no-store, no-cache";
            httpContext.Response.Headers.Pragma = "no-cache";
        }
    }
}
