using System.Globalization;
using System.Net;
using System.Text.Json;
using AOS.Messenger.Web.Controllers;
using AOS.Messenger.Web.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AOS.Messenger.Web.Filters;

/// <summary>
/// Defense-in-depth filter that enforces the per-normalized-target-email rate limit against the
/// authoritative MVC-bound model property on email-triggering Account POST actions whenever
/// pre-binding rate-limit form extraction could not observe that exact target (e.g. prefix aliases
/// or minute-window rollover skips).
/// </summary>
public sealed class AuthoritativeEmailRateLimitFilter : IAsyncActionFilter
{
    private readonly DualIpAndEmailRateLimiterPolicy _policy;

    public AuthoritativeEmailRateLimitFilter(DualIpAndEmailRateLimiterPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _policy = policy;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (!HttpMethods.IsPost(context.HttpContext.Request.Method))
        {
            await next();
            return;
        }

        string? rawTarget = null;
        foreach (var arg in context.ActionArguments.Values)
        {
            rawTarget = arg switch
            {
                RegisterViewModel reg => reg.Email,
                ForgotPasswordViewModel forgot => forgot.Email,
                ResetPasswordViewModel reset => reset.Email,
                RecoverAccountViewModel recover => recover.UsernameOrEmail,
                _ => rawTarget
            };

            if (rawTarget is not null)
            {
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(rawTarget))
        {
            await next();
            return;
        }

        var normalizedBoundTarget = DualIpAndEmailRateLimiterPolicy.NormalizePartitionEmail(rawTarget);
        var chargedInMiddleware = context.HttpContext.Items.TryGetValue(
            DualIpAndEmailRateLimiterPolicy.ChargedEmailItemKey,
            out var chargedObj)
            ? chargedObj as string
            : null;

        if (string.Equals(chargedInMiddleware, normalizedBoundTarget, StringComparison.Ordinal))
        {
            await next();
            return;
        }

        if (_policy.TryAcquireAuthoritativeEmail(normalizedBoundTarget, out var retryAfter))
        {
            context.HttpContext.Items[DualIpAndEmailRateLimiterPolicy.ChargedEmailItemKey] = normalizedBoundTarget;
            await next();
            return;
        }

        var httpContext = context.HttpContext;
        httpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        httpContext.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
            .ToString(CultureInfo.InvariantCulture);
        httpContext.Response.Headers.CacheControl = "no-store, no-cache";
        httpContext.Response.Headers.Pragma = "no-cache";

        bool isApiOrJson = httpContext.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
            || httpContext.Request.Headers.Accept.Any(h => h?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true)
            || string.Equals(httpContext.Request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);

        if (isApiOrJson)
        {
            var problemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = "Too Many Requests",
                Detail = "Rate limit exceeded. Please wait before trying again.",
                Instance = httpContext.Request.Path.Value
            };
            problemDetails.Extensions["traceId"] = httpContext.TraceIdentifier;

            httpContext.Response.ContentType = "application/problem+json";
            await httpContext.Response.WriteAsync(JsonSerializer.Serialize(problemDetails), httpContext.RequestAborted);
        }
        else
        {
            httpContext.Response.ContentType = "text/html; charset=utf-8";
            var html = $$"""
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
                        <p class="text-sm text-[#6E6560] dark:text-[#C9C0B9] mb-6">{{WebUtility.HtmlEncode("Rate limit exceeded. Please wait before trying again.")}}</p>
                        <a href="/" class="inline-block px-5 py-2 bg-[#B3929D] hover:bg-[#A07860] text-white rounded-md font-medium text-sm transition-colors">Return Home</a>
                    </div>
                </body>
                </html>
                """;
            await httpContext.Response.WriteAsync(html, httpContext.RequestAborted);
        }
    }
}
