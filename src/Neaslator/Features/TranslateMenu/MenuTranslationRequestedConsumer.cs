using System.Diagnostics;
using MassTransit;
using Neavents.Messaging.Contracts.Translation;
using Neaslator.Observability;

namespace Neaslator.Features.TranslateMenu;

/// <summary>
/// Handles an explicit "translate this menu" request from the dashboard.
///
/// Distinct from <see cref="MenuPublishedConsumer"/>, which reacts to a publish and debounces
/// so that a burst of edits collapses into one job. A person clicking translate expects it to
/// happen now, so this path does not debounce — but it goes through the same
/// <see cref="StartTranslationCommand"/> so both routes share one pipeline, diff engine and
/// translation memory. Before this existed, menu-service ran its own parallel translation job
/// whose output never reached the edge.
/// </summary>
public sealed class MenuTranslationRequestedConsumer : IConsumer<MenuTranslationRequested>
{
    private readonly IPublishEndpoint _publisher;
    private readonly ILogger<MenuTranslationRequestedConsumer> _logger;

    public MenuTranslationRequestedConsumer(
        IPublishEndpoint publisher,
        ILogger<MenuTranslationRequestedConsumer> logger)
    {
        _publisher = publisher;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<MenuTranslationRequested> context)
    {
        MenuTranslationRequested message = context.Message;

        using Activity? activity = NeaslatorActivitySources.Debounce.StartActivity("MenuTranslationRequested");
        activity?.SetTag("neaslator.menu_id", message.MenuId.ToString());
        activity?.SetTag("neaslator.owner_id", message.OwnerId.ToString());
        activity?.SetTag("neaslator.source_language", message.SourceLanguageCode);
        activity?.SetTag("neaslator.target_language", message.TargetLanguageCode ?? "all");
        activity?.SetTag("neaslator.requested_by", message.RequestedBy ?? "system");

        _logger.LogInformation(
            "Translation requested — MenuId={MenuId} Target={TargetLanguage} RequestedBy={RequestedBy}",
            message.MenuId, message.TargetLanguageCode ?? "all", message.RequestedBy ?? "system");

        // context.Publish, NOT the injected IPublishEndpoint.
        //
        // This consumer owns no DbContext and never saves. Under UseBusOutbox the scoped
        // IPublishEndpoint STAGES a message until SaveChangesAsync writes it, so publishing through it
        // here would stage StartTranslationCommand into a scope that then disposes — the command is
        // never sent, no exception, no log, and translation silently never starts.
        //
        // Publishing through the ConsumeContext instead hands the message to the inbox's own
        // transaction, which is committed when the inbox filter saves. That is the correct instrument
        // for a consumer with no unit of work of its own.
        await context.Publish(new StartTranslationCommand
        {
            MenuId = message.MenuId,
            OwnerId = message.OwnerId,

            // Carried through from the request. Without it the provider falls back to the owner,
            // which is only correct while owner and tenant are the same id.
            TenantId = message.TenantId,
            SourceLanguageCode = message.SourceLanguageCode,

            // The language the person actually picked. This was dropped: StartTranslationCommand
            // had no such field, so the request travelled this far and then the pipeline retargeted
            // every active language instead of the one asked for.
            TargetLanguageCode = message.TargetLanguageCode,

            // A person pressing Translate wants the MISSING languages produced, not the CHANGED
            // text — and on an unchanged menu the diff is empty, so the whole chain no-ops while
            // every layer reports success. See StartTranslationCommand.IgnorePreviousSnapshot.
            IgnorePreviousSnapshot = true,

            VenueType = message.VenueType,
            CuisineType = message.CuisineType,
            TriggeredAt = message.RequestedAt,
        }, context.CancellationToken);
    }
}
