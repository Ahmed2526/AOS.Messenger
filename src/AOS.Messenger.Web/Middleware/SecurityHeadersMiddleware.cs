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
        ApplySecurityHeaders(context.Response);

        await _next(context);
    }

    public static void ApplySecurityHeaders(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["X-Frame-Options"] = "DENY";
        response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; font-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'";
        response.Headers["Permissions-Policy"] = "camera=(), microphone=(self), geolocation=(), payment=(), usb=()";
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
