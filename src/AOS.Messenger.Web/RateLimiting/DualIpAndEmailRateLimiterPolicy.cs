using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;

namespace AOS.Messenger.Web.RateLimiting;

/// <summary>
/// Rate limiter policy that enforces dual rate limiting:
/// 1. Per-client-IP limit (prevents flooding from a single IP address).
/// 2. Per-normalized-target-email limit (prevents email bombing a specific address across IPs).
/// Retains each counter for its full window and rejects new identities at capacity.
/// Reports <see cref="RateLimiter.IdleDuration"/> so <see cref="PartitionedRateLimiter"/>
/// reclaims idle partition entries.
/// </summary>
public sealed class DualIpAndEmailRateLimiterPolicy : IRateLimiterPolicy<string>, IDisposable
{
    public const int DefaultMaxCacheEntries = 50_000;
    public const int IpPermitLimitPerMinute = 10;
    public const int EmailPermitLimitPerMinute = 5;
    private const int MaxPartitionEmailLength = 254;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

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

    private readonly object _gate = new();
    private readonly Dictionary<string, int> _ipCounts = new();
    private readonly Dictionary<string, int> _emailCounts = new();
    private readonly int _maxEntries;
    private long _currentWindowId = -1;
    private readonly TimeProvider _timeProvider;
    private int _disposed;

    public DualIpAndEmailRateLimiterPolicy()
        : this(DefaultMaxCacheEntries, null)
    {
    }

    public DualIpAndEmailRateLimiterPolicy(int maxCacheEntries = DefaultMaxCacheEntries, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCacheEntries);

        _maxEntries = maxCacheEntries;
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
        return IsIpExhausted(ip, out _);
    }

    private bool IsIpExhausted(string ip, out long windowId)
    {
        lock (_gate)
        {
            windowId = ResetWindowIfNeeded(_timeProvider.GetUtcNow().UtcTicks);
            return _ipCounts.TryGetValue(ip, out var used)
                ? used >= IpPermitLimitPerMinute
                : _ipCounts.Count >= _maxEntries;
        }
    }

    public RateLimitPartition<string> GetPartition(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        var ip = ResolveClientIp(httpContext);

        // If the client IP is already exhausted in the current fixed window, route all emails from
        // this IP to a single per-IP/per-window partition key instead of allocating unbounded ip:email keys.
        if (IsIpExhausted(ip, out var exhaustedWindowId))
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

    private bool TryAcquire(string ip, string email, int permitCount, out TimeSpan retryAfter)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            var nowTicks = _timeProvider.GetUtcNow().UtcTicks;
            var windowId = ResetWindowIfNeeded(nowTicks);

            if (TryConsume(_ipCounts, ip, IpPermitLimitPerMinute, permitCount)
                && TryConsume(_emailCounts, email, EmailPermitLimitPerMinute, permitCount))
            {
                retryAfter = TimeSpan.Zero;
                return true;
            }

            retryAfter = TimeSpan.FromTicks((windowId + 1) * Window.Ticks - nowTicks);
            return false;
        }
    }

    private long ResetWindowIfNeeded(long nowTicks)
    {
        var windowId = nowTicks / Window.Ticks;
        if (_currentWindowId != windowId)
        {
            _ipCounts.Clear();
            _emailCounts.Clear();
            _currentWindowId = windowId;
        }

        return windowId;
    }

    private bool TryConsume(Dictionary<string, int> counts, string key, int permitLimit, int permitCount)
    {
        if (!counts.TryGetValue(key, out var used) && counts.Count >= _maxEntries)
        {
            return false;
        }

        if (used >= permitLimit || permitCount > permitLimit - used)
        {
            return false;
        }

        if (permitCount > 0)
        {
            counts[key] = used + permitCount;
        }

        return true;
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
        lock (_gate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _ipCounts.Clear();
                _emailCounts.Clear();
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
                TimeSpan? retryAfter = currentWindowId == _exhaustedWindowId.Value
                    ? TimeSpan.FromTicks((currentWindowId + 1) * windowTicks - nowTicks)
                    : null;
                return new FixedWindowLease(isAcquired: false, retryAfter);
            }

            return _policy.TryAcquire(_ip, _email, permitCount, out var retryAfterForRequest)
                ? FixedWindowLease.AcquiredLease
                : new FixedWindowLease(isAcquired: false, retryAfterForRequest);
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
