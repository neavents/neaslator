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
                currentSnapshot = await _menuDataProvider.GetMenuSnapshotAsync(command.MenuId, command.OwnerId, command.TenantId, context.CancellationToken);
                fetchActivity?.SetTag("neaslator.snapshot_found", currentSnapshot is not null);
                if (currentSnapshot is not null)
                {
                    fetchActivity?.SetTag("neaslator.snapshot.sections", currentSnapshot.Sections.Count);
                    fetchActivity?.SetTag("neaslator.snapshot.items", currentSnapshot.Sections.Sum(s => s.Items.Count));
                }
            }

            if (currentSnapshot is null)
            {
                // Throw, do not return.
                //
                // This logged an error and returned, so MassTransit acknowledged the message and it
                // was never retried or dead-lettered. Every reason the fetch fails is transient or
                // fixable — menu-service restarting, a missing internal key, the wrong tenant — and
                // all of them ended as one line in a log nobody was reading, on a request that had
                // already answered 202 to the person who asked for it.
                activity?.SetStatus(ActivityStatusCode.Error, "Menu snapshot not found");
                activity?.AddEvent(new ActivityEvent("menu_snapshot_not_found"));

                throw new InvalidOperationException(
                    $"Could not read menu {command.MenuId} from menu-service (owner {command.OwnerId}, "
                    + $"tenant {command.TenantId?.ToString() ?? "none"}). Translation cannot start.");
            }

            MenuPublishSnapshot? previousSnapshotEntity = await _db.MenuPublishSnapshots
                .FirstOrDefaultAsync(s => s.MenuId == command.MenuId, context.CancellationToken);

            MenuSnapshot? previousSnapshot = previousSnapshotEntity is not null
                ? JsonSerializer.Deserialize<MenuSnapshot>(previousSnapshotEntity.SnapshotJson)
                : null;

            // An explicit request asks for the whole menu, so the diff has nothing to subtract.
            // See StartTranslationCommand.IgnorePreviousSnapshot for why a diff makes "Translate"
            // a permanent no-op once a snapshot exists.
            if (command.IgnorePreviousSnapshot)
            {
                previousSnapshot = null;
            }

            activity?.SetTag("neaslator.has_previous_snapshot", previousSnapshot is not null);
            activity?.SetTag("neaslator.ignore_previous_snapshot", command.IgnorePreviousSnapshot);
            activity?.SetTag("neaslator.target_language", command.TargetLanguageCode ?? "all");

            TranslationPipelineResult result;
            using (Activity? pipelineActivity = NeaslatorActivitySources.Saga.StartActivity("execute_pipeline"))
            {
                result = await _pipeline.ExecuteAsync(
                    currentSnapshot,
                    previousSnapshot,
                    command.SourceLanguageCode,
                    command.VenueType,
                    command.CuisineType,
                    context.CancellationToken,
                    command.TargetLanguageCode);

                pipelineActivity?.SetTag("neaslator.result.total", result.TotalLanguages);
                pipelineActivity?.SetTag("neaslator.result.completed", result.CompletedLanguages);
                pipelineActivity?.SetTag("neaslator.result.failed", result.FailedLanguages);
            }

            // The snapshot only advances when something actually landed.
            //
            // It used to be written unconditionally, right here, whatever the pipeline returned —
            // so a run that translated nothing still recorded "this text has been dealt with". The
            // next run diffed against it, found no changes, and returned 0/0. That state is
            // unrecoverable by pressing the button again, which is exactly the report: the toast is
            // green, the log says no_changes_detected, and no translation exists.
            //
            // A run whose languages all failed leaves the previous snapshot in place, so the next
            // attempt sees the same work to do.
            bool anythingLanded = result.CompletedLanguages > 0;
            activity?.SetTag("neaslator.snapshot_advanced", anythingLanded);

            using (Activity? snapshotActivity = NeaslatorActivitySources.Saga.StartActivity("save_snapshot"))
            {
                string snapshotJson = JsonSerializer.Serialize(currentSnapshot);
                if (!anythingLanded)
                {
                    snapshotActivity?.SetTag("neaslator.snapshot_action", "skipped");
                    _logger.LogWarning(
                        "Translation for menu {MenuId} completed no languages ({Failed} failed of {Total}); "
                        + "leaving the previous snapshot in place so a retry still has work to do.",
                        command.MenuId, result.FailedLanguages, result.TotalLanguages);
                }
                else if (previousSnapshotEntity is not null)
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
                    // The menu's own title and description, looked up exactly like every other
                    // string: normalise, hash, ask the cache. They were absent from this assembly
                    // entirely, so TranslatedMenuLanguage carried only sections and the consumer
                    // downstream had no choice but to store the source title against all 29 target
                    // languages.
                    //
                    // A miss leaves the field null rather than falling back to the source text. The
                    // fallbacks below do fall back, because a section with no name renders as a gap
                    // in the menu; a null title means "nothing new for this field", which lets the
                    // consumer keep what it already has instead of overwriting a translation an
                    // owner may have corrected by hand with the untranslated original.
                    string? translatedMenuName =
                        await LookupOrNullAsync(currentSnapshot.Name, command.SourceLanguageCode,
                            langResult.TargetLanguageCode, context.CancellationToken);

                    string? translatedMenuDescription =
                        await LookupOrNullAsync(currentSnapshot.Description, command.SourceLanguageCode,
                            langResult.TargetLanguageCode, context.CancellationToken);

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

                            // Sub-items go into the SAME Items list, not a nested one.
                            //
                            // DiffEngine has always sent sub-items to the provider — AddSubItemUnits
                            // sits right beside AddItemUnits — so they were translated, paid for at
                            // the LLM and written to the translation memory. This loop then assembled
                            // the completion event from section.Items alone, so not one of those
                            // translations ever reached the wire. Every portion, size and variant on
                            // every menu rendered in the source language, in every language, and
                            // nothing reported a problem: the payload was valid and the item names
                            // above it were correct.
                            //
                            // A flat list is right rather than a shortcut. qrmenu-edge's parser gives
                            // each sub-item its own dense id and its own SourceIds entry
                            // (PublicMenuParser records subEl exactly as it records itemEl), and
                            // MenuSyncDataTranslator resolves every entry in this list against that
                            // dictionary. So a sub-item keyed by its own ULID is translated by the
                            // code already there — no contract change, and no edge change.
                            foreach (SubItemSnapshot subItem in item.SubItems)
                            {
                                string subNameNorm = TextNormalizer.Normalize(subItem.Name);
                                long subNameHash = TranslationHasher.ComputeHash(subNameNorm);
                                IReadOnlyList<CacheLookupResult> subNameLookup = await _cache.LookupAsync(subNameHash, subNameNorm, command.SourceLanguageCode, [langResult.TargetLanguageCode], context.CancellationToken);
                                string translatedSubName = subNameLookup.FirstOrDefault(r => r.Translation is not null)?.Translation?.TranslatedText ?? subItem.Name;

                                string? translatedSubDescription = null;
                                if (!string.IsNullOrEmpty(subItem.Description))
                                {
                                    string subDescNorm = TextNormalizer.Normalize(subItem.Description.AsSpan());
                                    long subDescHash = TranslationHasher.ComputeHash(subDescNorm);
                                    IReadOnlyList<CacheLookupResult> subDescLookup = await _cache.LookupAsync(subDescHash, subDescNorm, command.SourceLanguageCode, [langResult.TargetLanguageCode], context.CancellationToken);
                                    translatedSubDescription = subDescLookup.FirstOrDefault(r => r.Translation is not null)?.Translation?.TranslatedText ?? subItem.Description;
                                }

                                translatedItems.Add(new TranslatedItemData
                                {
                                    ItemId = subItem.Id,
                                    TranslatedName = translatedSubName,
                                    TranslatedDescription = translatedSubDescription,
                                    // Flattened into the same list on purpose — qrmenu-edge resolves
                                    // every id through one source map — but SAID so, because the
                                    // other consumer stores these in a different table with a
                                    // foreign key. Without the flag it wrote them as dishes, the
                                    // key rejected them, and the whole menu's translations rolled
                                    // back: an add-on cost a menu every language it had.
                                    IsSubItem = true,
                                });
                            }
                        }
                        translatedSections.Add(new TranslatedSectionData { SectionId = section.Id, TranslatedName = translatedSectionName, Items = translatedItems });
                    }
                    translatedMenus.Add(new TranslatedMenuLanguage
                    {
                        LanguageCode = langResult.TargetLanguageCode,
                        TranslatedName = translatedMenuName,
                        TranslatedDescription = translatedMenuDescription,
                        Sections = translatedSections,
                    });
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

                // Flush the outbox.
                //
                // UseBusOutbox makes the scoped IPublishEndpoint STAGE this message and hold it until
                // SaveChangesAsync writes it to the outbox table. Nothing after this point saves, so
                // without this line the event is staged into a scope that then disposes and is never
                // sent — no exception, no log, a perfectly successful consume. Staging cannot fail, so
                // there is nothing to catch either.
                //
                // The publish cannot simply move above the earlier save at the snapshot step: the payload
                // is assembled from the translation results, which are computed after it. An explicit
                // save purely to flush the outbox is the same resolution identity used for its publish
                // sites that write no rows of their own.
                //
                // This exact defect was live in subscription at four sites, one of which meant a customer
                // per batch was invoiced and never charged. See OutboxPublishOrderingTests.
                await _db.SaveChangesAsync(context.CancellationToken);
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

    /// <summary>
    /// The translation of <paramref name="sourceText"/>, or null when there is not one.
    /// </summary>
    /// <remarks>
    /// Null rather than the source text, which is the opposite of what the section and item lookups
    /// do — and deliberately so. A section or item with no name renders as a visible gap, so falling
    /// back to the original is the lesser harm there. The menu title is a single field on a record
    /// whose null already means "no news", and a downstream consumer that receives the source text
    /// cannot tell it apart from a genuine translation that happens to be identical. It would then
    /// overwrite whatever is stored — including a correction an owner made by hand.
    /// </remarks>
    private async Task<string?> LookupOrNullAsync(
        string? sourceText,
        string sourceLanguageCode,
        string targetLanguageCode,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            return null;
        }

        string normalized = TextNormalizer.Normalize(sourceText.AsSpan());
        long hash = TranslationHasher.ComputeHash(normalized);

        IReadOnlyList<CacheLookupResult> lookup = await _cache
            .LookupAsync(hash, normalized, sourceLanguageCode, [targetLanguageCode], cancellationToken)
            .ConfigureAwait(false);

        return lookup.FirstOrDefault(r => r.Translation is not null)?.Translation?.TranslatedText;
    }
}
