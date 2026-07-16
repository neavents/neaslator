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
                CachedTranslation? cached = JsonSerializer.Deserialize<CachedTranslation>((string)values[i]!);
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
            await db.StringSetAsync([.. backfill]);
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
            JsonSerializer.Serialize(cached));

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
