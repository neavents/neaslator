using System.Text.Json;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Neavents.Messaging.Contracts.Translation;
using Neaslator.Domain.Entities;
using Neaslator.Domain.Enums;
using Neaslator.Features.TranslateMenu;
using Neaslator.Infrastructure.Cache;
using Neaslator.Infrastructure.Diff;
using Neaslator.Infrastructure.MenuData;
using Neaslator.Infrastructure.Notifications;
using Neaslator.Infrastructure.Providers;
using Neaslator.Persistence;
using NSubstitute;

namespace Neaslator.Tests.Consumers;

/// <summary>
/// End-to-end coverage of the translation saga (<see cref="StartTranslationConsumer"/>) via the
/// MassTransit in-memory harness with a fully-cached pipeline, so the flow runs without a live
/// LLM: fetch snapshot -> diff -> resolve cache -> persist snapshot -> assemble -> publish
/// MenuTranslationCompletedEvent -> notify. Also covers the snapshot-not-found short circuit.
/// </summary>
public sealed class StartTranslationConsumerTests
{
    private readonly IMenuDataProvider _menuData = Substitute.For<IMenuDataProvider>();
    private readonly ITranslationCache _cache = Substitute.For<ITranslationCache>();
    private readonly ITranslationRouter _router = Substitute.For<ITranslationRouter>();
    private readonly IClientProxy _clientProxy = Substitute.For<IClientProxy>();

    private ServiceProvider BuildHarness(string dbName)
    {
        IHubContext<TranslationHub> hub = Substitute.For<IHubContext<TranslationHub>>();
        IHubClients clients = Substitute.For<IHubClients>();
        hub.Clients.Returns(clients);
        clients.Group(Arg.Any<string>()).Returns(_clientProxy);

        return new ServiceCollection()
            .AddLogging()
            .AddDbContext<NeaslatorDbContext>(o => o.UseInMemoryDatabase(dbName))
            .AddSingleton(_menuData)
            .AddSingleton(_cache)
            .AddSingleton(_router)
            .AddSingleton(hub)
            .AddScoped<TranslationNotifier>()
            .AddScoped<TranslationPipeline>()
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<StartTranslationConsumer>();
                x.UsingInMemory((context, cfg) => cfg.ConfigureEndpoints(context));
            })
            .BuildServiceProvider(true);
    }

    private static async Task SeedLanguages(ServiceProvider provider, params string[] codes)
    {
        using IServiceScope scope = provider.CreateScope();
        NeaslatorDbContext db = scope.ServiceProvider.GetRequiredService<NeaslatorDbContext>();
        short order = 0;
        foreach (string code in codes)
            db.SupportedLanguages.Add(new SupportedLanguage { Code = code, EnglishName = code, NativeName = code, IsActive = true, SortOrder = order++ });
        await db.SaveChangesAsync();
    }

    private void CacheReturnsHitsForEverything()
    {
        _cache.LookupAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                string text = ci.ArgAt<string>(1);
                IReadOnlyList<string> targets = ci.ArgAt<IReadOnlyList<string>>(3);
                return (IReadOnlyList<CacheLookupResult>)targets
                    .Select(t => new CacheLookupResult(t,
                        new CachedTranslation($"{text}::{t}", TranslationProviderTier.Primary, 1f, text),
                        CacheSource.L1Garnet))
                    .ToList();
            });
    }

    private static MenuSnapshot SingleItemSnapshot(Ulid sectionId, Ulid itemId) => new()
    {
        Sections =
        [
            new SectionSnapshot
            {
                Id = sectionId,
                Name = "Starters",
                Items = [new ItemSnapshot { Id = itemId, Name = "Soup", Description = "Tomato soup" }]
            }
        ]
    };

    private static StartTranslationCommand Command(Ulid menuId, Ulid ownerId) => new()
    {
        MenuId = menuId,
        OwnerId = ownerId,
        SourceLanguageCode = "en",
        VenueType = "Restaurant",
        CuisineType = "Italian",
        TriggeredAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task HappyPath_PublishesCompletedEvent_SavesSnapshot_Notifies()
    {
        Ulid menuId = Ulid.NewUlid();
        Ulid ownerId = Ulid.NewUlid();
        Ulid sectionId = Ulid.NewUlid();
        Ulid itemId = Ulid.NewUlid();

        _menuData.GetMenuSnapshotAsync(menuId, Arg.Any<CancellationToken>())
            .Returns(SingleItemSnapshot(sectionId, itemId));
        CacheReturnsHitsForEverything();

        await using ServiceProvider provider = BuildHarness($"saga-happy-{menuId}");
        await SeedLanguages(provider, "fr", "de");
        ITestHarness harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish(Command(menuId, ownerId));

            (await harness.Consumed.Any<StartTranslationCommand>()).Should().BeTrue();
            (await harness.Published.Any<MenuTranslationCompletedEvent>()).Should().BeTrue();

            IPublishedMessage<MenuTranslationCompletedEvent> published =
                harness.Published.Select<MenuTranslationCompletedEvent>().First();
            MenuTranslationCompletedEvent evt = published.Context.Message;
            evt.MenuId.Should().Be(menuId);
            evt.OwnerId.Should().Be(ownerId);
            evt.TotalLanguages.Should().Be(2);
            evt.CompletedLanguages.Should().Be(2);
            evt.FailedLanguages.Should().Be(0);
            evt.TranslatedMenus.Should().HaveCount(2);
            evt.TranslatedMenus.Should().OnlyContain(m => m.Sections.Count == 1 && m.Sections[0].Items.Count == 1);

            // Router is never touched on a full cache hit.
            await _router.DidNotReceive().TranslateAsync(Arg.Any<TranslationBatchRequest>(), Arg.Any<CancellationToken>());

            // Snapshot persisted exactly once for this menu.
            using IServiceScope scope = provider.CreateScope();
            NeaslatorDbContext db = scope.ServiceProvider.GetRequiredService<NeaslatorDbContext>();
            (await db.MenuPublishSnapshots.CountAsync(s => s.MenuId == menuId)).Should().Be(1);

            // Started + Completed notifications were sent to the owner group.
            await _clientProxy.ReceivedWithAnyArgs(2).SendCoreAsync(default!, default!, default);
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task PartialFailure_ReportsCompletedAndFailedLanguages()
    {
        Ulid menuId = Ulid.NewUlid();
        Ulid ownerId = Ulid.NewUlid();
        Ulid sectionId = Ulid.NewUlid();
        Ulid itemId = Ulid.NewUlid();

        _menuData.GetMenuSnapshotAsync(menuId, Arg.Any<CancellationToken>())
            .Returns(SingleItemSnapshot(sectionId, itemId));

        // fr resolves from cache; de misses and is sent to the provider.
        _cache.LookupAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                string text = ci.ArgAt<string>(1);
                IReadOnlyList<string> targets = ci.ArgAt<IReadOnlyList<string>>(3);
                return (IReadOnlyList<CacheLookupResult>)targets.Select(t =>
                    t == "fr"
                        ? new CacheLookupResult(t, new CachedTranslation($"{text}::fr", TranslationProviderTier.Primary, 1f, text), CacheSource.L1Garnet)
                        : new CacheLookupResult(t, null, CacheSource.Miss)).ToList();
            });

        // The provider (only hit for de) fails.
        _router.TranslateAsync(Arg.Any<TranslationBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TranslationBatchResult
            {
                IsSuccess = false,
                Translations = [],
                TokenUsage = new TokenUsage(0, 0, 0),
                ErrorMessage = "provider down"
            });

        await using ServiceProvider provider = BuildHarness($"saga-partial-{menuId}");
        await SeedLanguages(provider, "fr", "de");
        ITestHarness harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish(Command(menuId, ownerId));

            (await harness.Consumed.Any<StartTranslationCommand>()).Should().BeTrue();
            (await harness.Published.Any<MenuTranslationCompletedEvent>()).Should().BeTrue();

            MenuTranslationCompletedEvent evt = harness.Published.Select<MenuTranslationCompletedEvent>().First().Context.Message;
            evt.TotalLanguages.Should().Be(2);
            evt.CompletedLanguages.Should().Be(1);
            evt.FailedLanguages.Should().Be(1);
            evt.FailedLanguageCodes.Should().ContainSingle().And.Contain("de");
            evt.TranslatedMenus.Should().ContainSingle(m => m.LanguageCode == "fr");
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task ExistingSnapshotIdenticalToCurrent_NoChanges_UpdatesInPlaceAndReportsZero()
    {
        Ulid menuId = Ulid.NewUlid();
        Ulid ownerId = Ulid.NewUlid();
        Ulid sectionId = Ulid.NewUlid();
        Ulid itemId = Ulid.NewUlid();
        MenuSnapshot snapshot = SingleItemSnapshot(sectionId, itemId);

        _menuData.GetMenuSnapshotAsync(menuId, Arg.Any<CancellationToken>()).Returns(snapshot);
        CacheReturnsHitsForEverything();

        await using ServiceProvider provider = BuildHarness($"saga-nochange-{menuId}");
        await SeedLanguages(provider, "fr", "de");

        // Pre-existing snapshot identical to the current one -> diff is empty.
        DateTimeOffset seededAt = DateTimeOffset.UtcNow.AddDays(-1);
        using (IServiceScope seed = provider.CreateScope())
        {
            NeaslatorDbContext seedDb = seed.ServiceProvider.GetRequiredService<NeaslatorDbContext>();
            seedDb.MenuPublishSnapshots.Add(new MenuPublishSnapshot
            {
                MenuId = menuId,
                OwnerId = ownerId,
                SnapshotJson = JsonSerializer.Serialize(snapshot),
                PublishedAt = seededAt
            });
            await seedDb.SaveChangesAsync();
        }

        ITestHarness harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish(Command(menuId, ownerId));

            (await harness.Consumed.Any<StartTranslationCommand>()).Should().BeTrue();
            (await harness.Published.Any<MenuTranslationCompletedEvent>()).Should().BeTrue();

            MenuTranslationCompletedEvent evt = harness.Published.Select<MenuTranslationCompletedEvent>().First().Context.Message;
            evt.TotalLanguages.Should().Be(0, "an identical snapshot yields no diff");
            evt.CompletedLanguages.Should().Be(0);
            evt.FailedLanguages.Should().Be(0);
            evt.TranslatedMenus.Should().BeEmpty();

            // The existing row is updated in place (not duplicated) and its timestamp advances.
            using IServiceScope scope = provider.CreateScope();
            NeaslatorDbContext db = scope.ServiceProvider.GetRequiredService<NeaslatorDbContext>();
            List<MenuPublishSnapshot> rows = await db.MenuPublishSnapshots.Where(s => s.MenuId == menuId).ToListAsync();
            rows.Should().ContainSingle();
            rows[0].PublishedAt.Should().BeAfter(seededAt);
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task SnapshotNotFound_ShortCircuits_NoCompletedEventPublished()
    {
        Ulid menuId = Ulid.NewUlid();
        Ulid ownerId = Ulid.NewUlid();

        _menuData.GetMenuSnapshotAsync(menuId, Arg.Any<CancellationToken>())
            .Returns((MenuSnapshot?)null);

        await using ServiceProvider provider = BuildHarness($"saga-null-{menuId}");
        await SeedLanguages(provider, "fr", "de");
        ITestHarness harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish(Command(menuId, ownerId));

            (await harness.Consumed.Any<StartTranslationCommand>()).Should().BeTrue();

            // No completion event, no snapshot saved.
            harness.Published.Select<MenuTranslationCompletedEvent>().Should().BeEmpty();
            using IServiceScope scope = provider.CreateScope();
            NeaslatorDbContext db = scope.ServiceProvider.GetRequiredService<NeaslatorDbContext>();
            (await db.MenuPublishSnapshots.CountAsync(s => s.MenuId == menuId)).Should().Be(0);

            // Only the "Started" notification fired before the short circuit.
            await _clientProxy.ReceivedWithAnyArgs(1).SendCoreAsync(default!, default!, default);
        }
        finally
        {
            await harness.Stop();
        }
    }
}
