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

        await context.SchedulePublish(
            _debounceWindow,
            new StartTranslationCommand
            {
                MenuId = context.Message.MenuId,
                OwnerId = context.Message.OwnerId,
                SourceLanguageCode = context.Message.SourceLanguageCode,
                VenueType = context.Message.VenueType,
                CuisineType = context.Message.CuisineType,
                TriggeredAt = context.Message.PublishedAt
            });
    }
}

public sealed record StartTranslationCommand
{
    public required Ulid MenuId { get; init; }
    public required Ulid OwnerId { get; init; }
    public required string SourceLanguageCode { get; init; }
    public required string VenueType { get; init; }
    public required string CuisineType { get; init; }
    public required DateTimeOffset TriggeredAt { get; init; }
}
