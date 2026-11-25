using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
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
            "default-src 'self'; script-src 'self'; style-src 'self'; font-src 'self'; img-src 'self' blob:; media-src 'self' blob:; connect-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'",
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

    [Fact]
    public void DualIpAndEmailRateLimiterPolicy_AfterCacheEviction_RecreatesBucketsWithoutObjectDisposedException()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero));
        using var policy = new DualIpAndEmailRateLimiterPolicy(maxCacheEntries: 4, clock);

        var firstCtx = new DefaultHttpContext();
        firstCtx.Connection.RemoteIpAddress = IPAddress.Parse("10.50.0.1");
        firstCtx.Request.ContentType = "application/x-www-form-urlencoded";
        firstCtx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["Email"] = "first@example.com"
        });

        var firstPartition = policy.GetPartition(firstCtx);
        using var retainedLimiter = firstPartition.Factory(firstPartition.PartitionKey);
        using (var firstLease = retainedLimiter.AttemptAcquire(1))
        {
            Assert.True(firstLease.IsAcquired);
        }

        // Force cache eviction beyond maxCacheEntries = 4 for both IPs and emails.
        for (int i = 0; i < 12; i++)
        {
            var churnCtx = new DefaultHttpContext();
            churnCtx.Connection.RemoteIpAddress = IPAddress.Parse($"10.50.1.{i}");
            churnCtx.Request.ContentType = "application/x-www-form-urlencoded";
            churnCtx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
            {
                ["Email"] = $"churn{i}@example.com"
            });

            var part = policy.GetPartition(churnCtx);
            using var churnLimiter = part.Factory(part.PartitionKey);
            using var churnLease = churnLimiter.AttemptAcquire(1);
            Assert.True(churnLease.IsAcquired);
        }

        // Re-acquiring on the retained DualRateLimiter must NOT throw ObjectDisposedException.
        using (var postEvictionLease = retainedLimiter.AttemptAcquire(1))
        {
            Assert.True(postEvictionLease.IsAcquired);
        }

        // Advancing beyond 15-minute sliding expiration + 1-minute window also recreates safely.
        clock.Advance(TimeSpan.FromMinutes(16));
        using (var afterExpiryLease = retainedLimiter.AttemptAcquire(1))
        {
            Assert.True(afterExpiryLease.IsAcquired);
        }
    }

    [Fact]
    public void DualIpAndEmailRateLimiterPolicy_ReportsIdleDuration_AndCollapsesExhaustedIpEmailKeys()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 11, 25, 13, 0, 0, TimeSpan.Zero));
        using var policy = new DualIpAndEmailRateLimiterPolicy(maxCacheEntries: 100, clock);
        var clientIp = IPAddress.Parse("172.20.0.9");

        RateLimiter? lastLimiter = null;
        for (int i = 0; i < DualIpAndEmailRateLimiterPolicy.IpPermitLimitPerMinute; i++)
        {
            var ctx = new DefaultHttpContext();
            ctx.Connection.RemoteIpAddress = clientIp;
            ctx.Request.ContentType = "application/x-www-form-urlencoded";
            ctx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
            {
                ["Email"] = $"u{i}@example.com"
            });

            var part = policy.GetPartition(ctx);
            var limiter = part.Factory(part.PartitionKey);
            using var lease = limiter.AttemptAcquire(1);
            Assert.True(lease.IsAcquired);
            lastLimiter = limiter;
        }

        Assert.NotNull(lastLimiter);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(TimeSpan.FromSeconds(10), lastLimiter.IdleDuration);
        lastLimiter.Dispose();

        // Once IP is exhausted in the current window, arbitrary distinct emails map to a single per-IP/window partition key.
        var spamCtx1 = new DefaultHttpContext();
        spamCtx1.Connection.RemoteIpAddress = clientIp;
        spamCtx1.Request.ContentType = "application/x-www-form-urlencoded";
        spamCtx1.Request.Form = new FormCollection(new Dictionary<string, StringValues> { ["Email"] = "random1@example.com" });

        var spamCtx2 = new DefaultHttpContext();
        spamCtx2.Connection.RemoteIpAddress = clientIp;
        spamCtx2.Request.ContentType = "application/x-www-form-urlencoded";
        spamCtx2.Request.Form = new FormCollection(new Dictionary<string, StringValues> { ["Email"] = "random2@example.com" });

        var spamPart1 = policy.GetPartition(spamCtx1);
        var spamPart2 = policy.GetPartition(spamCtx2);
        Assert.Equal(spamPart1.PartitionKey, spamPart2.PartitionKey);
        Assert.StartsWith("ip-exhausted:", spamPart1.PartitionKey, StringComparison.Ordinal);

        using var rejectedLimiter = spamPart1.Factory(spamPart1.PartitionKey);
        using var rejectedLease = rejectedLimiter.AttemptAcquire(1);
        Assert.False(rejectedLease.IsAcquired);
        Assert.True(rejectedLease.TryGetMetadata(MetadataName.RetryAfter.Name, out _));
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public TestTimeProvider(DateTimeOffset initialUtcNow)
        {
            _utcNow = initialUtcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }

    [Fact]
    public async Task SecurityHeadersOptions_AndMiddleware_DeriveAndEmitStrictR2CspOrigin()
    {
        Assert.Same(SecurityHeadersOptions.Default, SecurityHeadersOptions.FromServiceUrl(null));
        Assert.Same(SecurityHeadersOptions.Default, SecurityHeadersOptions.FromServiceUrl("   "));
        Assert.Null(SecurityHeadersOptions.Default.R2Origin);

        var opts = SecurityHeadersOptions.FromServiceUrl("https://acct123.r2.cloudflarestorage.com:443/");
        Assert.Equal("https://acct123.r2.cloudflarestorage.com", opts.R2Origin);
        Assert.Equal(
            "default-src 'self'; script-src 'self'; style-src 'self'; font-src 'self'; img-src 'self' blob: https://acct123.r2.cloudflarestorage.com; media-src 'self' blob: https://acct123.r2.cloudflarestorage.com; connect-src 'self' https://acct123.r2.cloudflarestorage.com; object-src 'none'; base-uri 'self'; frame-ancestors 'none'",
            opts.ContentSecurityPolicy);

        foreach (var badUrl in new[]
        {
            "http://acct123.r2.cloudflarestorage.com",
            "https://*.r2.cloudflarestorage.com",
            "https://acct123.r2.cloudflarestorage.com/bucket",
            "https://acct123.r2.cloudflarestorage.com?foo=bar",
            "https://user:pass@acct123.r2.cloudflarestorage.com",
            "https://acct123.r2.cloudflarestorage.com;evil"
        })
        {
            Assert.Throws<InvalidOperationException>(() => SecurityHeadersOptions.FromServiceUrl(badUrl));
        }

        var context = new DefaultHttpContext();
        var middleware = new SecurityHeadersMiddleware(_ => Task.CompletedTask, opts);
        await middleware.InvokeAsync(context);

        Assert.Equal(opts.ContentSecurityPolicy, context.Response.Headers["Content-Security-Policy"]);
    }
}
