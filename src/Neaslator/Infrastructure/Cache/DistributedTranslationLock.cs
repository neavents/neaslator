using System.Diagnostics;
using Neaslator.Observability;
using StackExchange.Redis;

namespace Neaslator.Infrastructure.Cache;

public sealed class DistributedTranslationLock
{
    private static readonly TimeSpan _lockTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _waitTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan _pollInterval = TimeSpan.FromMilliseconds(100);

    private readonly IConnectionMultiplexer _garnet;

    private const string _releaseScript = """
        if redis.call('get', KEYS[1]) == ARGV[1] then
            return redis.call('del', KEYS[1])
        else
            return 0
        end
        """;

    public DistributedTranslationLock(IConnectionMultiplexer garnet)
    {
        _garnet = garnet;
    }

    public async Task<LockResult> TryAcquireAsync(
        long sourceHash,
        string targetLanguageCode,
        CancellationToken cancellationToken)
    {
        using Activity? activity = NeaslatorActivitySources.Lock.StartActivity("DistributedLock.Acquire");
        activity?.SetTag("neaslator.lock.source_hash", sourceHash);
        activity?.SetTag("neaslator.lock.target_language", targetLanguageCode);
        activity?.SetTag("neaslator.lock.ttl_seconds", _lockTtl.TotalSeconds);

        IDatabase db = _garnet.GetDatabase();
        string lockKey = $"neaslator:lock:{sourceHash}:{targetLanguageCode}";
        string lockValue = Guid.NewGuid().ToString("N");

        bool acquired = await db.StringSetAsync(lockKey, lockValue, _lockTtl, When.NotExists);

        if (acquired)
        {
            activity?.SetTag("neaslator.lock.outcome", "acquired");
            activity?.AddEvent(new ActivityEvent("lock_acquired_first_attempt"));
            NeaslatorMetrics.LockAcquiredTotal.Add(1,
                new KeyValuePair<string, object?>("target_language", targetLanguageCode));
            return LockResult.Acquired(lockKey, lockValue);
        }

        activity?.AddEvent(new ActivityEvent("lock_contention_detected",
            tags: new ActivityTagsCollection([
                new("lock_key", lockKey),
                new("wait_timeout_seconds", _waitTimeout.TotalSeconds)
            ])));

        string cacheKey = $"neaslator:t:{sourceHash}:{targetLanguageCode}";
        Stopwatch stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < _waitTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(_pollInterval, cancellationToken);

            RedisValue cached = await db.StringGetAsync(cacheKey);
            if (cached.HasValue)
            {
                activity?.SetTag("neaslator.lock.outcome", "resolved_by_peer");
                activity?.SetTag("neaslator.lock.wait_duration_ms", stopwatch.Elapsed.TotalMilliseconds);
                activity?.AddEvent(new ActivityEvent("lock_resolved_by_peer",
                    tags: new ActivityTagsCollection([
                        new("wait_ms", stopwatch.Elapsed.TotalMilliseconds)
                    ])));
                NeaslatorMetrics.LockWaitedTotal.Add(1,
                    new KeyValuePair<string, object?>("target_language", targetLanguageCode));
                return LockResult.ResolvedByPeer(cached!);
            }
        }

        await db.StringSetAsync(lockKey, lockValue, _lockTtl, When.Always);
        activity?.SetTag("neaslator.lock.outcome", "forced_acquisition");
        activity?.SetTag("neaslator.lock.wait_duration_ms", stopwatch.Elapsed.TotalMilliseconds);
        activity?.AddEvent(new ActivityEvent("lock_forced_after_timeout",
            tags: new ActivityTagsCollection([
                new("wait_ms", stopwatch.Elapsed.TotalMilliseconds),
                new("timeout_seconds", _waitTimeout.TotalSeconds)
            ])));
        NeaslatorMetrics.LockForcedTotal.Add(1,
            new KeyValuePair<string, object?>("target_language", targetLanguageCode));
        return LockResult.ForcedAcquisition(lockKey, lockValue);
    }

    public async Task ReleaseAsync(string lockKey, string lockValue)
    {
        using Activity? activity = NeaslatorActivitySources.Lock.StartActivity("DistributedLock.Release");
        activity?.SetTag("neaslator.lock.key", lockKey);

        IDatabase db = _garnet.GetDatabase();
        RedisResult result = await db.ScriptEvaluateAsync(
            _releaseScript,
            [new RedisKey(lockKey)],
            [new RedisValue(lockValue)]);

        bool released = (int)result == 1;
        activity?.SetTag("neaslator.lock.released", released);
        if (!released)
        {
            activity?.AddEvent(new ActivityEvent("lock_release_missed",
                tags: new ActivityTagsCollection([
                    new("reason", "lock_expired_or_stolen")
                ])));
        }
    }
}

public sealed record LockResult
{
    public required LockOutcome Outcome { get; init; }
    public string? LockKey { get; init; }
    public string? LockValue { get; init; }
    public string? CachedValue { get; init; }

    public static LockResult Acquired(string lockKey, string lockValue) =>
        new() { Outcome = LockOutcome.Acquired, LockKey = lockKey, LockValue = lockValue };

    public static LockResult ResolvedByPeer(string cachedValue) =>
        new() { Outcome = LockOutcome.ResolvedByPeer, CachedValue = cachedValue };

    public static LockResult ForcedAcquisition(string lockKey, string lockValue) =>
        new() { Outcome = LockOutcome.ForcedAcquisition, LockKey = lockKey, LockValue = lockValue };
}

public enum LockOutcome
{
    Acquired,
    ResolvedByPeer,
    ForcedAcquisition
}
