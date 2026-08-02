using System.Diagnostics;
using MassTransit;
using Neavents.Messaging.Contracts.Translation;
using Neaslator.Observability;
using StackExchange.Redis;

namespace Neaslator.Features.TranslateMenu;

public sealed class MenuPublishedConsumer : IConsumer<MenuPublishedEvent>
{
    private static readonly TimeSpan _debounceWindow = TimeSpan.FromSeconds(5);
    private readonly IConnectionMultiplexer _garnet;
    private readonly IPublishEndpoint _publisher;
    private readonly ILogger<MenuPublishedConsumer> _logger;

    public MenuPublishedConsumer(
        IConnectionMultiplexer garnet,
        IPublishEndpoint publishEndpoint,
        ILogger<MenuPublishedConsumer> logger)
    {
        _garnet = garnet;
        _publisher = publishEndpoint;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<MenuPublishedEvent> context)
    {
        using Activity? activity = NeaslatorActivitySources.Debounce.StartActivity("MenuPublished.Debounce");
        activity?.SetTag("neaslator.menu_id", context.Message.MenuId.ToString());
        activity?.SetTag("neaslator.owner_id", context.Message.OwnerId.ToString());
        activity?.SetTag("neaslator.source_language", context.Message.SourceLanguageCode);
        activity?.SetTag("neaslator.venue_type", context.Message.VenueType);
        activity?.SetTag("neaslator.cuisine_type", context.Message.CuisineType);
        activity?.SetTag("neaslator.published_at", context.Message.PublishedAt.ToString("O"));

        IDatabase db = _garnet.GetDatabase();
        string debounceKey = $"neaslator:debounce:{context.Message.MenuId}";

        bool isFirst = await db.StringSetAsync(
            debounceKey,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            _debounceWindow,
            When.NotExists);

        activity?.SetTag("neaslator.debounce.is_first", isFirst);

        if (!isFirst)
        {
            await db.KeyExpireAsync(debounceKey, _debounceWindow);
            activity?.SetTag("neaslator.debounce.coalesced", true);
            activity?.AddEvent(new ActivityEvent("debounce_coalesced",
                tags: new ActivityTagsCollection([
                    new("menu_id", context.Message.MenuId.ToString()),
                    new("debounce_window_seconds", _debounceWindow.TotalSeconds)
                ])));
            NeaslatorMetrics.DebounceCoalescedTotal.Add(1,
                new KeyValuePair<string, object?>("menu_id", context.Message.MenuId.ToString()));
            _logger.LogInformation("Debounce coalesced for menu {MenuId}", context.Message.MenuId);
            return;
        }

        NeaslatorMetrics.DebounceTriggeredTotal.Add(1,
            new KeyValuePair<string, object?>("menu_id", context.Message.MenuId.ToString()));
        activity?.SetTag("neaslator.debounce.coalesced", false);
        activity?.AddEvent(new ActivityEvent("debounce_triggered",
            tags: new ActivityTagsCollection([
                new("menu_id", context.Message.MenuId.ToString()),
                new("debounce_window_seconds", _debounceWindow.TotalSeconds)
            ])));

        await context.SchedulePublish(_debounceWindow, StartTranslationCommand.From(context.Message));
    }
}

public sealed record StartTranslationCommand
{
    /// <summary>
    /// Builds the command from the event that triggered it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A named function rather than an object initialiser inside the consumer, because this mapping
    /// is a seam and the seam is what broke. <c>TenantId</c> existed on the event, on this command,
    /// and at the fetch that needs it — and was simply never copied here. Every end was individually
    /// correct, so nothing failed to compile and no assertion anywhere went red; translation just
    /// stopped working for every menu whose owner was not its tenant.
    /// </para>
    /// <para>
    /// Pulled out so a test can assert the mapping directly. The consumer's own output goes through
    /// <c>SchedulePublish</c>, an extension method that cannot be substituted, which is a large part
    /// of why this was never covered.
    /// </para>
    /// </remarks>
    public static StartTranslationCommand From(MenuPublishedEvent message) => new()
    {
        MenuId = message.MenuId,
        OwnerId = message.OwnerId,
        TenantId = message.TenantId,
        SourceLanguageCode = message.SourceLanguageCode,
        VenueType = message.VenueType,
        CuisineType = message.CuisineType,
        TriggeredAt = message.PublishedAt,
    };

    public required Ulid MenuId { get; init; }
    public required Ulid OwnerId { get; init; }

    /// <summary>
    /// The organisation the menu belongs to, which is NOT the owner.
    /// </summary>
    /// <remarks>
    /// The owner is the venue or event; the tenant is the organisation that owns it. The two were
    /// identical on every menu in the estate, so the owner was sent as the tenant header and it
    /// worked by coincidence. The first menu that genuinely belonged to a venue inside an
    /// organisation made the header name a tenant owning nothing, and the menu fetch answered 404.
    ///
    /// Nullable: a publisher that predates the field omits it, and the provider falls back to the
    /// owner — the previous behaviour rather than a hard failure.
    /// </remarks>
    public Ulid? TenantId { get; init; }

    public required string SourceLanguageCode { get; init; }
    public required string VenueType { get; init; }
    public required string CuisineType { get; init; }
    public required DateTimeOffset TriggeredAt { get; init; }
}
