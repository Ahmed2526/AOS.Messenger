using System.Net;
using System.Security.Claims;
using AOS.Messenger.Web.Filters;
using AOS.Messenger.Web.Middleware;
using AOS.Messenger.Web.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace AOS.Messenger.Tests;

public sealed class WebSecurityHardeningTests
{
    [Fact]
    public async Task SecurityHeadersMiddleware_EmitsRequiredHeaders()
    {
        var context = new DefaultHttpContext();
        RequestDelegate next = _ => Task.CompletedTask;

        var middleware = new SecurityHeadersMiddleware(next);
        await middleware.InvokeAsync(context);

        Assert.Equal("nosniff", context.Response.Headers["X-Content-Type-Options"]);
        Assert.Equal("DENY", context.Response.Headers["X-Frame-Options"]);
        Assert.Equal("strict-origin-when-cross-origin", context.Response.Headers["Referrer-Policy"]);
        Assert.Equal(
            "default-src 'self'; script-src 'self'; style-src 'self'; font-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'",
            context.Response.Headers["Content-Security-Policy"]);
        Assert.Equal(
            "camera=(), microphone=(self), geolocation=(), payment=(), usb=()",
            context.Response.Headers["Permissions-Policy"]);

        var permissionsPolicy = context.Response.Headers["Permissions-Policy"].ToString();
        Assert.DoesNotContain("run-ad-auction", permissionsPolicy);
        Assert.DoesNotContain("join-ad-interest-group", permissionsPolicy);
    }

    [Fact]
    public void AuthenticatedNoCacheFilter_WhenUserIsAuthenticated_SetsNoCacheHeaders()
    {
        var filter = new AuthenticatedNoCacheFilter();
        var httpContext = new DefaultHttpContext();
        var identity = new ClaimsIdentity("TestAuth");
        identity.AddClaim(new Claim(ClaimTypes.Name, "Alice"));
        httpContext.User = new ClaimsPrincipal(identity);

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var context = new ActionExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            new Dictionary<string, object?>(),
            new object());

        filter.OnActionExecuting(context);

        Assert.Equal("no-store, no-cache", httpContext.Response.Headers.CacheControl.ToString());
        Assert.Equal("no-cache", httpContext.Response.Headers.Pragma.ToString());
    }

    [Fact]
    public void AuthenticatedNoCacheFilter_WhenUserIsAnonymous_DoesNotSetCacheHeaders()
    {
        var filter = new AuthenticatedNoCacheFilter();
        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity()); // Unauthenticated

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var context = new ActionExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            new Dictionary<string, object?>(),
            new object());

        filter.OnActionExecuting(context);

        Assert.Empty(httpContext.Response.Headers.CacheControl.ToString());
        Assert.Empty(httpContext.Response.Headers.Pragma.ToString());
    }

    [Fact]
    public void DualIpAndEmailRateLimiterPolicy_PartitionsByIpAndNormalizedEmail()
    {
        var policy = new DualIpAndEmailRateLimiterPolicy();
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.100");
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["Email"] = "  Alice.Smith@Example.COM  "
        });

        var partition = policy.GetPartition(context);

        // Partition factory returns a DualRateLimiter instance
        var limiter = partition.Factory("192.168.1.100:alice.smith@example.com");
        Assert.NotNull(limiter);

        // First attempt should succeed
        using var lease = limiter.AttemptAcquire(1);
        Assert.True(lease.IsAcquired);
    }

    [Fact]
    public void DualIpAndEmailRateLimiterPolicy_EnforcesPerEmailLimitAcrossDifferentIps()
    {
        var policy = new DualIpAndEmailRateLimiterPolicy();
        const string targetEmail = "victim@example.com";

        // Try 5 requests from 5 different IPs for the same target email (limit is 5 per email)
        for (int i = 1; i <= 5; i++)
        {
            var ctx = new DefaultHttpContext();
            ctx.Connection.RemoteIpAddress = IPAddress.Parse($"10.0.0.{i}");
            ctx.Request.ContentType = "application/x-www-form-urlencoded";
            ctx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
            {
                ["Email"] = targetEmail
            });

            var partition = policy.GetPartition(ctx);
            var limiter = partition.Factory($"10.0.0.{i}:{targetEmail}");
            using var lease = limiter.AttemptAcquire(1);
            Assert.True(lease.IsAcquired, $"Attempt {i} from distinct IP should acquire permit.");
        }

        // 6th request from a 6th IP for the SAME email must be rejected!
        var ctx6 = new DefaultHttpContext();
        ctx6.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.6");
        ctx6.Request.ContentType = "application/x-www-form-urlencoded";
        ctx6.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["Email"] = targetEmail
        });

        var partition6 = policy.GetPartition(ctx6);
        var limiter6 = partition6.Factory($"10.0.0.6:{targetEmail}");
        using var lease6 = limiter6.AttemptAcquire(1);
        Assert.False(lease6.IsAcquired, "6th attempt for the same email must be throttled regardless of client IP.");
    }

    [Fact]
    public void DualIpAndEmailRateLimiterPolicy_EnforcesPerIpLimitAcrossDifferentEmails()
    {
        var policy = new DualIpAndEmailRateLimiterPolicy();
        var clientIp = IPAddress.Parse("172.16.0.5");

        // Try 10 requests from the same IP for 10 different emails (limit is 10 per IP)
        for (int i = 1; i <= 10; i++)
        {
            var ctx = new DefaultHttpContext();
            ctx.Connection.RemoteIpAddress = clientIp;
            ctx.Request.ContentType = "application/x-www-form-urlencoded";
            ctx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
            {
                ["Email"] = $"user{i}@example.com"
            });

            var partition = policy.GetPartition(ctx);
            var limiter = partition.Factory($"{clientIp}:user{i}@example.com");
            using var lease = limiter.AttemptAcquire(1);
            Assert.True(lease.IsAcquired, $"Attempt {i} from IP {clientIp} should succeed.");
        }

        // 11th request from the same IP (even with a new email) must be rejected!
        var ctx11 = new DefaultHttpContext();
        ctx11.Connection.RemoteIpAddress = clientIp;
        ctx11.Request.ContentType = "application/x-www-form-urlencoded";
        ctx11.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["Email"] = "user11@example.com"
        });

        var partition11 = policy.GetPartition(ctx11);
        var limiter11 = partition11.Factory($"{clientIp}:user11@example.com");
        using var lease11 = limiter11.AttemptAcquire(1);
        Assert.False(lease11.IsAcquired, "11th attempt from same IP must be throttled regardless of different target email.");
    }
}
