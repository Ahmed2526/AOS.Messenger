using System.Net;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using AOS.Messenger.Web.Controllers;
using AOS.Messenger.Web.Filters;
using AOS.Messenger.Web.Middleware;
using AOS.Messenger.Web.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.RateLimiting;
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
    public void DualIpAndEmailRateLimiterPolicy_AtCapacity_RetainsActiveCountersAndBoundsOverflowWithoutGlobalLockout()
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
        using (var firstLease = retainedLimiter.AttemptAcquire(DualIpAndEmailRateLimiterPolicy.EmailPermitLimitPerMinute - 1))
        {
            Assert.True(firstLease.IsAcquired);
        }

        // Fill both exact maps beyond maxCacheEntries = 4; unseen clean callers still get bounded overflow permits.
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
            Assert.True(churnLease.IsAcquired, $"Clean caller {i} must not suffer a global capacity lockout.");
        }

        // Original hot identity MUST still have retained its 4/5 count and exhaust cleanly on its 6th request.
        using var remainingLease = retainedLimiter.AttemptAcquire(1);
        Assert.True(remainingLease.IsAcquired);
        using var exhaustedLease = retainedLimiter.AttemptAcquire(1);
        Assert.False(exhaustedLease.IsAcquired);

        // An overflow email identity hitting its shard cap is still throttled after 5 permits.
        // Use a dedicated policy with only its exact maps filled so no prior churn occupies overflow shards.
        using var overflowPolicy = new DualIpAndEmailRateLimiterPolicy(maxCacheEntries: 4, clock);
        for (int i = 0; i < 4; i++)
        {
            var seedCtx = new DefaultHttpContext();
            seedCtx.Connection.RemoteIpAddress = IPAddress.Parse($"10.60.0.{i + 1}");
            seedCtx.Request.ContentType = "application/x-www-form-urlencoded";
            seedCtx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
            {
                ["Email"] = $"exact{i}@example.com"
            });

            var seedPart = overflowPolicy.GetPartition(seedCtx);
            using var seedLimiter = seedPart.Factory(seedPart.PartitionKey);
            using var seedLease = seedLimiter.AttemptAcquire(1);
            Assert.True(seedLease.IsAcquired);
        }

        var overflowSpamCtx = new DefaultHttpContext();
        overflowSpamCtx.Connection.RemoteIpAddress = IPAddress.Parse("10.50.2.1");
        overflowSpamCtx.Request.ContentType = "application/x-www-form-urlencoded";
        overflowSpamCtx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["Email"] = "overflow-target@example.com"
        });
        var overflowPartition = overflowPolicy.GetPartition(overflowSpamCtx);
        using var overflowLimiter = overflowPartition.Factory(overflowPartition.PartitionKey);
        for (int i = 0; i < DualIpAndEmailRateLimiterPolicy.EmailPermitLimitPerMinute; i++)
        {
            using var lease = overflowLimiter.AttemptAcquire(1);
            Assert.True(lease.IsAcquired);
        }
        using (var rejectedOverflowLease = overflowLimiter.AttemptAcquire(1))
        {
            Assert.False(rejectedOverflowLease.IsAcquired);
        }

        clock.Advance(TimeSpan.FromMinutes(1));
        using (var afterExpiryLease = retainedLimiter.AttemptAcquire(1))
        {
            Assert.True(afterExpiryLease.IsAcquired);
        }
    }

    [Fact]
    public void DualIpAndEmailRateLimiterPolicy_EmailRejectedAndEmptyEmailRequests_PreserveIpFairnessAndPreventGlobalUnknownBottleneck()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 11, 25, 14, 0, 0, TimeSpan.Zero));
        using var policy = new DualIpAndEmailRateLimiterPolicy(maxCacheEntries: 100, clock);

        // Exhaust target@example.com using attacker IPs.
        for (int i = 0; i < DualIpAndEmailRateLimiterPolicy.EmailPermitLimitPerMinute; i++)
        {
            var atkCtx = new DefaultHttpContext();
            atkCtx.Connection.RemoteIpAddress = IPAddress.Parse($"198.51.100.{i + 1}");
            atkCtx.Request.ContentType = "application/x-www-form-urlencoded";
            atkCtx.Request.Form = new FormCollection(new Dictionary<string, StringValues> { ["Email"] = "target@example.com" });
            var part = policy.GetPartition(atkCtx);
            using var limiter = part.Factory(part.PartitionKey);
            using var lease = limiter.AttemptAcquire(1);
            Assert.True(lease.IsAcquired);
        }

        // Clean IP sends requests targeting already-exhausted email; rejected email must NOT drain clean IP quota.
        var cleanIp = IPAddress.Parse("203.0.113.50");
        for (int i = 0; i < DualIpAndEmailRateLimiterPolicy.IpPermitLimitPerMinute; i++)
        {
            var blockedEmailCtx = new DefaultHttpContext();
            blockedEmailCtx.Connection.RemoteIpAddress = cleanIp;
            blockedEmailCtx.Request.ContentType = "application/x-www-form-urlencoded";
            blockedEmailCtx.Request.Form = new FormCollection(new Dictionary<string, StringValues> { ["Email"] = "target@example.com" });
            var p = policy.GetPartition(blockedEmailCtx);
            using var l = p.Factory(p.PartitionKey);
            using var rejected = l.AttemptAcquire(1);
            Assert.False(rejected.IsAcquired);
        }

        // Same clean IP still has full IP quota when emailing an unthrottled address.
        var otherCtx = new DefaultHttpContext();
        otherCtx.Connection.RemoteIpAddress = cleanIp;
        otherCtx.Request.ContentType = "application/x-www-form-urlencoded";
        otherCtx.Request.Form = new FormCollection(new Dictionary<string, StringValues> { ["Email"] = "fresh@example.com" });
        var freshPart = policy.GetPartition(otherCtx);
        using var freshLimiter = freshPart.Factory(freshPart.PartitionKey);
        using var freshLease = freshLimiter.AttemptAcquire(1);
        Assert.True(freshLease.IsAcquired);

        // Email-less endpoints (e.g. ConfirmEmail/ConfirmRecovery) across >5 distinct IPs must not collide on a global 5/min bucket.
        for (int i = 1; i <= 8; i++)
        {
            var tokenCtx = new DefaultHttpContext();
            tokenCtx.Connection.RemoteIpAddress = IPAddress.Parse($"203.0.113.{100 + i}");
            tokenCtx.Request.ContentType = "application/x-www-form-urlencoded";
            tokenCtx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
            {
                ["UserId"] = Guid.NewGuid().ToString(),
                ["Token"] = "tok"
            });
            var p = policy.GetPartition(tokenCtx);
            Assert.Equal($"203.0.113.{100 + i}", p.PartitionKey);
            using var l = p.Factory(p.PartitionKey);
            using var lease = l.AttemptAcquire(1);
            Assert.True(lease.IsAcquired);
        }

        // Unbuffered form stream (simulating cross-window skip when IsIpCurrentlyExhausted was true) must never trigger sync Request.Form IO.
        var syncIoCtx = new DefaultHttpContext();
        syncIoCtx.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.200");
        syncIoCtx.Request.ContentType = "application/x-www-form-urlencoded";
        syncIoCtx.Request.Body = new ThrowingSyncReadStream();
        var safePart = policy.GetPartition(syncIoCtx);
        using var safeLimiter = safePart.Factory(safePart.PartitionKey);
        using var safeLease = safeLimiter.AttemptAcquire(1);
        Assert.True(safeLease.IsAcquired);
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

        // Advancing into the next minute allows the limiter to re-evaluate against the new window instead of rejecting with null RetryAfter.
        clock.Advance(TimeSpan.FromMinutes(1));
        using var postRolloverLease = rejectedLimiter.AttemptAcquire(1);
        Assert.True(postRolloverLease.IsAcquired);
        var newPartition = policy.GetPartition(spamCtx1);
        using var newLimiter = newPartition.Factory(newPartition.PartitionKey);
        using var freshLease = newLimiter.AttemptAcquire(1);
        Assert.True(freshLease.IsAcquired);
    }

    private sealed class ThrowingSyncReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous operations are disallowed.");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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

    [Fact]
    public void DualIpAndEmailRateLimiterPolicy_EnforcesPolicyScopedKeys_SkipsWhitespace_AndEnforcesDailyCap()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 11, 26, 10, 0, 0, TimeSpan.Zero));
        using var policy = new DualIpAndEmailRateLimiterPolicy(maxCacheEntries: 100, clock);

        // 1. recover-account-limiter ignores decoy/empty Email and targets UsernameOrEmail
        for (int i = 0; i < DualIpAndEmailRateLimiterPolicy.EmailPermitLimitPerMinute; i++)
        {
            var recCtx = new DefaultHttpContext();
            recCtx.Connection.RemoteIpAddress = IPAddress.Parse($"198.51.100.{i + 1}");
            recCtx.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
                new EndpointMetadataCollection(new EnableRateLimitingAttribute("recover-account-limiter")),
                "RecoverAccount"));
            recCtx.Request.ContentType = "application/x-www-form-urlencoded";
            recCtx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
            {
                ["Email"] = i == 0 ? "decoy@example.com" : "   ",
                ["UsernameOrEmail"] = "Victim@Example.COM"
            });

            var part = policy.GetPartition(recCtx);
            Assert.EndsWith("|victim@example.com", part.PartitionKey, StringComparison.Ordinal);
            using var limiter = part.Factory(part.PartitionKey);
            using var lease = limiter.AttemptAcquire(1);
            Assert.True(lease.IsAcquired);
        }

        // 6th request targeting Victim@Example.COM in the same minute from a fresh IP fails
        var recBlocked = new DefaultHttpContext();
        recBlocked.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.77");
        recBlocked.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
            new EndpointMetadataCollection(new EnableRateLimitingAttribute("recover-account-limiter")),
            "RecoverAccount"));
        recBlocked.Request.ContentType = "application/x-www-form-urlencoded";
        recBlocked.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["Email"] = "",
            ["model.UsernameOrEmail"] = "victim@example.com"
        });
        var recBlockedPart = policy.GetPartition(recBlocked);
        using (var limiter = recBlockedPart.Factory(recBlockedPart.PartitionKey))
        using (var lease = limiter.AttemptAcquire(1))
        {
            Assert.False(lease.IsAcquired);
        }

        // 2. Daily email cap (20/day) is enforced across separate minute windows on the same UTC day
        const string dailyTarget = "dailycap@example.com";
        for (int batch = 0; batch < 4; batch++)
        {
            for (int i = 0; i < DualIpAndEmailRateLimiterPolicy.EmailPermitLimitPerMinute; i++)
            {
                var ctx = new DefaultHttpContext();
                ctx.Connection.RemoteIpAddress = IPAddress.Parse($"198.51.101.{i + 10}");
                ctx.Request.ContentType = "application/x-www-form-urlencoded";
                ctx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
                {
                    ["model.Email"] = dailyTarget
                });

                var part = policy.GetPartition(ctx);
                using var limiter = part.Factory(part.PartitionKey);
                using var lease = limiter.AttemptAcquire(1);
                Assert.True(lease.IsAcquired);
            }

            clock.Advance(TimeSpan.FromMinutes(2));
        }

        // 21st attempt on the same UTC day must be rejected with RetryAfter > 1 minute
        var dailyCtx = new DefaultHttpContext();
        dailyCtx.Connection.RemoteIpAddress = IPAddress.Parse("198.51.101.200");
        dailyCtx.Request.ContentType = "application/x-www-form-urlencoded";
        dailyCtx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["Email"] = dailyTarget
        });
        var dailyPart = policy.GetPartition(dailyCtx);
        using (var limiter = dailyPart.Factory(dailyPart.PartitionKey))
        using (var lease = limiter.AttemptAcquire(1))
        {
            Assert.False(lease.IsAcquired);
            Assert.True(lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter));
            Assert.True(retryAfter > TimeSpan.FromMinutes(1));
        }

        // Authoritative check catches an email even when pre-binding GetPartition had an exhausted IP rollover or alias
        Assert.False(policy.TryAcquireAuthoritativeEmail(dailyTarget, out var authRetryAfter));
        Assert.True(authRetryAfter > TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task AuthoritativeEmailRateLimitFilter_RejectsPrefixOrRolloverBypass_WithRetryAfterAndHtml429()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero));
        using var policy = new DualIpAndEmailRateLimiterPolicy(maxCacheEntries: 100, clock);

        for (int i = 0; i < DualIpAndEmailRateLimiterPolicy.EmailPermitLimitPerMinute; i++)
        {
            Assert.True(policy.TryAcquireAuthoritativeEmail("target@example.com", out _));
        }

        var filter = new AuthoritativeEmailRateLimitFilter(policy);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = HttpMethods.Post;
        httpContext.Request.Path = "/Account/Register";
        httpContext.Response.Body = new MemoryStream();

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor(), new ModelStateDictionary());
        var executingContext = new ActionExecutingContext(
            actionContext,
            Array.Empty<IFilterMetadata>(),
            new Dictionary<string, object?>
            {
                ["model"] = new RegisterViewModel { Email = "target@example.com", UserName = "u", DisplayName = "U", Password = "Password123!", ConfirmPassword = "Password123!" }
            },
            controller: new object());

        var nextCalled = false;
        await filter.OnActionExecutionAsync(executingContext, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(actionContext, Array.Empty<IFilterMetadata>(), new object()));
        });

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status429TooManyRequests, httpContext.Response.StatusCode);
        Assert.True(httpContext.Response.Headers.ContainsKey("Retry-After"));
        Assert.Contains("no-store", httpContext.Response.Headers.CacheControl.ToString(), StringComparison.OrdinalIgnoreCase);

        httpContext.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(httpContext.Response.Body, Encoding.UTF8);
        var html = await reader.ReadToEndAsync();
        Assert.Contains("429", html);
    }

    [Fact]
    public async Task RequestSizeLimitEnforcementMiddleware_EnforcesBodyReaderPipeLimit_OnChunkedBodiesWithoutContentLength()
    {
        var middleware = new RequestSizeLimitEnforcementMiddleware(async ctx =>
        {
            var readResult = await ctx.Request.BodyReader.ReadAtLeastAsync(200);
            ctx.Request.BodyReader.AdvanceTo(readResult.Buffer.End);
        });

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = HttpMethods.Post;
        httpContext.Request.Body = new MemoryStream(new byte[256]); // No Content-Length set
        httpContext.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(new RequestSizeLimitAttribute(100)),
            "LimitedEndpoint"));

        var ex = await Assert.ThrowsAsync<BadHttpRequestException>(() => middleware.InvokeAsync(httpContext));
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, ex.StatusCode);
    }

    [Fact]
    public void DualIpAndEmailRateLimiterPolicy_Ipv6PartitionKey_NeverCollidesWithEmailLessIpv6()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 11, 27, 10, 0, 0, TimeSpan.Zero));
        using var policy = new DualIpAndEmailRateLimiterPolicy(maxCacheEntries: 100, clock);

        // Email-less request from 2001:db8::1:abcd
        var emailLessCtx = new DefaultHttpContext();
        emailLessCtx.Connection.RemoteIpAddress = IPAddress.Parse("2001:db8::1:abcd");
        var emailLessPart = policy.GetPartition(emailLessCtx);
        Assert.Equal("2001:db8::1:abcd", emailLessPart.PartitionKey);
        using (var l = emailLessPart.Factory(emailLessPart.PartitionKey))
        using (var lease = l.AttemptAcquire(1))
        {
            Assert.True(lease.IsAcquired);
        }

        // Request from 2001:db8::1 with UsernameOrEmail=abcd must get a distinct partition key and enforce email quota
        for (int i = 0; i < DualIpAndEmailRateLimiterPolicy.EmailPermitLimitPerMinute; i++)
        {
            var idCtx = new DefaultHttpContext();
            idCtx.Connection.RemoteIpAddress = IPAddress.Parse("2001:db8::1");
            idCtx.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
                new EndpointMetadataCollection(new EnableRateLimitingAttribute("recover-account-limiter")),
                "RecoverAccount"));
            idCtx.Request.ContentType = "application/x-www-form-urlencoded";
            idCtx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
            {
                ["UsernameOrEmail"] = "abcd"
            });

            var part = policy.GetPartition(idCtx);
            Assert.Equal("2001:db8::1|abcd", part.PartitionKey);
            Assert.NotEqual(emailLessPart.PartitionKey, part.PartitionKey);
            using var limiter = part.Factory(part.PartitionKey);
            using var lease = limiter.AttemptAcquire(1);
            Assert.True(lease.IsAcquired);
        }

        var blockedCtx = new DefaultHttpContext();
        blockedCtx.Connection.RemoteIpAddress = IPAddress.Parse("2001:db8::2");
        blockedCtx.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
            new EndpointMetadataCollection(new EnableRateLimitingAttribute("recover-account-limiter")),
            "RecoverAccount"));
        blockedCtx.Request.ContentType = "application/x-www-form-urlencoded";
        blockedCtx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["UsernameOrEmail"] = "abcd"
        });
        var blockedPart = policy.GetPartition(blockedCtx);
        using (var blockedLimiter = blockedPart.Factory(blockedPart.PartitionKey))
        using (var rejected = blockedLimiter.AttemptAcquire(1))
        {
            Assert.False(rejected.IsAcquired);
        }
    }
}
