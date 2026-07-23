using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Neaslator.Domain.Entities;
using Neaslator.Infrastructure.Cache;
using Neaslator.Infrastructure.Providers;
using Neaslator.Observability;
using Neaslator.Persistence;

namespace Neaslator.Features.QualityUpgrade;

public sealed class QualityUpgradeJob : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<QualityUpgradeJob> _logger;
    private static readonly TimeSpan _interval = TimeSpan.FromHours(6);

    public QualityUpgradeJob(IServiceScopeFactory scopeFactory, ILogger<QualityUpgradeJob> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_interval, stoppingToken);
                await UpgradeDegradedEntries(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Quality upgrade job failed");
            }
        }
    }

    internal async Task UpgradeDegradedEntries(CancellationToken ct)
    {
        using Activity? activity = NeaslatorActivitySources.QualityUpgrade.StartActivity("QualityUpgradeJob.Run");
        long startTicks = Stopwatch.GetTimestamp();

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NeaslatorDbContext>();
        var router = scope.ServiceProvider.GetRequiredService<ITranslationRouter>();
        var cache = scope.ServiceProvider.GetRequiredService<ITranslationCache>();

        List<TranslationMemoryEntry> degraded = await db.TranslationMemory
            .Where(e => e.ProviderTier > Domain.Enums.TranslationProviderTier.Primary)
            .OrderBy(e => e.UpdatedAt)
            .Take(500)
            .AsNoTracking()
            .ToListAsync(ct);

        activity?.SetTag("neaslator.quality.scanned", degraded.Count);
        NeaslatorMetrics.QualityUpgradeEntriesScanned.Add(degraded.Count);

        if (degraded.Count == 0)
        {
            activity?.SetTag("neaslator.quality.result", "no_degraded_entries");
            activity?.AddEvent(new ActivityEvent("no_degraded_entries"));
            _logger.LogInformation("No degraded translations to upgrade");
            return;
        }

        var grouped = degraded
            .GroupBy(e => new { e.TargetLanguageCode, e.SourceLanguageCode })
            .ToList();

        activity?.SetTag("neaslator.quality.language_groups", grouped.Count);
        activity?.SetTag("neaslator.quality.total_entries", degraded.Count);

        int upgradedCount = 0;
        int failedGroupCount = 0;

        foreach (var group in grouped)
        {
            using Activity? groupActivity = NeaslatorActivitySources.QualityUpgrade.StartActivity("QualityUpgrade.LanguageGroup");
            groupActivity?.SetTag("neaslator.quality.source_language", group.Key.SourceLanguageCode);
            groupActivity?.SetTag("neaslator.quality.target_language", group.Key.TargetLanguageCode);
            groupActivity?.SetTag("neaslator.quality.group_size", group.Count());

            try
            {
                TranslationBatchRequest request = new()
                {
                    SourceLanguageCode = group.Key.SourceLanguageCode,
                    TargetLanguageCode = group.Key.TargetLanguageCode,
                    VenueType = "General",
                    CuisineType = "General",
                    SectionName = "Menu",
                    Items = group.Select(e => new TranslationBatchItem
                    {
                        SourceHash = e.SourceHash,
                        Name = e.NormalizedSourceText,
                        Description = null
                    }).ToList()
                };

                TranslationBatchResult result = await router.TranslateAsync(request, ct);

                groupActivity?.SetTag("neaslator.quality.batch_success", result.IsSuccess);
                groupActivity?.SetTag("neaslator.quality.provider", result.ProviderName);
                groupActivity?.SetTag("neaslator.quality.input_tokens", result.TokenUsage.InputTokens);
                groupActivity?.SetTag("neaslator.quality.output_tokens", result.TokenUsage.OutputTokens);

                if (result.IsSuccess)
                {
                    int groupUpgraded = 0;
                    foreach (TranslatedUnit translated in result.Translations)
                    {
                        TranslationMemoryEntry? sourceEntry = group.FirstOrDefault(e => e.SourceHash == translated.SourceHash);
                        if (sourceEntry is null) continue;

                        await cache.StoreAsync(
                            translated.SourceHash,
                            sourceEntry.NormalizedSourceText,
                            group.Key.SourceLanguageCode,
                            group.Key.TargetLanguageCode,
                            translated.TranslatedName,
                            Domain.Enums.TranslationProviderTier.Primary,
                            "quality_upgrade",
                            translated.ConfidenceScore,
                            ct);
                        groupUpgraded++;
                    }

                    upgradedCount += groupUpgraded;
                    groupActivity?.SetTag("neaslator.quality.group_upgraded", groupUpgraded);
                    NeaslatorMetrics.QualityUpgradeEntriesUpgraded.Add(groupUpgraded,
                        new KeyValuePair<string, object?>("target_language", group.Key.TargetLanguageCode));
                }
                else
                {
                    failedGroupCount++;
                    groupActivity?.SetStatus(ActivityStatusCode.Error, result.ErrorMessage);
                    NeaslatorMetrics.QualityUpgradeFailures.Add(1,
                        new("target_language", group.Key.TargetLanguageCode),
                        new("error", result.ErrorMessage ?? "unknown"));
                }
            }
            catch (Exception ex)
            {
                failedGroupCount++;
                groupActivity?.AddEvent(new ActivityEvent("exception",
                    tags: new ActivityTagsCollection([
                        new("exception.type", ex.GetType().FullName ?? ex.GetType().Name),
                        new("exception.message", ex.Message)
                    ])));
                groupActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                NeaslatorMetrics.QualityUpgradeFailures.Add(1,
                    new("target_language", group.Key.TargetLanguageCode),
                    new("error", ex.GetType().Name));
                _logger.LogError(ex, "Quality upgrade failed for language {Language}", group.Key.TargetLanguageCode);
            }
        }

        double durationSeconds = Stopwatch.GetElapsedTime(startTicks).TotalSeconds;
        activity?.SetTag("neaslator.quality.upgraded", upgradedCount);
        activity?.SetTag("neaslator.quality.failed_groups", failedGroupCount);
        activity?.SetTag("neaslator.quality.duration_seconds", durationSeconds);

        if (failedGroupCount > 0 && upgradedCount == 0)
            activity?.SetStatus(ActivityStatusCode.Error, "All quality upgrade groups failed");

        _logger.LogInformation("Quality upgrade completed: {Count} entries upgraded, {Failed} groups failed in {Duration:F1}s",
            upgradedCount, failedGroupCount, durationSeconds);
    }
}
