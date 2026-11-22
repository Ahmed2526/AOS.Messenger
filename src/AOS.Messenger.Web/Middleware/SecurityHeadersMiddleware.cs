namespace AOS.Messenger.Web.Middleware;

/// <summary>
/// Security headers middleware that sets defensive HTTP response headers
/// including CSP, framing controls, MIME sniffing protection, referrer policy,
/// and Permissions-Policy.
/// </summary>
public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["X-Frame-Options"] = "DENY";
        context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; font-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'";
        context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(self), geolocation=(), payment=(), usb=()";

        await _next(context);
    }
}

/// <summary>
/// Extension methods for registering <see cref="SecurityHeadersMiddleware"/> in the HTTP pipeline.
/// </summary>
public static class SecurityHeadersMiddlewareExtensions
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app)
    {
        return app.UseMiddleware<SecurityHeadersMiddleware>();
    }
}
