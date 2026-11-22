namespace AOS.Messenger.Web.Middleware;

/// <summary>
/// Enforces strict same-origin Origin header validation on SignalR endpoints (/hubs/chat)
/// to defend against Cross-Site WebSocket Hijacking (CSWSH).
/// </summary>
public sealed class SignalROriginValidationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<SignalROriginValidationMiddleware> _logger;
    private readonly string? _configuredAllowedOrigin;

    public SignalROriginValidationMiddleware(
        RequestDelegate next,
        ILogger<SignalROriginValidationMiddleware> logger,
        IConfiguration? configuration = null)
    {
        _next = next;
        _logger = logger;
        _configuredAllowedOrigin = configuration?["SignalR:AllowedOrigin"]
            ?? configuration?["PublicBaseUrl"];
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/hubs/chat", StringComparison.OrdinalIgnoreCase))
        {
            if (context.Request.Headers.TryGetValue("Origin", out var originValues))
            {
                var origin = originValues.ToString();
                if (!string.IsNullOrWhiteSpace(origin) && !IsValidOrigin(context, origin))
                {
                    _logger.LogWarning(
                        "CSWSH protection: rejected cross-origin SignalR request to {Path} with origin {Origin}",
                        context.Request.Path,
                        origin);

                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
            }
        }

        await _next(context);
    }

    private bool IsValidOrigin(HttpContext context, string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri))
        {
            return false;
        }

        // 1. Exact string match against request scheme + authority
        var requestOrigin = $"{context.Request.Scheme}://{context.Request.Host.Value}";
        if (string.Equals(origin.TrimEnd('/'), requestOrigin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 2. Component match against request scheme and host
        var requestHost = context.Request.Host;
        if (string.Equals(originUri.Scheme, context.Request.Scheme, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(originUri.Host, requestHost.Host, StringComparison.OrdinalIgnoreCase))
        {
            if (requestHost.Port.HasValue)
            {
                if (originUri.Port == requestHost.Port.Value)
                {
                    return true;
                }
            }
            else if (originUri.IsDefaultPort)
            {
                return true;
            }
        }

        // 3. Match against configured allowed origin / PublicBaseUrl
        if (!string.IsNullOrWhiteSpace(_configuredAllowedOrigin) &&
            Uri.TryCreate(_configuredAllowedOrigin, UriKind.Absolute, out var allowedUri))
        {
            if (string.Equals(originUri.Scheme, allowedUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(originUri.Host, allowedUri.Host, StringComparison.OrdinalIgnoreCase) &&
                originUri.Port == allowedUri.Port)
            {
                return true;
            }
        }

        return false;
    }
}
