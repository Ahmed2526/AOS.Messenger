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
/// 2. Per-normalized-target-email minute and daily limits (prevents email bombing a specific address across IPs).
/// Retains active per-window counters up to capacity while routing overflow keys through bounded
/// randomized hash shards so unseen legitimate identities never suffer a global lockout.
/// Reports <see cref="RateLimiter.IdleDuration"/> so <see cref="PartitionedRateLimiter"/>
/// reclaims idle partition entries.
/// </summary>
public sealed class DualIpAndEmailRateLimiterPolicy : IRateLimiterPolicy<string>, IDisposable
{
    public const int DefaultMaxCacheEntries = 50_000;
    public const int IpPermitLimitPerMinute = 10;
    public const int EmailPermitLimitPerMinute = 5;
    public const int EmailPermitLimitPerDay = 20;
    internal const string ChargedEmailItemKey = "__AOS.DualRateLimit.ChargedEmail";
    private const int MaxPartitionEmailLength = 254;
    private const int OverflowShardCount = 256;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DayWindow = TimeSpan.FromDays(1);

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
    private readonly Dictionary<string, int> _dailyEmailCounts = new();
    private readonly int[] _ipOverflowCounts = new int[OverflowShardCount];
    private readonly int[] _emailOverflowCounts = new int[OverflowShardCount];
    private readonly int[] _dailyEmailOverflowCounts = new int[OverflowShardCount];
    private readonly ulong _overflowHashSeed = CreateOverflowHashSeed();
    private readonly int _maxEntries;
    private long _currentWindowId = -1;
    private long _currentDayId = -1;
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
        var policyName = GetEndpointRateLimitPolicyName(endpoint);
        return policyName is not null && PolicyNameSet.Contains(policyName);
    }

    private static string? GetEndpointRateLimitPolicyName(Endpoint? endpoint) =>
        endpoint?.Metadata
            .GetOrderedMetadata<EnableRateLimitingAttribute>()
            .LastOrDefault()?
            .PolicyName;

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
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            windowId = ResetWindowIfNeeded(_timeProvider.GetUtcNow().UtcTicks);
            return GetUsedCount(_ipCounts, _ipOverflowCounts, ip) >= IpPermitLimitPerMinute;
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
        if (!string.IsNullOrEmpty(email))
        {
            httpContext.Items[ChargedEmailItemKey] = email;
        }

        var partitionKey = string.IsNullOrEmpty(email) ? ip : $"{ip}:{email}";

        return RateLimitPartition.Get(
            partitionKey,
            _ => new DualRateLimiter(this, ip, email, _timeProvider, exhaustedWindowId: null));
    }

    /// <summary>
    /// Enforces minute and daily per-email quotas against an already-bound email identifier
    /// when pre-binding form extraction could not observe it. Does not re-charge the client IP.
    /// </summary>
    public bool TryAcquireAuthoritativeEmail(string rawEmailOrIdentifier, out TimeSpan retryAfter)
    {
        if (string.IsNullOrWhiteSpace(rawEmailOrIdentifier))
        {
            retryAfter = TimeSpan.Zero;
            return true;
        }

        var normalized = NormalizePartitionEmail(rawEmailOrIdentifier);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            var nowTicks = _timeProvider.GetUtcNow().UtcTicks;
            var windowId = ResetWindowIfNeeded(nowTicks);
            var dayId = _currentDayId;

            var canEmailMinute = CanConsume(_emailCounts, _emailOverflowCounts, normalized, EmailPermitLimitPerMinute, 1);
            var canEmailDay = CanConsume(_dailyEmailCounts, _dailyEmailOverflowCounts, normalized, EmailPermitLimitPerDay, 1);

            if (canEmailMinute && canEmailDay)
            {
                Consume(_emailCounts, _emailOverflowCounts, normalized, 1);
                Consume(_dailyEmailCounts, _dailyEmailOverflowCounts, normalized, 1);
                retryAfter = TimeSpan.Zero;
                return true;
            }

            retryAfter = !canEmailMinute
                ? TimeSpan.FromTicks(Math.Max(1, (windowId + 1) * Window.Ticks - nowTicks))
                : TimeSpan.FromTicks(Math.Max(1, (dayId + 1) * DayWindow.Ticks - nowTicks));
            return false;
        }
    }

    private bool TryAcquire(string ip, string email, int permitCount, out TimeSpan retryAfter)
    {
        if (permitCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(permitCount));
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            var nowTicks = _timeProvider.GetUtcNow().UtcTicks;
            var windowId = ResetWindowIfNeeded(nowTicks);
            var dayId = _currentDayId;

            var hasEmail = !string.IsNullOrEmpty(email);
            var canIp = CanConsume(_ipCounts, _ipOverflowCounts, ip, IpPermitLimitPerMinute, permitCount);
            var canEmailMinute = !hasEmail
                || CanConsume(_emailCounts, _emailOverflowCounts, email, EmailPermitLimitPerMinute, permitCount);
            var canEmailDay = !hasEmail
                || CanConsume(_dailyEmailCounts, _dailyEmailOverflowCounts, email, EmailPermitLimitPerDay, permitCount);

            if (canIp && canEmailMinute && canEmailDay)
            {
                Consume(_ipCounts, _ipOverflowCounts, ip, permitCount);
                if (hasEmail)
                {
                    Consume(_emailCounts, _emailOverflowCounts, email, permitCount);
                    Consume(_dailyEmailCounts, _dailyEmailOverflowCounts, email, permitCount);
                }

                retryAfter = TimeSpan.Zero;
                return true;
            }

            retryAfter = (!canIp || !canEmailMinute)
                ? TimeSpan.FromTicks(Math.Max(1, (windowId + 1) * Window.Ticks - nowTicks))
                : TimeSpan.FromTicks(Math.Max(1, (dayId + 1) * DayWindow.Ticks - nowTicks));
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
            Array.Clear(_ipOverflowCounts);
            Array.Clear(_emailOverflowCounts);
            _currentWindowId = windowId;
        }

        var dayId = nowTicks / DayWindow.Ticks;
        if (_currentDayId != dayId)
        {
            _dailyEmailCounts.Clear();
            Array.Clear(_dailyEmailOverflowCounts);
            _currentDayId = dayId;
        }

        return windowId;
    }

    private int GetUsedCount(Dictionary<string, int> counts, int[] overflowCounts, string key)
    {
        if (counts.TryGetValue(key, out var used))
        {
            return used;
        }

        return counts.Count < _maxEntries ? 0 : overflowCounts[GetOverflowIndex(key)];
    }

    private bool CanConsume(
        Dictionary<string, int> counts,
        int[] overflowCounts,
        string key,
        int permitLimit,
        int permitCount)
    {
        var used = GetUsedCount(counts, overflowCounts, key);
        return used < permitLimit && permitCount <= permitLimit - used;
    }

    private void Consume(
        Dictionary<string, int> counts,
        int[] overflowCounts,
        string key,
        int permitCount)
    {
        if (permitCount <= 0)
        {
            return;
        }

        if (counts.TryGetValue(key, out var used))
        {
            counts[key] = used + permitCount;
            return;
        }

        if (counts.Count < _maxEntries)
        {
            counts[key] = permitCount;
            return;
        }

        overflowCounts[GetOverflowIndex(key)] += permitCount;
    }

    private int GetOverflowIndex(string key)
    {
        unchecked
        {
            var hash = _overflowHashSeed ^ 0x9E3779B97F4A7C15UL;
            for (var i = 0; i < key.Length; i++)
            {
                hash ^= key[i];
                hash *= 1099511628211UL;
            }

            hash ^= hash >> 33;
            hash *= 0xFF51AFD7ED558CCDUL;
            hash ^= hash >> 33;
            return (int)(hash & (ulong)(OverflowShardCount - 1));
        }
    }

    private static ulong CreateOverflowHashSeed()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        return BitConverter.ToUInt64(bytes);
    }

    private static string ResolveClientIp(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "anonymous";

    private static string ExtractNormalizedEmail(HttpContext context)
    {
        var policyName = GetEndpointRateLimitPolicyName(context.GetEndpoint());
        var isRecoverPolicy = string.Equals(policyName, "recover-account-limiter", StringComparison.OrdinalIgnoreCase);

        if (context.Request.HasFormContentType)
        {
            // Read only already-parsed/buffered form state; never invoke synchronous Request.Form parsing
            // if asynchronous buffering was skipped right before a fixed-window boundary.
            var form = context.Features.Get<IFormFeature>()?.Form;
            if (form is not null)
            {
                // MVC binds `model.*` preferentially whenever any `model.` prefix exists, so prioritize prefixed keys first.
                string? email = isRecoverPolicy
                    ? GetFirstNonWhiteSpace(form, ["model.UsernameOrEmail", "model.usernameOrEmail", "UsernameOrEmail", "usernameOrEmail"])
                    : GetFirstNonWhiteSpace(form, ["model.Email", "model.email", "Email", "email", "model.UsernameOrEmail", "model.usernameOrEmail", "UsernameOrEmail", "usernameOrEmail"]);

                if (!string.IsNullOrWhiteSpace(email))
                {
                    return NormalizePartitionEmail(email);
                }
            }
        }

        if (!HttpMethods.IsPost(context.Request.Method)
            && context.Request.Query.TryGetValue("email", out var queryEmail)
            && !string.IsNullOrWhiteSpace(queryEmail))
        {
            return NormalizePartitionEmail(queryEmail.ToString());
        }

        return string.Empty;
    }

    private static string? GetFirstNonWhiteSpace(IFormCollection form, ReadOnlySpan<string> keys)
    {
        foreach (var key in keys)
        {
            if (form.TryGetValue(key, out var values))
            {
                foreach (var value in values)
                {
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }
        }

        return null;
    }

    internal static string NormalizePartitionEmail(string rawEmail)
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
                _dailyEmailCounts.Clear();
                Array.Clear(_ipOverflowCounts);
                Array.Clear(_emailOverflowCounts);
                Array.Clear(_dailyEmailOverflowCounts);
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
                    var retryAfter = TimeSpan.FromTicks(Math.Max(1, (currentWindowId + 1) * windowTicks - nowTicks));
                    return new FixedWindowLease(isAcquired: false, retryAfter);
                }
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
