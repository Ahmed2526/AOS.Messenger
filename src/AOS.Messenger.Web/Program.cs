using System.Net;
using System.Security.Claims;
using System.Text.Json;
using AOS.Messenger.Application;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Infrastructure;
using AOS.Messenger.Web.Converters;
using AOS.Messenger.Web.Filters;
using AOS.Messenger.Web.Hubs;
using AOS.Messenger.Web.Middleware;
using AOS.Messenger.Web.RateLimiting;
using AOS.Messenger.Web.Services;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;

var builder = WebApplication.CreateBuilder(args);

// --------------------------------------------------------------------------
// Service registration
// --------------------------------------------------------------------------
builder.Services.AddSingleton(TimeProvider.System);

// Forwarded headers hardening - clear default networks/proxies and trust only configured ones
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
#pragma warning disable ASPDEPR005, CS0618
    options.KnownNetworks.Clear();
#pragma warning restore ASPDEPR005, CS0618
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();

    var knownProxies = builder.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>();
    if (knownProxies is not null)
    {
        foreach (var proxy in knownProxies)
        {
            if (IPAddress.TryParse(proxy, out var ip))
            {
                options.KnownProxies.Add(ip);
            }
        }
    }

    var knownNetworks = builder.Configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>();
    if (knownNetworks is not null)
    {
        foreach (var network in knownNetworks)
        {
            if (System.Net.IPNetwork.TryParse(network, out var ipNetwork))
            {
                options.KnownIPNetworks.Add(ipNetwork);
            }
        }
    }
});

builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "RequestVerificationToken";
});

builder.Services.AddControllersWithViews(options =>
    {
        options.Filters.Add(new Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryTokenAttribute());
        options.Filters.Add(new AuthenticatedNoCacheFilter());
        options.Filters.Add(new AntiforgeryProblemDetailsFilter());
    })
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new MessageIdJsonConverter());
        options.JsonSerializerOptions.Converters.Add(new UserIdJsonConverter());
        options.JsonSerializerOptions.Converters.Add(new ConversationIdJsonConverter());
    });

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        var httpContext = context.HttpContext;
        httpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;

        bool isApiOrJson = httpContext.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
            || httpContext.Request.Headers.Accept.Any(h => h?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true)
            || string.Equals(httpContext.Request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);

        if (!httpContext.Response.HasStarted &&
            (httpContext.User.Identity?.IsAuthenticated == true || isApiOrJson))
        {
            httpContext.Response.Headers.CacheControl = "no-store, no-cache";
            httpContext.Response.Headers.Pragma = "no-cache";
        }

        if (isApiOrJson && !httpContext.Response.HasStarted)
        {
            httpContext.Response.ContentType = "application/problem+json";

            var problemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = "Too Many Requests",
                Detail = "Rate limit exceeded. Please wait before trying again.",
                Instance = httpContext.Request.Path.Value
            };
            problemDetails.Extensions["traceId"] = httpContext.TraceIdentifier;

            try
            {
                await httpContext.Response.WriteAsync(JsonSerializer.Serialize(problemDetails), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || httpContext.RequestAborted.IsCancellationRequested)
            {
            }
        }
    };

    // Partitioned per-client-IP rate limiter for login
    options.AddPolicy("login-limiter", httpContext =>
        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));

    // Dual IP + normalized-target-email rate limiting for email-triggering endpoints
    var dualEmailPolicy = new DualIpAndEmailRateLimiterPolicy();
    options.AddPolicy("email-endpoint-limiter", dualEmailPolicy);
    options.AddPolicy("email-limiter", dualEmailPolicy);
    options.AddPolicy("register-limiter", dualEmailPolicy);
    options.AddPolicy("forgot-password-limiter", dualEmailPolicy);
    options.AddPolicy("recover-account-limiter", dualEmailPolicy);

    options.AddPolicy("search", httpContext =>
        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? httpContext.Connection.RemoteIpAddress?.ToString()
                ?? "anonymous",
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));

    options.AddPolicy("messages", httpContext =>
        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? httpContext.Connection.RemoteIpAddress?.ToString()
                ?? "anonymous",
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));
});

builder.Services.AddSignalR(options =>
    {
        options.MaximumReceiveMessageSize = 16 * 1024; // 16 KB cap as required by AUD-047 and AGENTS.md
        options.AddFilter<SessionAndThrottleHubFilter>();
    })
    .AddJsonProtocol(options =>
    {
        options.PayloadSerializerOptions.Converters.Add(new MessageIdJsonConverter());
        options.PayloadSerializerOptions.Converters.Add(new UserIdJsonConverter());
        options.PayloadSerializerOptions.Converters.Add(new ConversationIdJsonConverter());
    });
builder.Services.AddSingleton<SessionAndThrottleHubFilter>(sp =>
    new SessionAndThrottleHubFilter(
        sp.GetService<TimeProvider>(),
        sp.GetService<ILogger<SessionAndThrottleHubFilter>>()));
builder.Services.AddSingleton<IConnectionTracker, InMemoryConnectionTracker>();
builder.Services.AddSingleton<IOnlinePresenceTracker>(sp => (InMemoryConnectionTracker)sp.GetRequiredService<IConnectionTracker>());
builder.Services.AddSingleton<ISessionConnectionTerminator, SignalRSessionConnectionTerminator>();
builder.Services.AddScoped<IRealtimeNotifier, SignalRRealtimeNotifier>();

builder.Services.AddApplicationServices();
builder.Services.AddInfrastructureServices(builder.Configuration);

// --------------------------------------------------------------------------
// HTTP pipeline
// --------------------------------------------------------------------------
var app = builder.Build();

// Forwarded headers processed early before HTTPS redirection and HSTS
app.UseForwardedHeaders();

// Centralized exception handling as required by AGENTS.md
app.UseMiddleware<ExceptionHandlingMiddleware>();

// Security headers middleware
app.UseSecurityHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();

// Buffer form asynchronously if needed so RateLimiter can inspect form fields without sync I/O exceptions
app.Use(async (context, next) =>
{
    if (context.Request.HasFormContentType && HttpMethods.IsPost(context.Request.Method))
    {
        _ = await context.Request.ReadFormAsync(context.RequestAborted);
    }
    await next();
});

app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

// CSWSH protection: Enforce same-origin Origin header validation on SignalR endpoints
app.UseMiddleware<SignalROriginValidationMiddleware>();

app.MapStaticAssets().AllowAnonymous();
app.MapHub<ChatHub>("/hubs/chat");

app.MapControllerRoute(
        name: "default",
        pattern: "{controller=Chat}/{action=Index}/{id?}")
    .WithStaticAssets();

app.Run();

public partial class Program { }
