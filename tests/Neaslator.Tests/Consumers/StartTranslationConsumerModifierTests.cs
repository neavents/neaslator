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

public sealed class StartTranslationConsumerModifierTests
{
    private static readonly Ulid MilkId = Ulid.NewUlid();
    private static readonly Ulid OatId = Ulid.NewUlid();
    private static readonly Ulid SoyId = Ulid.NewUlid();

    private readonly IMenuDataProvider _menuData = Substitute.For<IMenuDataProvider>();
    private readonly ITranslationCache _cache = Substitute.For<ITranslationCache>();
    private readonly ITranslationRouter _router = Substitute.For<ITranslationRouter>();

    private ServiceProvider BuildHarness()
    {
        string database = $"modifiers-{Ulid.NewUlid()}";
        IHubContext<TranslationHub> hub = Substitute.For<IHubContext<TranslationHub>>();
        IHubClients clients = Substitute.For<IHubClients>();
        hub.Clients.Returns(clients);
        clients.Group(Arg.Any<string>()).Returns(Substitute.For<IClientProxy>());

        return new ServiceCollection()
            .AddLogging()
            .AddDbContext<NeaslatorDbContext>(o => o.UseInMemoryDatabase(database))
            .AddSingleton(_menuData)
            .AddSingleton(_cache)
            .AddSingleton(_router)
            .AddSingleton(hub)
            .AddScoped<TranslationNotifier>()
            .AddScoped<TranslationPipeline>()
            .AddSingleton<Neaslator.Infrastructure.LanguagePolicy.ITranslationLanguagePolicySource>(Neaslator.Tests.Shared.FixedLanguagePolicy.Unrestricted)
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<StartTranslationConsumer>();
                x.UsingInMemory((context, cfg) => cfg.ConfigureEndpoints(context));
            })
            .BuildServiceProvider(true);
    }

    private void CacheHitsExcept(params string[] misses)
    {
        _cache.LookupAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                string text = ci.ArgAt<string>(1);
                return (IReadOnlyList<CacheLookupResult>)ci.ArgAt<IReadOnlyList<string>>(3)
                    .Select(t => misses.Contains(text)
                        ? new CacheLookupResult(t, null, CacheSource.Miss)
                        : new CacheLookupResult(t, new CachedTranslation($"{text}::{t}", TranslationProviderTier.Primary, 1f, text), CacheSource.L1Garnet))
                    .ToList();
            });
        _router.TranslateAsync(Arg.Any<TranslationBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TranslationBatchResult { IsSuccess = true, Translations = [], TokenUsage = new TokenUsage(0, 0, 0) });
    }

    private static MenuSnapshot Menu(IReadOnlyDictionary<string, string>? oatHumanNames = null) => new()
    {
        Name = "Kahve",
        Sections = [new SectionSnapshot { Id = Ulid.NewUlid(), Name = "Sıcak", Items = [new ItemSnapshot { Id = Ulid.NewUlid(), Name = "Latte" }] }],
        ModifierGroups =
        [
            new ModifierGroupSnapshot
            {
                Id = MilkId,
                Name = "Süt",
                Options =
                [
                    new ModifierOptionSnapshot { Id = OatId, Name = "Yulaf", HumanNames = oatHumanNames },
                    new ModifierOptionSnapshot { Id = SoyId, Name = "Soya" },
                ],
            },
        ],
    };

    private async Task<MenuTranslationCompletedEvent> RunAsync(MenuSnapshot snapshot, params string[] languages)
    {
        Ulid menuId = Ulid.NewUlid();
        _menuData.GetMenuSnapshotAsync(menuId, Arg.Any<Ulid>(), Arg.Any<Ulid?>(), Arg.Any<CancellationToken>()).Returns(snapshot);

        await using ServiceProvider provider = BuildHarness();
        using (IServiceScope scope = provider.CreateScope())
        {
            NeaslatorDbContext db = scope.ServiceProvider.GetRequiredService<NeaslatorDbContext>();
            short order = 0;
            foreach (string code in languages.Prepend("tr"))
                db.SupportedLanguages.Add(new SupportedLanguage { Code = code, EnglishName = code, NativeName = code, IsActive = true, SortOrder = order++ });
            await db.SaveChangesAsync();
        }

        ITestHarness harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish(new StartTranslationCommand
            {
                MenuId = menuId,
                OwnerId = Ulid.NewUlid(),
                SourceLanguageCode = "tr",
                VenueType = "Cafe",
                CuisineType = "Coffee",
                TriggeredAt = DateTimeOffset.UtcNow,
            });

            (await harness.Published.Any<MenuTranslationCompletedEvent>()).Should().BeTrue();
            return harness.Published.Select<MenuTranslationCompletedEvent>().Single().Context.Message;
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task Every_language_carries_each_group_and_option_name_by_id_in_order()
    {
        CacheHitsExcept();

        MenuTranslationCompletedEvent completed = await RunAsync(Menu(), "en", "de");

        foreach (string language in new[] { "en", "de" })
        {
            TranslatedModifierGroupData milk = completed.TranslatedMenus.Single(l => l.LanguageCode == language)
                .ModifierGroups.Should().ContainSingle().Subject;
            milk.GroupId.Should().Be(MilkId);
            milk.TranslatedName.Should().Be($"Süt::{language}");
            milk.Options.Select(o => (o.OptionId, o.TranslatedName)).Should().Equal((OatId, $"Yulaf::{language}"), (SoyId, $"Soya::{language}"));
        }

        completed.TranslatedMenus.SelectMany(l => l.Sections).SelectMany(s => s.Items)
            .Should().OnlyContain(i => i.ItemId != OatId && i.ItemId != SoyId, "modifiers never ride in the dish list");
    }

    [Fact]
    public async Task A_name_a_person_wrote_travels_in_its_language_and_the_translator_fills_the_rest()
    {
        CacheHitsExcept();

        MenuTranslationCompletedEvent completed = await RunAsync(Menu(new Dictionary<string, string> { ["EN"] = "Oat drink" }), "en", "de");

        completed.TranslatedMenus.Single(l => l.LanguageCode == "en").ModifierGroups!.Single().Options[0].TranslatedName.Should().Be("Oat drink");
        completed.TranslatedMenus.Single(l => l.LanguageCode == "de").ModifierGroups!.Single().Options[0].TranslatedName.Should().Be("Yulaf::de");
    }

    [Fact]
    public async Task A_name_with_no_translation_is_null_rather_than_the_source_text()
    {
        CacheHitsExcept("Soya");

        MenuTranslationCompletedEvent completed = await RunAsync(Menu(), "en");

        TranslatedModifierGroupData milk = completed.TranslatedMenus.Single().ModifierGroups!.Single();
        milk.Options.Single(o => o.OptionId == SoyId).TranslatedName.Should().BeNull();
        milk.Options.Single(o => o.OptionId == OatId).TranslatedName.Should().Be("Yulaf::en");
    }

    [Fact]
    public async Task A_menu_without_modifiers_says_so_with_an_empty_list()
    {
        CacheHitsExcept();

        MenuTranslationCompletedEvent completed = await RunAsync(Menu() with { ModifierGroups = [] }, "en");

        completed.TranslatedMenus.Single().ModifierGroups.Should().NotBeNull().And.BeEmpty();
    }
}
