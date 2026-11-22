using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Memory;

namespace AOS.Messenger.Web.RateLimiting;

/// <summary>
/// Rate limiter policy that enforces dual rate limiting:
/// 1. Per-client-IP limit (prevents flooding from a single IP address).
/// 2. Per-normalized-target-email limit (prevents email bombing a specific address across IPs).
/// Enforces bounded memory via MemoryCache with SizeLimit = 50,000, 15-minute sliding expiration,
/// and post-eviction disposal of RateLimiter instances.
/// </summary>
public sealed class DualIpAndEmailRateLimiterPolicy : IRateLimiterPolicy<string>, IDisposable
{
    private readonly MemoryCache _ipCache = new(new MemoryCacheOptions { SizeLimit = 50_000 });
    private readonly MemoryCache _emailCache = new(new MemoryCacheOptions { SizeLimit = 50_000 });
    private readonly object _ipLock = new();
    private readonly object _emailLock = new();

    public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected => null;

    public RateLimitPartition<string> GetPartition(HttpContext httpContext)
    {
        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
        var email = ExtractNormalizedEmail(httpContext);

        var partitionKey = $"{ip}:{email}";

        return RateLimitPartition.Get(partitionKey, _ =>
        {
            var ipLimiter = GetOrCreateIpLimiter(ip);
            var emailLimiter = GetOrCreateEmailLimiter(email);

            return new DualRateLimiter(ipLimiter, emailLimiter);
        });
    }

    private RateLimiter GetOrCreateIpLimiter(string ip)
    {
        lock (_ipLock)
        {
            if (_ipCache.TryGetValue(ip, out RateLimiter? limiter) && limiter is not null)
            {
                return limiter;
            }

            limiter = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            });

            var options = new MemoryCacheEntryOptions
            {
                Size = 1,
                SlidingExpiration = TimeSpan.FromMinutes(15)
            };
            options.RegisterPostEvictionCallback(EvictionCallback);

            _ipCache.Set(ip, limiter, options);
            return limiter;
        }
    }

    private RateLimiter GetOrCreateEmailLimiter(string email)
    {
        lock (_emailLock)
        {
            if (_emailCache.TryGetValue(email, out RateLimiter? limiter) && limiter is not null)
            {
                return limiter;
            }

            limiter = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            });

            var options = new MemoryCacheEntryOptions
            {
                Size = 1,
                SlidingExpiration = TimeSpan.FromMinutes(15)
            };
            options.RegisterPostEvictionCallback(EvictionCallback);

            _emailCache.Set(email, limiter, options);
            return limiter;
        }
    }

    private static void EvictionCallback(object key, object? value, EvictionReason reason, object? state)
    {
        if (value is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private static string ExtractNormalizedEmail(HttpContext context)
    {
        if (context.Request.HasFormContentType)
        {
            var form = context.Request.Form;
            string? email = form["Email"].FirstOrDefault()
                ?? form["email"].FirstOrDefault()
                ?? form["usernameOrEmail"].FirstOrDefault()
                ?? form["UsernameOrEmail"].FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(email))
            {
                return email.Trim().ToLowerInvariant();
            }
        }

        if (context.Request.Query.TryGetValue("email", out var queryEmail) && !string.IsNullOrWhiteSpace(queryEmail))
        {
            return queryEmail.ToString().Trim().ToLowerInvariant();
        }

        return "unknown";
    }

    public void Dispose()
    {
        _ipCache.Dispose();
        _emailCache.Dispose();
    }

    private sealed class DualRateLimiter : RateLimiter
    {
        private readonly RateLimiter _ipLimiter;
        private readonly RateLimiter _emailLimiter;

        public DualRateLimiter(RateLimiter ipLimiter, RateLimiter emailLimiter)
        {
            _ipLimiter = ipLimiter;
            _emailLimiter = emailLimiter;
        }

        public override TimeSpan? IdleDuration => null;

        public override RateLimiterStatistics? GetStatistics() => null;

        protected override RateLimitLease AttemptAcquireCore(int permitCount)
        {
            var ipLease = _ipLimiter.AttemptAcquire(permitCount);
            if (!ipLease.IsAcquired)
            {
                return ipLease;
            }

            var emailLease = _emailLimiter.AttemptAcquire(permitCount);
            if (!emailLease.IsAcquired)
            {
                ipLease.Dispose();
                return emailLease;
            }

            return new CombinedRateLimitLease(ipLease, emailLease);
        }

        protected override async ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
        {
            var ipLease = await _ipLimiter.AcquireAsync(permitCount, cancellationToken);
            if (!ipLease.IsAcquired)
            {
                return ipLease;
            }

            RateLimitLease emailLease;
            try
            {
                emailLease = await _emailLimiter.AcquireAsync(permitCount, cancellationToken);
            }
            catch
            {
                ipLease.Dispose();
                throw;
            }

            if (!emailLease.IsAcquired)
            {
                ipLease.Dispose();
                return emailLease;
            }

            return new CombinedRateLimitLease(ipLease, emailLease);
        }

        private sealed class CombinedRateLimitLease : RateLimitLease
        {
            private readonly RateLimitLease _lease1;
            private readonly RateLimitLease _lease2;
            private int _disposed;

            public CombinedRateLimitLease(RateLimitLease lease1, RateLimitLease lease2)
            {
                _lease1 = lease1;
                _lease2 = lease2;
            }

            public override bool IsAcquired => true;

            public override IEnumerable<string> MetadataNames => _lease1.MetadataNames.Concat(_lease2.MetadataNames);

            public override bool TryGetMetadata(string metadataName, out object? metadata)
            {
                return _lease1.TryGetMetadata(metadataName, out metadata) || _lease2.TryGetMetadata(metadataName, out metadata);
            }

            protected override void Dispose(bool disposing)
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0 && disposing)
                {
                    _lease1.Dispose();
                    _lease2.Dispose();
                }
            }
        }
    }
}
