using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.RateLimiting;
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
builder.Services.AddSingleton(SecurityHeadersOptions.FromConfiguration(builder.Configuration));

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
        options.Filters.Add<AuthoritativeEmailRateLimitFilter>();
    })
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new MessageIdJsonConverter());
        options.JsonSerializerOptions.Converters.Add(new UserIdJsonConverter());
        options.JsonSerializerOptions.Converters.Add(new ConversationIdJsonConverter());
        options.JsonSerializerOptions.Converters.Add(new MediaIdJsonConverter());
        options.JsonSerializerOptions.Converters.Add(new AvatarUploadIdJsonConverter());
        options.JsonSerializerOptions.Converters.Add(new StickerPackIdJsonConverter());
        options.JsonSerializerOptions.Converters.Add(new StickerIdJsonConverter());
    });

builder.Services.AddSingleton(sp =>
    new DualIpAndEmailRateLimiterPolicy(timeProvider: sp.GetRequiredService<TimeProvider>()));

builder.Services.AddOptions<RateLimiterOptions>()
    .Configure<DualIpAndEmailRateLimiterPolicy>((options, dualEmailPolicy) =>
    {
        foreach (var policyName in DualIpAndEmailRateLimiterPolicy.RegisteredPolicyNames)
        {
            options.AddPolicy(policyName, dualEmailPolicy);
        }
    });

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        var httpContext = context.HttpContext;
        httpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;

        if (!httpContext.Response.HasStarted
            && context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            var retrySeconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
            httpContext.Response.Headers.RetryAfter = retrySeconds.ToString(CultureInfo.InvariantCulture);
        }

        bool isApiOrJson = httpContext.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
            || httpContext.Request.Headers.Accept.Any(h => h?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true)
            || string.Equals(httpContext.Request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);

        if (!httpContext.Response.HasStarted &&
            (httpContext.User.Identity?.IsAuthenticated == true || isApiOrJson))
        {
            httpContext.Response.Headers.CacheControl = "no-store, no-cache";
            httpContext.Response.Headers.Pragma = "no-cache";
        }

        if (!httpContext.Response.HasStarted)
        {
            try
            {
                if (isApiOrJson)
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

                    await httpContext.Response.WriteAsync(JsonSerializer.Serialize(problemDetails), cancellationToken);
                }
                else
                {
                    httpContext.Response.ContentType = "text/html; charset=utf-8";
                    var html = """
                        <!DOCTYPE html>
                        <html lang="en">
                        <head>
                            <meta charset="utf-8" />
                            <meta name="viewport" content="width=device-width, initial-scale=1.0" />
                            <title>429 - Too Many Requests</title>
                            <link rel="stylesheet" href="/css/app.css" />
                        </head>
                        <body class="min-h-screen flex items-center justify-center p-4 bg-[#F8F6F3] dark:bg-[#302F2C] text-[#2B2725] dark:text-[#F0E9E2] font-sans">
                            <div class="bg-[#FFFFFF] dark:bg-[#3A3835] border border-[#E3DCD7] dark:border-[#514D48] rounded-xl p-8 max-w-md w-full shadow-lg text-center">
                                <div class="text-4xl font-bold text-[#B3929D] mb-2"><h1>429</h1></div>
                                <div class="text-lg font-semibold mb-2"><h2>Too Many Requests</h2></div>
                                <p class="text-sm text-[#6E6560] dark:text-[#C9C0B9] mb-6">Rate limit exceeded. Please wait before trying again.</p>
                                <a href="/" class="inline-block px-5 py-2 bg-[#B3929D] hover:bg-[#A07860] text-white rounded-md font-medium text-sm transition-colors">Return Home</a>
                            </div>
                        </body>
                        </html>
                        """;
                    await httpContext.Response.WriteAsync(html, cancellationToken);
                }
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

    options.AddPolicy("messages-read", httpContext =>
        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? httpContext.Connection.RemoteIpAddress?.ToString()
                ?? "anonymous",
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));

    options.AddPolicy("receipts", httpContext =>
        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? httpContext.Connection.RemoteIpAddress?.ToString()
                ?? "anonymous",
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = 180,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));

    options.AddPolicy("presence", httpContext =>
        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? httpContext.Connection.RemoteIpAddress?.ToString()
                ?? "anonymous",
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = 180,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));

    options.AddPolicy("media-upload", httpContext =>
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

    options.AddPolicy("media-cancel", httpContext =>
        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? httpContext.Connection.RemoteIpAddress?.ToString()
                ?? "anonymous",
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));

    options.AddPolicy("media-download", httpContext =>
        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? httpContext.Connection.RemoteIpAddress?.ToString()
                ?? "anonymous",
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = 240,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));

    options.AddPolicy("avatar-download", httpContext =>
        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? httpContext.Connection.RemoteIpAddress?.ToString()
                ?? "anonymous",
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = 300,
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
        options.PayloadSerializerOptions.Converters.Add(new MediaIdJsonConverter());
        options.PayloadSerializerOptions.Converters.Add(new AvatarUploadIdJsonConverter());
        options.PayloadSerializerOptions.Converters.Add(new StickerPackIdJsonConverter());
        options.PayloadSerializerOptions.Converters.Add(new StickerIdJsonConverter());
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

app.UseAuthentication();
app.UseMiddleware<RequestSizeLimitEnforcementMiddleware>();

// Buffer small forms asynchronously only for dual IP+Email rate-limited POST endpoints after RequestSizeLimit
// enforcement and when the IP isn't already exhausted, avoiding global pre-auth/unbounded form buffering.
app.Use(async (context, next) =>
{
    var dualEmailPolicy = context.RequestServices.GetRequiredService<DualIpAndEmailRateLimiterPolicy>();
    if (HttpMethods.IsPost(context.Request.Method)
        && context.Request.HasFormContentType
        && DualIpAndEmailRateLimiterPolicy.RequiresFormEmailExtraction(context.GetEndpoint())
        && !dualEmailPolicy.IsIpCurrentlyExhausted(context))
    {
        try
        {
            _ = await context.Request.ReadFormAsync(DualIpAndEmailRateLimiterPolicy.AuthFormParsingOptions, context.RequestAborted);
        }
        catch (InvalidDataException ex)
        {
            throw new BadHttpRequestException("Invalid form data.", StatusCodes.Status400BadRequest, ex);
        }
    }

    await next();
});

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
