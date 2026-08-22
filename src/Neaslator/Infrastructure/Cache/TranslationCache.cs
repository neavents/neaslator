using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Neaslator.Domain.Entities;
using Neaslator.Domain.Enums;
using Neaslator.Observability;
using Neaslator.Persistence;
using Npgsql;
using StackExchange.Redis;

namespace Neaslator.Infrastructure.Cache;

public sealed class TranslationCache : ITranslationCache
{
    /// <summary>
    /// How long an L1 entry lives in Garnet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// L1 entries were written with no expiry at all, so Garnet accumulated every translation this
    /// service ever looked up and never gave any of it back. Keys are content-addressed
    /// (<c>sourceHash:targetLanguage</c>) so nothing here goes stale — but nothing here is ever
    /// evicted either, and the estate is heading for roughly seventy-five languages across every
    /// string on every menu.
    /// </para>
    /// <para>
    /// That is not this service's problem alone. Garnet is shared with identity, menu, media,
    /// subscription and messaging, so an unbounded translation cache does not simply grow — under
    /// memory pressure it evicts the permission and entitlement entries those services depend on,
    /// and the symptom surfaces somewhere with no connection to translation at all.
    /// </para>
    /// <para>
    /// Safe because L1 is only an accelerator: the durable copy is <c>TranslationMemory</c> in
    /// Postgres, which is what the lookup already falls back to when an L1 value is missing or
    /// corrupt. An expired key costs one indexed read and is backfilled on the way past.
    /// </para>
    /// <para>
    /// Long rather than short, because freshness is not what this bounds — memory is. A hot menu's
    /// translations are re-backfilled the first time anyone asks after expiry.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan L1Ttl = TimeSpan.FromDays(30);

    private readonly IConnectionMultiplexer _garnet;
    private readonly NeaslatorDbContext _db;

    public TranslationCache(IConnectionMultiplexer garnet, NeaslatorDbContext dbContext)
    {
        _garnet = garnet;
        _db = dbContext;
    }

    public async Task<IReadOnlyList<CacheLookupResult>> LookupAsync(
        long sourceHash,
        string normalizedSourceText,
        string sourceLanguageCode,
        IReadOnlyList<string> targetLanguageCodes,
        CancellationToken cancellationToken)
    {
        using Activity? activity = NeaslatorActivitySources.Cache.StartActivity("TranslationCache.Lookup");
        activity?.SetTag("neaslator.cache.source_hash", sourceHash);
        activity?.SetTag("neaslator.cache.source_language", sourceLanguageCode);
        activity?.SetTag("neaslator.cache.target_count", targetLanguageCodes.Count);

        List<CacheLookupResult> results = new(targetLanguageCodes.Count);
        List<string> l1Misses = [];
        int l1HitCount = 0;
        int l2HitCount = 0;
        int collisionCount = 0;

        IDatabase db = _garnet.GetDatabase();

        RedisKey[] keys = new RedisKey[targetLanguageCodes.Count];
        for (int i = 0; i < targetLanguageCodes.Count; i++)
            keys[i] = $"neaslator:t:{sourceHash}:{targetLanguageCodes[i]}";

        RedisValue[] values = await db.StringGetAsync(keys);

        for (int i = 0; i < targetLanguageCodes.Count; i++)
        {
            if (values[i].HasValue)
            {
                CachedTranslation? cached = null;
                try
                {
                    cached = JsonSerializer.Deserialize<CachedTranslation>((string)values[i]!);
                }
                catch (JsonException)
                {
                    // A poisoned/corrupt L1 value must not break the lookup — L2 (Postgres) is
                    // authoritative, so degrade to an L1 miss and fall through.
                    activity?.AddEvent(new ActivityEvent("l1_corrupt_value_skipped",
                        tags: new ActivityTagsCollection([
                            new("target_language", targetLanguageCodes[i]),
                            new("source_hash", sourceHash)
                        ])));
                }

                if (cached is not null &&
                    cached.NormalizedSourceText.Equals(normalizedSourceText, StringComparison.Ordinal))
                {
                    results.Add(new CacheLookupResult(targetLanguageCodes[i], cached, CacheSource.L1Garnet));
                    l1HitCount++;
                    continue;
                }

                if (cached is not null)
                {
                    collisionCount++;
                    NeaslatorMetrics.CacheCollisions.Add(1,
                        new("level", "l1"),
                        new("target_language", targetLanguageCodes[i]));
                    activity?.AddEvent(new ActivityEvent("hash_collision_l1",
                        tags: new ActivityTagsCollection([
                            new("target_language", targetLanguageCodes[i]),
                            new("source_hash", sourceHash)
                        ])));
                }
            }
            l1Misses.Add(targetLanguageCodes[i]);
        }

        if (l1Misses.Count == 0)
        {
            activity?.SetTag("neaslator.cache.l1_hits", l1HitCount);
            activity?.SetTag("neaslator.cache.l2_hits", 0);
            activity?.SetTag("neaslator.cache.misses", 0);
            activity?.SetTag("neaslator.cache.collisions", collisionCount);
            return results;
        }

        List<TranslationMemoryEntry> l2Hits = await _db.TranslationMemory
            .Where(e => e.SourceHash == sourceHash
                     && e.SourceLanguageCode == sourceLanguageCode
                     && l1Misses.Contains(e.TargetLanguageCode))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        List<KeyValuePair<RedisKey, RedisValue>> backfill = [];

        foreach (TranslationMemoryEntry entry in l2Hits)
        {
            if (!entry.NormalizedSourceText.Equals(normalizedSourceText, StringComparison.Ordinal))
            {
                collisionCount++;
                NeaslatorMetrics.CacheCollisions.Add(1,
                    new("level", "l2"),
                    new("target_language", entry.TargetLanguageCode));
                activity?.AddEvent(new ActivityEvent("hash_collision_l2",
                    tags: new ActivityTagsCollection([
                        new("target_language", entry.TargetLanguageCode),
                        new("source_hash", sourceHash)
                    ])));
                continue;
            }

            CachedTranslation cached = new(
                entry.TranslatedText,
                entry.ProviderTier,
                entry.ConfidenceScore,
                entry.NormalizedSourceText);

            results.Add(new CacheLookupResult(entry.TargetLanguageCode, cached, CacheSource.L2PostgreSql));
            l1Misses.Remove(entry.TargetLanguageCode);
            l2HitCount++;

            backfill.Add(new(
                $"neaslator:t:{sourceHash}:{entry.TargetLanguageCode}",
                JsonSerializer.Serialize(cached)));
        }

        if (backfill.Count > 0)
        {
            // A batch rather than the multi-key StringSetAsync overload, which maps to MSET — and
            // MSET cannot carry an expiry, so every key written that way lived in Garnet forever.
            // A batch is still one pipelined round trip and each SET carries its own TTL.
            IBatch batch = db.CreateBatch();
            List<Task> writes = new(backfill.Count);

            foreach (KeyValuePair<RedisKey, RedisValue> entry in backfill)
            {
                writes.Add(batch.StringSetAsync(entry.Key, entry.Value, L1Ttl));
            }

            batch.Execute();
            await Task.WhenAll(writes);

            NeaslatorMetrics.CacheBackfills.Add(backfill.Count,
                new KeyValuePair<string, object?>("source_hash", sourceHash.ToString()));
        }

        List<long> hitIds = l2Hits.Where(e => e.NormalizedSourceText.Equals(normalizedSourceText, StringComparison.Ordinal)).Select(e => e.Id).ToList();
        if (hitIds.Count > 0)
            await _db.TranslationMemory.Where(e => hitIds.Contains(e.Id)).ExecuteUpdateAsync(s => s.SetProperty(e => e.HitCount, e => e.HitCount + 1), cancellationToken);

        foreach (string missLang in l1Misses)
            results.Add(new CacheLookupResult(missLang, null, CacheSource.Miss));

        activity?.SetTag("neaslator.cache.l1_hits", l1HitCount);
        activity?.SetTag("neaslator.cache.l2_hits", l2HitCount);
        activity?.SetTag("neaslator.cache.misses", l1Misses.Count);
        activity?.SetTag("neaslator.cache.collisions", collisionCount);
        activity?.SetTag("neaslator.cache.backfilled", backfill.Count);

        return results;
    }

    public async Task StoreAsync(
        long sourceHash,
        string normalizedSourceText,
        string sourceLanguageCode,
        string targetLanguageCode,
        string translatedText,
        TranslationProviderTier providerTier,
        string providerName,
        float confidenceScore,
        CancellationToken cancellationToken)
    {
        using Activity? activity = NeaslatorActivitySources.Cache.StartActivity("TranslationCache.Store");
        activity?.SetTag("neaslator.cache.source_hash", sourceHash);
        activity?.SetTag("neaslator.cache.source_language", sourceLanguageCode);
        activity?.SetTag("neaslator.cache.target_language", targetLanguageCode);
        activity?.SetTag("neaslator.cache.provider", providerName);
        activity?.SetTag("neaslator.cache.provider_tier", providerTier.ToString());

        TranslationMemoryEntry entry = new()
        {
            SourceHash = sourceHash,
            NormalizedSourceText = normalizedSourceText,
            SourceLanguageCode = sourceLanguageCode,
            TargetLanguageCode = targetLanguageCode,
            TranslatedText = translatedText,
            ProviderTier = providerTier,
            ProviderName = providerName,
            ConfidenceScore = confidenceScore,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _db.TranslationMemory.Add(entry);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            activity?.SetTag("neaslator.cache.store_action", "inserted");
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pgEx && pgEx.SqlState == "23505")
        {
            _db.Entry(entry).State = EntityState.Detached;

            await _db.TranslationMemory
                .Where(e => e.SourceHash == sourceHash
                         && e.SourceLanguageCode == sourceLanguageCode
                         && e.TargetLanguageCode == targetLanguageCode)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(e => e.TranslatedText, translatedText)
                    .SetProperty(e => e.ProviderTier, providerTier)
                    .SetProperty(e => e.ProviderName, providerName)
                    .SetProperty(e => e.ConfidenceScore, confidenceScore)
                    .SetProperty(e => e.UpdatedAt, DateTimeOffset.UtcNow),
                    cancellationToken);

            activity?.SetTag("neaslator.cache.store_action", "upserted");
            activity?.AddEvent(new ActivityEvent("duplicate_key_upsert"));
        }

        CachedTranslation cached = new(translatedText, providerTier, confidenceScore, normalizedSourceText);
        IDatabase db = _garnet.GetDatabase();
        await db.StringSetAsync(
            $"neaslator:t:{sourceHash}:{targetLanguageCode}",
            JsonSerializer.Serialize(cached),
            L1Ttl);

        activity?.AddEvent(new ActivityEvent("l1_cache_populated"));
    }

    public async Task InvalidateAsync(long sourceHash, string targetLanguageCode)
    {
        using Activity? activity = NeaslatorActivitySources.Cache.StartActivity("TranslationCache.Invalidate");
        activity?.SetTag("neaslator.cache.source_hash", sourceHash);
        activity?.SetTag("neaslator.cache.target_language", targetLanguageCode);

        IDatabase db = _garnet.GetDatabase();
        bool deleted = await db.KeyDeleteAsync($"neaslator:t:{sourceHash}:{targetLanguageCode}");
        activity?.SetTag("neaslator.cache.key_existed", deleted);
    }
}
