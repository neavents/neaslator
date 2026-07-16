using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Neaslator.Domain.Entities;
using Neaslator.Domain.Enums;
using Neaslator.Infrastructure.Cache;
using Neaslator.Infrastructure.Diff;
using Neaslator.Infrastructure.Providers;
using Neaslator.Observability;
using Neaslator.Persistence;

namespace Neaslator.Features.TranslateMenu;

public sealed class TranslationPipeline
{
    private const int MaxBatchSize = 20;

    private readonly NeaslatorDbContext _db;
    private readonly ITranslationCache _cache;
    private readonly ITranslationRouter _router;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TranslationPipeline> _logger;

    public TranslationPipeline(
        NeaslatorDbContext dbContext,
        ITranslationCache cache,
        ITranslationRouter router,
        IServiceScopeFactory scopeFactory,
        ILogger<TranslationPipeline> logger)
    {
        _db = dbContext;
        _cache = cache;
        _router = router;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<TranslationPipelineResult> ExecuteAsync(
        MenuSnapshot currentSnapshot,
        MenuSnapshot? previousSnapshot,
        string sourceLanguageCode,
        string venueType,
        string cuisineType,
        CancellationToken cancellationToken)
    {
        long pipelineStartTicks = Stopwatch.GetTimestamp();

        using Activity? activity = NeaslatorActivitySources.Pipeline.StartActivity("TranslationPipeline.Execute");
        activity?.SetTag("neaslator.source_language", sourceLanguageCode);
        activity?.SetTag("neaslator.venue_type", venueType);
        activity?.SetTag("neaslator.cuisine_type", cuisineType);
        activity?.SetTag("neaslator.has_previous_snapshot", previousSnapshot is not null);

        int totalSections = currentSnapshot.Sections.Count;
        int totalItems = currentSnapshot.Sections.Sum(s => s.Items.Count);
        activity?.SetTag("neaslator.snapshot.total_sections", totalSections);
        activity?.SetTag("neaslator.snapshot.total_items", totalItems);

        IReadOnlyList<TranslationUnit> changedUnits;
        using (Activity? diffActivity = NeaslatorActivitySources.Pipeline.StartActivity("compute_diff"))
        {
            changedUnits = DiffEngine.ComputeDiff(currentSnapshot, previousSnapshot);
            diffActivity?.SetTag("neaslator.diff.total_sections", totalSections);
            diffActivity?.SetTag("neaslator.diff.total_items", totalItems);
            diffActivity?.SetTag("neaslator.diff.changed_units", changedUnits.Count);
            diffActivity?.SetTag("neaslator.diff.section_names_changed", changedUnits.Count(u => u.UnitType == TranslationUnitType.SectionName));
            diffActivity?.SetTag("neaslator.diff.item_names_changed", changedUnits.Count(u => u.UnitType == TranslationUnitType.ItemName));
            diffActivity?.SetTag("neaslator.diff.item_descriptions_changed", changedUnits.Count(u => u.UnitType == TranslationUnitType.ItemDescription));
        }

        NeaslatorMetrics.PipelineDiffChangedUnits.Add(changedUnits.Count);

        if (changedUnits.Count == 0)
        {
            activity?.SetTag("neaslator.result", "no_changes");
            activity?.AddEvent(new ActivityEvent("no_changes_detected"));
            RecordPipelineDuration(pipelineStartTicks, 0);
            return new TranslationPipelineResult
            {
                TotalLanguages = 0,
                CompletedLanguages = 0,
                FailedLanguages = 0,
                Results = []
            };
        }

        List<SupportedLanguage> targetLanguages = await _db.SupportedLanguages
            .Where(l => l.IsActive && l.Code != sourceLanguageCode)
            .OrderBy(l => l.SortOrder)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        List<string> targetCodes = targetLanguages.Select(l => l.Code).ToList();
        activity?.SetTag("neaslator.target_languages_count", targetCodes.Count);

        Dictionary<string, List<TranslationUnit>> cacheMissesByLanguage = [];
        int l1Hits = 0;
        int l2Hits = 0;
        int totalMisses = 0;
        int totalLookups = 0;

        using (Activity? cacheActivity = NeaslatorActivitySources.Cache.StartActivity("resolve_cache"))
        {
            foreach (TranslationUnit unit in changedUnits)
            {
                IReadOnlyList<CacheLookupResult> lookupResults = await _cache.LookupAsync(
                    unit.SourceHash,
                    unit.NormalizedSourceText,
                    sourceLanguageCode,
                    targetCodes,
                    cancellationToken);

                foreach (CacheLookupResult result in lookupResults)
                {
                    totalLookups++;

                    NeaslatorMetrics.CacheLookups.Add(1,
                        new("level", result.Source == CacheSource.L1Garnet ? "l1" : result.Source == CacheSource.L2PostgreSql ? "l2" : "miss"),
                        new("result", result.Translation is not null ? "hit" : "miss"));

                    if (result.Source == CacheSource.Miss)
                    {
                        totalMisses++;
                        if (!cacheMissesByLanguage.TryGetValue(result.TargetLanguageCode, out List<TranslationUnit>? misses))
                        {
                            misses = [];
                            cacheMissesByLanguage[result.TargetLanguageCode] = misses;
                        }
                        misses.Add(unit);
                    }
                    else
                    {
                        if (result.Source == CacheSource.L1Garnet)
                            l1Hits++;
                        else
                            l2Hits++;

                        NeaslatorMetrics.ItemsProcessed.Add(1,
                            new KeyValuePair<string, object?>("source", result.Source == CacheSource.L1Garnet ? "cache_l1" : "cache_l2"));
                    }
                }
            }

            cacheActivity?.SetTag("neaslator.cache.total_lookups", totalLookups);
            cacheActivity?.SetTag("neaslator.cache.l1_hits", l1Hits);
            cacheActivity?.SetTag("neaslator.cache.l2_hits", l2Hits);
            cacheActivity?.SetTag("neaslator.cache.misses", totalMisses);
            cacheActivity?.SetTag("neaslator.cache.miss_languages", cacheMissesByLanguage.Count);
            cacheActivity?.SetTag("neaslator.cache.hit_ratio", totalLookups > 0 ? (double)(l1Hits + l2Hits) / totalLookups : 0.0);
            cacheActivity?.SetTag("neaslator.cache.l1_hit_ratio", totalLookups > 0 ? (double)l1Hits / totalLookups : 0.0);
        }

        activity?.SetTag("neaslator.cache.total_lookups", totalLookups);
        activity?.SetTag("neaslator.cache.total_hits", l1Hits + l2Hits);
        activity?.SetTag("neaslator.cache.total_misses", totalMisses);

        if (cacheMissesByLanguage.Count == 0)
        {
            activity?.SetTag("neaslator.result", "fully_cached");
            activity?.AddEvent(new ActivityEvent("all_translations_cached"));
            NeaslatorMetrics.PipelineLanguagesPerRun.Record(targetCodes.Count);
            RecordPipelineDuration(pipelineStartTicks, targetCodes.Count);
            return new TranslationPipelineResult
            {
                TotalLanguages = targetCodes.Count,
                CompletedLanguages = targetCodes.Count,
                FailedLanguages = 0,
                Results = targetCodes.Select(c => new LanguageResult
                {
                    TargetLanguageCode = c,
                    IsSuccess = true
                }).ToList()
            };
        }

        List<LanguageResult> results = [];
        int completedCount = targetCodes.Count - cacheMissesByLanguage.Count;
        int failedCount = 0;

        foreach (string code in targetCodes)
        {
            if (!cacheMissesByLanguage.ContainsKey(code))
                results.Add(new LanguageResult { TargetLanguageCode = code, IsSuccess = true });
        }

        int totalBatchCount = 0;

        using (Activity? translateActivity = NeaslatorActivitySources.Provider.StartActivity("fan_out_translations"))
        {
            int totalUnitsToTranslate = cacheMissesByLanguage.Values.Sum(v => v.Count);
            translateActivity?.SetTag("neaslator.translation.languages_count", cacheMissesByLanguage.Count);
            translateActivity?.SetTag("neaslator.translation.total_units", totalUnitsToTranslate);

            using SemaphoreSlim concurrencyLimiter = new(20);

            Task[] translationTasks = cacheMissesByLanguage.Select(async kvp =>
            {
                await concurrencyLimiter.WaitAsync(cancellationToken);
                try
                {
                    string targetLang = kvp.Key;
                    List<TranslationUnit> units = kvp.Value;

                    List<List<TranslationUnit>> chunks = [];
                    for (int i = 0; i < units.Count; i += MaxBatchSize)
                    {
                        chunks.Add(units.GetRange(i, Math.Min(MaxBatchSize, units.Count - i)));
                    }

                    Interlocked.Add(ref totalBatchCount, chunks.Count);

                    try
                    {
                        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
                        ITranslationCache scopedCache = scope.ServiceProvider.GetRequiredService<ITranslationCache>();

                        foreach (List<TranslationUnit> chunk in chunks)
                        {
                            using Activity? batchActivity = NeaslatorActivitySources.Provider.StartActivity("translate_batch");
                            batchActivity?.SetTag("neaslator.batch.target_language", targetLang);
                            batchActivity?.SetTag("neaslator.batch.size", chunk.Count);
                            batchActivity?.SetTag("neaslator.batch.source_language", sourceLanguageCode);

                            TranslationBatchRequest request = new()
                            {
                                SourceLanguageCode = sourceLanguageCode,
                                TargetLanguageCode = targetLang,
                                VenueType = venueType,
                                CuisineType = cuisineType,
                                SectionName = "Menu",
                                Items = chunk.Select(u => new TranslationBatchItem
                                {
                                    SourceHash = u.SourceHash,
                                    Name = u.NormalizedSourceText,
                                    Description = null
                                }).ToList()
                            };

                            TranslationBatchResult batchResult = await _router.TranslateAsync(request, cancellationToken);

                            batchActivity?.SetTag("neaslator.batch.success", batchResult.IsSuccess);
                            batchActivity?.SetTag("neaslator.batch.provider", batchResult.ProviderName);
                            batchActivity?.SetTag("neaslator.batch.latency_ms", batchResult.Latency.TotalMilliseconds);
                            batchActivity?.SetTag("neaslator.batch.input_tokens", batchResult.TokenUsage.InputTokens);
                            batchActivity?.SetTag("neaslator.batch.output_tokens", batchResult.TokenUsage.OutputTokens);
                            batchActivity?.SetTag("neaslator.batch.cached_tokens", batchResult.TokenUsage.CachedTokens);

                            if (batchResult.IsSuccess)
                            {
                                batchActivity?.SetTag("neaslator.batch.translated_count", batchResult.Translations.Count);

                                foreach (TranslatedUnit translated in batchResult.Translations)
                                {
                                    TranslationUnit? matchingUnit = chunk.FirstOrDefault(u => u.SourceHash == translated.SourceHash);
                                    if (matchingUnit is null) continue;

                                    await scopedCache.StoreAsync(
                                        translated.SourceHash,
                                        matchingUnit.NormalizedSourceText,
                                        sourceLanguageCode,
                                        targetLang,
                                        translated.TranslatedName,
                                        batchResult.ProviderTier,
                                        batchResult.ProviderName,
                                        translated.ConfidenceScore,
                                        cancellationToken);

                                    NeaslatorMetrics.ItemsProcessed.Add(1, new KeyValuePair<string, object?>("source", "provider"));
                                }
                            }
                            else
                            {
                                batchActivity?.SetStatus(ActivityStatusCode.Error, batchResult.ErrorMessage);
                                batchActivity?.AddEvent(new ActivityEvent("batch_failed",
                                    tags: new ActivityTagsCollection([ new("error", batchResult.ErrorMessage ?? "unknown") ])));

                                lock (results)
                                {
                                    results.Add(new LanguageResult
                                    {
                                        TargetLanguageCode = targetLang,
                                        IsSuccess = false,
                                        ErrorMessage = batchResult.ErrorMessage
                                    });
                                    failedCount++;
                                }
                                return;
                            }
                        }

                        lock (results)
                        {
                            results.Add(new LanguageResult { TargetLanguageCode = targetLang, IsSuccess = true });
                            completedCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Translation failed for language {Language}", targetLang);
                        lock (results)
                        {
                            results.Add(new LanguageResult
                            {
                                TargetLanguageCode = targetLang,
                                IsSuccess = false,
                                ErrorMessage = ex.Message
                            });
                            failedCount++;
                        }
                    }
                }
                finally
                {
                    concurrencyLimiter.Release();
                }
            }).ToArray();

            await Task.WhenAll(translationTasks);

            translateActivity?.SetTag("neaslator.translation.batch_count", totalBatchCount);
            translateActivity?.SetTag("neaslator.translation.completed", completedCount);
            translateActivity?.SetTag("neaslator.translation.failed", failedCount);
        }

        activity?.SetTag("neaslator.result.total_languages", targetCodes.Count);
        activity?.SetTag("neaslator.result.completed", completedCount);
        activity?.SetTag("neaslator.result.failed", failedCount);
        activity?.SetTag("neaslator.result", failedCount == 0 ? "success" : completedCount > 0 ? "partial" : "failed");

        if (failedCount > 0 && completedCount == 0)
            activity?.SetStatus(ActivityStatusCode.Error, "All language translations failed");

        NeaslatorMetrics.PipelineLanguagesPerRun.Record(targetCodes.Count);
        RecordPipelineDuration(pipelineStartTicks, targetCodes.Count);

        return new TranslationPipelineResult
        {
            TotalLanguages = targetCodes.Count,
            CompletedLanguages = completedCount,
            FailedLanguages = failedCount,
            Results = results
        };
    }

    private static void RecordPipelineDuration(long startTicks, int languageCount)
    {
        double elapsed = Stopwatch.GetElapsedTime(startTicks).TotalSeconds;
        NeaslatorMetrics.PipelineDurationSeconds.Record(elapsed,
            new KeyValuePair<string, object?>("languages", languageCount));
    }
}

public sealed class TranslationPipelineResult
{
    public required int TotalLanguages { get; init; }
    public required int CompletedLanguages { get; init; }
    public required int FailedLanguages { get; init; }
    public required IReadOnlyList<LanguageResult> Results { get; init; }
}

public sealed class LanguageResult
{
    public required string TargetLanguageCode { get; init; }
    public required bool IsSuccess { get; init; }
    public string? ErrorMessage { get; init; }
}
