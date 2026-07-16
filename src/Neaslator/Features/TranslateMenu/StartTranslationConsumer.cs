using System.Diagnostics;
using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Neavents.Messaging.Contracts.Translation;
using Neaslator.Domain.Entities;
using Neaslator.Domain.Enums;
using Neaslator.Infrastructure.Cache;
using Neaslator.Infrastructure.Diff;
using Neaslator.Infrastructure.Hashing;
using Neaslator.Infrastructure.MenuData;
using Neaslator.Infrastructure.Normalization;
using Neaslator.Infrastructure.Notifications;
using Neaslator.Observability;
using Neaslator.Persistence;

namespace Neaslator.Features.TranslateMenu;

public sealed class StartTranslationConsumer : IConsumer<StartTranslationCommand>
{
    private readonly TranslationPipeline _pipeline;
    private readonly NeaslatorDbContext _db;
    private readonly IPublishEndpoint _publisher;
    private readonly TranslationNotifier _notifier;
    private readonly IMenuDataProvider _menuDataProvider;
    private readonly ITranslationCache _cache;
    private readonly ILogger<StartTranslationConsumer> _logger;

    public StartTranslationConsumer(
        TranslationPipeline pipeline,
        NeaslatorDbContext dbContext,
        IPublishEndpoint publishEndpoint,
        TranslationNotifier notifier,
        IMenuDataProvider menuDataProvider,
        ITranslationCache cache,
        ILogger<StartTranslationConsumer> logger)
    {
        _pipeline = pipeline;
        _db = dbContext;
        _publisher = publishEndpoint;
        _notifier = notifier;
        _menuDataProvider = menuDataProvider;
        _cache = cache;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<StartTranslationCommand> context)
    {
        long sagaStartTicks = Stopwatch.GetTimestamp();
        StartTranslationCommand command = context.Message;

        using Activity? activity = NeaslatorActivitySources.Saga.StartActivity("StartTranslation");
        activity?.SetTag("neaslator.menu_id", command.MenuId.ToString());
        activity?.SetTag("neaslator.owner_id", command.OwnerId.ToString());
        activity?.SetTag("neaslator.source_language", command.SourceLanguageCode);
        activity?.SetTag("neaslator.venue_type", command.VenueType);
        activity?.SetTag("neaslator.cuisine_type", command.CuisineType);
        activity?.SetTag("neaslator.triggered_at", command.TriggeredAt.ToString("O"));
        activity?.SetTag("neaslator.delay_from_trigger_ms", (DateTimeOffset.UtcNow - command.TriggeredAt).TotalMilliseconds);

        try
        {
            _logger.LogInformation("Starting translation for menu {MenuId}", command.MenuId);

            await _notifier.NotifyOwnerAsync(command.OwnerId, new TranslationStatusNotification(
                command.MenuId,
                TranslationNotificationType.Started,
                0, 0, 0));
            NeaslatorMetrics.NotificationsSent.Add(1, new KeyValuePair<string, object?>("type", "started"));

            MenuSnapshot? currentSnapshot;
            using (Activity? fetchActivity = NeaslatorActivitySources.Saga.StartActivity("fetch_menu_snapshot"))
            {
                currentSnapshot = await _menuDataProvider.GetMenuSnapshotAsync(command.MenuId, context.CancellationToken);
                fetchActivity?.SetTag("neaslator.snapshot_found", currentSnapshot is not null);
                if (currentSnapshot is not null)
                {
                    fetchActivity?.SetTag("neaslator.snapshot.sections", currentSnapshot.Sections.Count);
                    fetchActivity?.SetTag("neaslator.snapshot.items", currentSnapshot.Sections.Sum(s => s.Items.Count));
                }
            }

            if (currentSnapshot is null)
            {
                _logger.LogError("Failed to retrieve menu snapshot for menu {MenuId}", command.MenuId);
                activity?.SetStatus(ActivityStatusCode.Error, "Menu snapshot not found");
                activity?.AddEvent(new ActivityEvent("menu_snapshot_not_found"));
                return;
            }

            MenuPublishSnapshot? previousSnapshotEntity = await _db.MenuPublishSnapshots
                .FirstOrDefaultAsync(s => s.MenuId == command.MenuId, context.CancellationToken);

            MenuSnapshot? previousSnapshot = previousSnapshotEntity is not null
                ? JsonSerializer.Deserialize<MenuSnapshot>(previousSnapshotEntity.SnapshotJson)
                : null;

            activity?.SetTag("neaslator.has_previous_snapshot", previousSnapshot is not null);

            TranslationPipelineResult result;
            using (Activity? pipelineActivity = NeaslatorActivitySources.Saga.StartActivity("execute_pipeline"))
            {
                result = await _pipeline.ExecuteAsync(
                    currentSnapshot,
                    previousSnapshot,
                    command.SourceLanguageCode,
                    command.VenueType,
                    command.CuisineType,
                    context.CancellationToken);

                pipelineActivity?.SetTag("neaslator.result.total", result.TotalLanguages);
                pipelineActivity?.SetTag("neaslator.result.completed", result.CompletedLanguages);
                pipelineActivity?.SetTag("neaslator.result.failed", result.FailedLanguages);
            }

            using (Activity? snapshotActivity = NeaslatorActivitySources.Saga.StartActivity("save_snapshot"))
            {
                string snapshotJson = JsonSerializer.Serialize(currentSnapshot);
                if (previousSnapshotEntity is not null)
                {
                    previousSnapshotEntity.SnapshotJson = snapshotJson;
                    previousSnapshotEntity.PublishedAt = DateTimeOffset.UtcNow;
                    snapshotActivity?.SetTag("neaslator.snapshot_action", "updated");
                }
                else
                {
                    _db.MenuPublishSnapshots.Add(new MenuPublishSnapshot
                    {
                        MenuId = command.MenuId,
                        OwnerId = command.OwnerId,
                        SnapshotJson = snapshotJson,
                        PublishedAt = DateTimeOffset.UtcNow
                    });
                    snapshotActivity?.SetTag("neaslator.snapshot_action", "created");
                }
                await _db.SaveChangesAsync(context.CancellationToken);
            }

            List<TranslatedMenuLanguage> translatedMenus = [];
            using (Activity? assembleActivity = NeaslatorActivitySources.Saga.StartActivity("assemble_translations"))
            {
                foreach (LanguageResult langResult in result.Results.Where(r => r.IsSuccess))
                {
                    List<TranslatedSectionData> translatedSections = [];
                    foreach (SectionSnapshot section in currentSnapshot.Sections)
                    {
                        string sectionNameNorm = TextNormalizer.Normalize(section.Name);
                        long sectionNameHash = TranslationHasher.ComputeHash(sectionNameNorm);
                        IReadOnlyList<CacheLookupResult> sectionLookup = await _cache.LookupAsync(sectionNameHash, sectionNameNorm, command.SourceLanguageCode, [langResult.TargetLanguageCode], context.CancellationToken);
                        string translatedSectionName = sectionLookup.FirstOrDefault(r => r.Translation is not null)?.Translation?.TranslatedText ?? section.Name;

                        List<TranslatedItemData> translatedItems = [];
                        foreach (ItemSnapshot item in section.Items)
                        {
                            string nameNorm = TextNormalizer.Normalize(item.Name);
                            long nameHash = TranslationHasher.ComputeHash(nameNorm);
                            IReadOnlyList<CacheLookupResult> nameLookup = await _cache.LookupAsync(nameHash, nameNorm, command.SourceLanguageCode, [langResult.TargetLanguageCode], context.CancellationToken);
                            string translatedName = nameLookup.FirstOrDefault(r => r.Translation is not null)?.Translation?.TranslatedText ?? item.Name;

                            string? translatedDescription = null;
                            if (!string.IsNullOrEmpty(item.Description))
                            {
                                string descNorm = TextNormalizer.Normalize(item.Description.AsSpan());
                                long descHash = TranslationHasher.ComputeHash(descNorm);
                                IReadOnlyList<CacheLookupResult> descLookup = await _cache.LookupAsync(descHash, descNorm, command.SourceLanguageCode, [langResult.TargetLanguageCode], context.CancellationToken);
                                translatedDescription = descLookup.FirstOrDefault(r => r.Translation is not null)?.Translation?.TranslatedText ?? item.Description;
                            }

                            translatedItems.Add(new TranslatedItemData { ItemId = item.Id, TranslatedName = translatedName, TranslatedDescription = translatedDescription });
                        }
                        translatedSections.Add(new TranslatedSectionData { SectionId = section.Id, TranslatedName = translatedSectionName, Items = translatedItems });
                    }
                    translatedMenus.Add(new TranslatedMenuLanguage { LanguageCode = langResult.TargetLanguageCode, Sections = translatedSections });
                }
                assembleActivity?.SetTag("neaslator.assembled_languages", translatedMenus.Count);
            }

            List<string> failedLanguageCodes = result.Results
                .Where(r => !r.IsSuccess)
                .Select(r => r.TargetLanguageCode)
                .ToList();

            using (Activity? publishActivity = NeaslatorActivitySources.Saga.StartActivity("publish_completion_event"))
            {
                publishActivity?.SetTag("neaslator.event.completed_languages", result.CompletedLanguages);
                publishActivity?.SetTag("neaslator.event.failed_languages", result.FailedLanguages);

                await _publisher.Publish(new MenuTranslationCompletedEvent
                {
                    MenuId = command.MenuId,
                    OwnerId = command.OwnerId,
                    SourceLanguageCode = command.SourceLanguageCode,
                    TotalLanguages = result.TotalLanguages,
                    CompletedLanguages = result.CompletedLanguages,
                    FailedLanguages = result.FailedLanguages,
                    FailedLanguageCodes = failedLanguageCodes,
                    TranslatedMenus = translatedMenus,
                    CompletedAt = DateTimeOffset.UtcNow
                }, context.CancellationToken);
            }

            TranslationNotificationType notificationType = result.FailedLanguages > 0
                ? result.CompletedLanguages > 0
                    ? TranslationNotificationType.PartiallyCompleted
                    : TranslationNotificationType.Failed
                : TranslationNotificationType.Completed;

            string? errorSummary = result.FailedLanguages > 0
                ? string.Join(", ", result.Results.Where(r => !r.IsSuccess).Select(r => r.TargetLanguageCode))
                : null;

            await _notifier.NotifyOwnerAsync(command.OwnerId, new TranslationStatusNotification(
                command.MenuId,
                notificationType,
                result.TotalLanguages,
                result.CompletedLanguages,
                result.FailedLanguages,
                errorSummary));
            NeaslatorMetrics.NotificationsSent.Add(1, new KeyValuePair<string, object?>("type", notificationType.ToString().ToLowerInvariant()));

            double sagaDuration = Stopwatch.GetElapsedTime(sagaStartTicks).TotalSeconds;
            NeaslatorMetrics.SagaDurationSeconds.Record(sagaDuration,
                new("menu_id", command.MenuId.ToString()),
                new("result", notificationType.ToString().ToLowerInvariant()));

            activity?.SetTag("neaslator.result.completed", result.CompletedLanguages);
            activity?.SetTag("neaslator.result.failed", result.FailedLanguages);
            activity?.SetTag("neaslator.result.total", result.TotalLanguages);
            activity?.SetTag("neaslator.saga_duration_seconds", sagaDuration);

            _logger.LogInformation(
                "Translation completed for menu {MenuId}: {Completed}/{Total} languages, {Failed} failed",
                command.MenuId, result.CompletedLanguages, result.TotalLanguages, result.FailedLanguages);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddEvent(new ActivityEvent("exception",
                tags: new ActivityTagsCollection([
                    new("exception.type", ex.GetType().FullName ?? ex.GetType().Name),
                    new("exception.message", ex.Message)
                ])));

            double sagaDuration = Stopwatch.GetElapsedTime(sagaStartTicks).TotalSeconds;
            NeaslatorMetrics.SagaDurationSeconds.Record(sagaDuration,
                new("menu_id", command.MenuId.ToString()),
                new("result", "exception"));

            throw;
        }
    }
}
