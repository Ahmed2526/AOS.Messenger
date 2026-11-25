using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Memory;

namespace AOS.Messenger.Web.RateLimiting;

/// <summary>
/// Rate limiter policy that enforces dual rate limiting:
/// 1. Per-client-IP limit (prevents flooding from a single IP address).
/// 2. Per-normalized-target-email limit (prevents email bombing a specific address across IPs).
/// Enforces bounded memory via MemoryCache with SizeLimit = 50,000, 15-minute sliding expiration,
/// non-disposable bucket state (immune to post-eviction ObjectDisposedException), and active
/// <see cref="RateLimiter.IdleDuration"/> reporting so <see cref="PartitionedRateLimiter"/>
/// reclaims idle partition entries.
/// </summary>
public sealed class DualIpAndEmailRateLimiterPolicy : IRateLimiterPolicy<string>, IDisposable
{
    public const int DefaultMaxCacheEntries = 50_000;
    public const int IpPermitLimitPerMinute = 10;
    public const int EmailPermitLimitPerMinute = 5;
    private const int MaxPartitionEmailLength = 254;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CacheSlidingExpiration = TimeSpan.FromMinutes(15);

    public static readonly string[] RegisteredPolicyNames =
    [
        "email-endpoint-limiter",
        "email-limiter",
        "register-limiter",
        "forgot-password-limiter",
        "recover-account-limiter"
    ];

    private static readonly HashSet<string> PolicyNameSet = new(RegisteredPolicyNames, StringComparer.OrdinalIgnoreCase);

    public static readonly FormOptions AuthFormParsingOptions = new()
    {
        ValueCountLimit = 16,
        KeyLengthLimit = 128,
        ValueLengthLimit = 16 * 1024,
        MultipartBodyLengthLimit = 16 * 1024,
        MemoryBufferThreshold = 16 * 1024
    };

    private readonly MemoryCache _ipCache;
    private readonly MemoryCache _emailCache;
    private readonly object _ipLock = new();
    private readonly object _emailLock = new();
    private readonly TimeProvider _timeProvider;
    private int _disposed;

    public DualIpAndEmailRateLimiterPolicy()
        : this(DefaultMaxCacheEntries, null)
    {
    }

    public DualIpAndEmailRateLimiterPolicy(int maxCacheEntries = DefaultMaxCacheEntries, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCacheEntries);

        _ipCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = maxCacheEntries });
        _emailCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = maxCacheEntries });
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected => null;

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public static bool RequiresFormEmailExtraction(Endpoint? endpoint)
    {
        var policyName = endpoint?.Metadata
            .GetOrderedMetadata<EnableRateLimitingAttribute>()
            .LastOrDefault()?
            .PolicyName;

        return policyName is not null && PolicyNameSet.Contains(policyName);
    }

    public bool IsIpCurrentlyExhausted(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        if (IsDisposed)
        {
            return false;
        }

        var ip = ResolveClientIp(httpContext);
        return _ipCache.TryGetValue(ip, out FixedWindowBucket? ipBucket)
            && ipBucket is not null
            && ipBucket.IsExhaustedInCurrentWindow(out _);
    }

    public RateLimitPartition<string> GetPartition(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        var ip = ResolveClientIp(httpContext);

        // If the client IP is already exhausted in the current fixed window, route all emails from
        // this IP to a single per-IP/per-window partition key instead of allocating unbounded ip:email keys.
        if (_ipCache.TryGetValue(ip, out FixedWindowBucket? ipBucket)
            && ipBucket is not null
            && ipBucket.IsExhaustedInCurrentWindow(out var exhaustedWindowId))
        {
            var exhaustedKey = $"ip-exhausted:{ip}:{exhaustedWindowId}";
            return RateLimitPartition.Get(
                exhaustedKey,
                _ => new DualRateLimiter(this, ip, email: string.Empty, _timeProvider, exhaustedWindowId));
        }

        var email = ExtractNormalizedEmail(httpContext);
        var partitionKey = $"{ip}:{email}";

        return RateLimitPartition.Get(
            partitionKey,
            _ => new DualRateLimiter(this, ip, email, _timeProvider, exhaustedWindowId: null));
    }

    private FixedWindowBucket GetOrCreateIpBucket(string ip) =>
        GetOrCreateBucket(_ipCache, _ipLock, ip, IpPermitLimitPerMinute);

    private FixedWindowBucket GetOrCreateEmailBucket(string email) =>
        GetOrCreateBucket(_emailCache, _emailLock, email, EmailPermitLimitPerMinute);

    private FixedWindowBucket GetOrCreateBucket(MemoryCache cache, object gate, string key, int permitLimit)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        if (cache.TryGetValue(key, out FixedWindowBucket? bucket) && bucket is not null)
        {
            return bucket;
        }

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);

            if (cache.TryGetValue(key, out bucket) && bucket is not null)
            {
                return bucket;
            }

            bucket = new FixedWindowBucket(permitLimit, Window, _timeProvider);
            cache.Set(key, bucket, new MemoryCacheEntryOptions
            {
                Size = 1,
                SlidingExpiration = CacheSlidingExpiration
            });
            return bucket;
        }
    }

    private static string ResolveClientIp(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "anonymous";

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
                return NormalizePartitionEmail(email);
            }
        }

        if (context.Request.Query.TryGetValue("email", out var queryEmail) && !string.IsNullOrWhiteSpace(queryEmail))
        {
            return NormalizePartitionEmail(queryEmail.ToString());
        }

        return "unknown";
    }

    private static string NormalizePartitionEmail(string rawEmail)
    {
        var normalized = rawEmail.Trim().ToLowerInvariant();
        if (normalized.Length <= MaxPartitionEmailLength)
        {
            return normalized;
        }

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return $"sha256:{Convert.ToHexString(hashBytes)}";
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _ipCache.Dispose();
            _emailCache.Dispose();
        }
    }

    private sealed class FixedWindowBucket
    {
        private readonly object _gate = new();
        private readonly int _permitLimit;
        private readonly long _windowTicks;
        private readonly TimeProvider _timeProvider;
        private long _currentWindowId = -1;
        private int _usedPermits;

        public FixedWindowBucket(int permitLimit, TimeSpan window, TimeProvider timeProvider)
        {
            _permitLimit = permitLimit;
            _windowTicks = window.Ticks;
            _timeProvider = timeProvider;
        }

        public bool IsExhaustedInCurrentWindow(out long windowId)
        {
            var nowTicks = _timeProvider.GetUtcNow().UtcTicks;
            windowId = nowTicks / _windowTicks;

            lock (_gate)
            {
                return _currentWindowId == windowId && _usedPermits >= _permitLimit;
            }
        }

        public bool TryAcquire(int permitCount, out TimeSpan retryAfter)
        {
            if (permitCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(permitCount));
            }

            var nowTicks = _timeProvider.GetUtcNow().UtcTicks;
            var windowId = nowTicks / _windowTicks;

            lock (_gate)
            {
                if (_currentWindowId != windowId)
                {
                    _currentWindowId = windowId;
                    _usedPermits = 0;
                }

                if (permitCount == 0)
                {
                    retryAfter = TimeSpan.Zero;
                    return _usedPermits < _permitLimit;
                }

                if (permitCount <= _permitLimit - _usedPermits)
                {
                    _usedPermits += permitCount;
                    retryAfter = TimeSpan.Zero;
                    return true;
                }

                var windowEndTicks = (windowId + 1) * _windowTicks;
                retryAfter = TimeSpan.FromTicks(Math.Max(1, windowEndTicks - nowTicks));
                return false;
            }
        }
    }

    private sealed class DualRateLimiter : RateLimiter
    {
        private readonly DualIpAndEmailRateLimiterPolicy _policy;
        private readonly string _ip;
        private readonly string _email;
        private readonly TimeProvider _timeProvider;
        private readonly long? _exhaustedWindowId;
        private long _lastActivityUtcTicks;

        public DualRateLimiter(
            DualIpAndEmailRateLimiterPolicy policy,
            string ip,
            string email,
            TimeProvider timeProvider,
            long? exhaustedWindowId)
        {
            _policy = policy;
            _ip = ip;
            _email = email;
            _timeProvider = timeProvider;
            _exhaustedWindowId = exhaustedWindowId;
            _lastActivityUtcTicks = timeProvider.GetUtcNow().UtcTicks;
        }

        public override TimeSpan? IdleDuration
        {
            get
            {
                var elapsedTicks = _timeProvider.GetUtcNow().UtcTicks - Interlocked.Read(ref _lastActivityUtcTicks);
                return TimeSpan.FromTicks(Math.Max(0, elapsedTicks));
            }
        }

        public override RateLimiterStatistics? GetStatistics() => null;

        protected override RateLimitLease AttemptAcquireCore(int permitCount)
        {
            ObjectDisposedException.ThrowIf(_policy.IsDisposed, this);

            var nowTicks = _timeProvider.GetUtcNow().UtcTicks;
            Interlocked.Exchange(ref _lastActivityUtcTicks, nowTicks);

            if (_exhaustedWindowId.HasValue)
            {
                var windowTicks = Window.Ticks;
                var currentWindowId = nowTicks / windowTicks;
                if (currentWindowId == _exhaustedWindowId.Value)
                {
                    var windowEndTicks = (currentWindowId + 1) * windowTicks;
                    return new FixedWindowLease(isAcquired: false, TimeSpan.FromTicks(Math.Max(1, windowEndTicks - nowTicks)));
                }
            }

            var ipBucket = _policy.GetOrCreateIpBucket(_ip);
            if (!ipBucket.TryAcquire(permitCount, out var ipRetryAfter))
            {
                return new FixedWindowLease(isAcquired: false, ipRetryAfter);
            }

            if (_email.Length > 0)
            {
                var emailBucket = _policy.GetOrCreateEmailBucket(_email);
                if (!emailBucket.TryAcquire(permitCount, out var emailRetryAfter))
                {
                    return new FixedWindowLease(isAcquired: false, emailRetryAfter);
                }
            }

            return FixedWindowLease.AcquiredLease;
        }

        protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromCanceled<RateLimitLease>(cancellationToken);
            }

            return new ValueTask<RateLimitLease>(AttemptAcquireCore(permitCount));
        }

        private sealed class FixedWindowLease : RateLimitLease
        {
            public static readonly FixedWindowLease AcquiredLease = new(isAcquired: true, retryAfter: null);
            private static readonly string[] AcquiredMetadataNames = [];
            private static readonly string[] RejectedMetadataNames = [MetadataName.RetryAfter.Name];

            private readonly TimeSpan? _retryAfter;

            public FixedWindowLease(bool isAcquired, TimeSpan? retryAfter)
            {
                IsAcquired = isAcquired;
                _retryAfter = retryAfter;
            }

            public override bool IsAcquired { get; }

            public override IEnumerable<string> MetadataNames =>
                _retryAfter.HasValue ? RejectedMetadataNames : AcquiredMetadataNames;

            public override bool TryGetMetadata(string metadataName, out object? metadata)
            {
                if (_retryAfter.HasValue && string.Equals(metadataName, MetadataName.RetryAfter.Name, StringComparison.Ordinal))
                {
                    metadata = _retryAfter.Value;
                    return true;
                }

                metadata = null;
                return false;
            }
        }
    }
}
