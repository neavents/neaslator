using System.Reflection;
using Neaslator.Features.TranslateMenu;
using Neavents.Messaging.Contracts.Translation;
using Xunit;

namespace Neaslator.Tests.Consumers;

/// <summary>
/// That what arrives on the event still exists by the time the work starts.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the test that was missing on 2026-08-02.</b> Menu translation failed on every publish
/// of every menu whose owner was not its tenant, and the cause was not a wrong value — it was a value
/// that stopped existing halfway. <c>MenuPublishedEvent</c> carried a tenant,
/// <c>StartTranslationCommand</c> had a field for it, and the fetch already passed it to
/// menu-service. <c>MenuPublishedConsumer</c> simply never copied it across.
/// </para>
/// <para>
/// Nothing caught that, and nothing could: each end was individually correct, so there was no
/// compile error and no failing assertion anywhere. The consumer is the seam, and the seam had no
/// test.
/// </para>
/// <para>
/// The reflection test below is deliberately general rather than a list of fields. A named-field
/// assertion would have to be remembered every time the event grows, which is exactly the discipline
/// that failed here — the field was added and the copy was forgotten. This one fails the moment a
/// correspondingly named property stops being carried, whatever it is called.
/// </para>
/// </remarks>
public sealed class EventFieldsSurviveTheHopTests
{
    private static MenuPublishedEvent Event() => new()
    {
        MenuId = Ulid.NewUlid(),
        OwnerId = Ulid.NewUlid(),
        TenantId = Ulid.NewUlid(),
        PublishedAt = DateTimeOffset.UtcNow,
        SourceLanguageCode = "tr",
        VenueType = "Restaurant",
        CuisineType = "Turkish",
    };

    [Fact]
    public void The_tenant_reaches_the_translation_command()
    {
        // The specific regression. Without it the provider falls back to the owner, menu-service's
        // tenant filter matches nothing, and the fetch answers 404 — after the publisher has already
        // been told 200, so nobody who pressed publish ever sees it.
        MenuPublishedEvent message = Event();

        StartTranslationCommand scheduled = StartTranslationCommand.From(message);

        Assert.Equal(message.TenantId, scheduled.TenantId);
    }

    [Fact]
    public void Every_field_the_command_shares_with_the_event_is_carried()
    {
        // General on purpose. A named-field list would have to be remembered whenever the event
        // grows, and forgetting exactly that is what broke translation estate-wide.
        MenuPublishedEvent message = Event();
        StartTranslationCommand scheduled = StartTranslationCommand.From(message);

        PropertyInfo[] commandProperties = typeof(StartTranslationCommand).GetProperties();
        List<string> dropped = [];

        foreach (PropertyInfo eventProperty in typeof(MenuPublishedEvent).GetProperties())
        {
            PropertyInfo? match = Array.Find(
                commandProperties, c => c.Name == eventProperty.Name && c.PropertyType == eventProperty.PropertyType);
            if (match is null) continue;   // the command genuinely does not model it

            object? from = eventProperty.GetValue(message);
            object? to = match.GetValue(scheduled);

            if (!Equals(from, to)) dropped.Add($"{eventProperty.Name}: sent {from}, arrived {to}");
        }

        Assert.True(
            dropped.Count == 0,
            "Fields present on both the event and the command must be copied across. Dropped:\n  "
            + string.Join("\n  ", dropped));
    }

    [Fact]
    public void A_publisher_that_sends_no_tenant_is_carried_through_as_null()
    {
        // Producers older than contracts 1.28.0 omit it, and the consumer must pass the absence on
        // rather than substituting the owner here — the fallback belongs at the fetch, where it can
        // be seen and removed later.
        MenuPublishedEvent message = Event() with { TenantId = null };

        StartTranslationCommand scheduled = StartTranslationCommand.From(message);

        Assert.Null(scheduled.TenantId);
    }
}
