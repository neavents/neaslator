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
        RedisResult? result;
        try
        {
            result = await db.ScriptEvaluateAsync(
                _releaseScript,
                [new RedisKey(lockKey)],
                [new RedisValue(lockValue)]);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("Lua", StringComparison.OrdinalIgnoreCase))
        {
            // Garnet can be built without Lua, and this estate runs Garnet.
            //
            // The compare-and-delete script is what stops us releasing a lease that has already
            // expired or been taken by someone else. Without Lua that atomicity is unavailable —
            // but an UNCONDITIONAL delete is not the only alternative, and it is the wrong one.
            // AcquireAsync has a ForcedAcquisition path, so "another holder owns this key" is a
            // designed state rather than an expiry edge case: deleting blindly hands a second
            // worker the same menu, and translation is the paid path.
            //
            // Read, compare, delete only on a match. Not atomic — the holder could change between
            // the read and the delete — but that window is microseconds wide instead of always.
            // The TTL remains the dead-man switch, and a lease that can never be released would
            // make every other instance wait out the full timeout on every miss, which is why this
            // does not simply give up.
            activity?.AddEvent(new ActivityEvent("lock_release_without_lua"));

            // Its own guard. This path runs inside the handler for a FAILED call, and the read it
            // adds can fail the same way — an unguarded one would throw out of a release that is
            // invoked from a finally, turning a successful translation into a reported failure.
            RedisValue current;

            try
            {
                current = await db.StringGetAsync(lockKey).ConfigureAwait(false);
            }
            // Deliberately broad. RedisTimeoutException derives from TimeoutException, not from
            // RedisException, so catching the Redis hierarchy misses the single most likely
            // failure here. This method's contract is that it never throws; the catch has to be as
            // wide as that promise.
            catch (Exception readFailure) when (readFailure is not OperationCanceledException)
            {
                activity?.SetTag("neaslator.lock.released", false);
                activity?.AddEvent(new ActivityEvent("lock_release_failed",
                    tags: new ActivityTagsCollection([new("reason", readFailure.GetType().Name)])));
                return;
            }

            if (current.IsNullOrEmpty)
            {
                // Already gone: expired, or released by whoever took it over. Nothing to do, and
                // nothing wrong.
                activity?.SetTag("neaslator.lock.released", false);
                activity?.AddEvent(new ActivityEvent("lock_release_missed",
                    tags: new ActivityTagsCollection([new("reason", "already_gone")])));
                return;
            }

            if (!string.Equals(current.ToString(), lockValue, StringComparison.Ordinal))
            {
                // Someone else holds it now. Releasing would be releasing THEIR lease.
                activity?.SetTag("neaslator.lock.released", false);
                activity?.AddEvent(new ActivityEvent("lock_release_missed",
                    tags: new ActivityTagsCollection([new("reason", "held_by_another")])));
                return;
            }

            try
            {
                bool deleted = await db.KeyDeleteAsync(lockKey).ConfigureAwait(false);
                activity?.SetTag("neaslator.lock.released", deleted);
            }
            catch (Exception deleteFailure) when (deleteFailure is not OperationCanceledException)
            {
                activity?.SetTag("neaslator.lock.released", false);
                activity?.AddEvent(new ActivityEvent("lock_release_failed",
                    tags: new ActivityTagsCollection([new("reason", deleteFailure.GetType().Name)])));
            }

            return;
        }
        // Deliberately broader than RedisException, which is what this used to catch.
        // RedisTimeoutException derives from TimeoutException and NOT from RedisException —
        // verified against the assembly, not assumed — so the narrower catch missed the single
        // most likely failure and let it escape the very guarantee documented below. A timeout
        // talking to Garnet is exactly the moment a release is called and exactly the moment it
        // must not throw.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never throw out of a release. This is called from a finally, so an exception here
            // would replace the caller's real result — a successful translation reported as a
            // failure — and the TTL reclaims the lease regardless.
            activity?.AddEvent(new ActivityEvent("lock_release_failed",
                tags: new ActivityTagsCollection([new("reason", ex.GetType().Name)])));
            activity?.SetTag("neaslator.lock.released", false);
            return;
        }

        bool released = result is not null && (int)result == 1;
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
