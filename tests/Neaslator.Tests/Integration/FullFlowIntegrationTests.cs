using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Neavents.Messaging.Contracts.Settings;
using Neavents.Messaging.Contracts.Translation;
using Neaslator.Domain.Entities;
using Neaslator.Domain.Enums;
using Neaslator.Features.TranslateMenu;
using Neaslator.Infrastructure.Cache;
using Neaslator.Infrastructure.Hashing;
using Neaslator.Infrastructure.LanguagePolicy;
using Neaslator.Infrastructure.MenuData;
using Neaslator.Infrastructure.Normalization;
using Neaslator.Infrastructure.Notifications;
using Neaslator.Infrastructure.Providers;
using Neaslator.Persistence;
using NSubstitute;
using Polly;
using StackExchange.Redis;
using Testcontainers.PostgreSql;
using WireMock.Server;
using WireMockRequest = WireMock.RequestBuilders.Request;
using WireMockResponse = WireMock.ResponseBuilders.Response;

namespace Neaslator.Tests.Integration;

/// <summary>
/// True end-to-end: the real service graph (TranslationCache, TranslationPipeline,
/// TranslationRouter, HttpMenuDataProvider, StartTranslationConsumer) wired against a real
/// PostgreSQL container, a real Redis (Garnet-compatible) container, and a WireMock menu
/// service, driven through the MassTransit harness. Only the LLM provider (deterministic,
/// separately unit-tested) and the SignalR hub are faked. Requires Docker.
///
/// Note: the app's RabbitMQ transport + delayed-message-exchange plugin are intentionally
/// out of scope here (they need a plugin-enabled broker image); this exercises the
/// application logic and its real data-store integration, not the transport binary.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FullFlowIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();

#pragma warning disable CS0618 // parameterless ContainerBuilder ctor is deprecated but functional
    private readonly IContainer _redis = new ContainerBuilder()
        .WithImage("redis:7-alpine")
        .WithPortBinding(6379, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Ready to accept connections"))
        .Build();
#pragma warning restore CS0618

    private WireMockServer _menuService = null!;
    private WireMockServer _identity = null!;
    private IConnectionMultiplexer _garnet = null!;
    private ServiceProvider _provider = null!;
    private ITestHarness _harness = null!;
    private readonly EchoProvider _echo = new();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());
        _menuService = WireMockServer.Start();
        _identity = WireMockServer.Start();

        string redisConnString = $"{_redis.Hostname}:{_redis.GetMappedPublicPort(6379)},abortConnect=false";
        _garnet = await ConnectionMultiplexer.ConnectAsync(redisConnString);

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddDbContext<NeaslatorDbContext>(o => o.UseNpgsql(_postgres.GetConnectionString()));
        services.AddSingleton(_garnet);
        services.AddScoped<TranslationCache>();
        services.AddScoped<ITranslationCache>(sp => sp.GetRequiredService<TranslationCache>());
        services.AddSingleton<ITranslationProvider>(_echo);
        services.AddScoped<ITranslationRouter>(sp => new TranslationRouter(
            [new ProviderRegistration { Provider = sp.GetRequiredService<ITranslationProvider>(), Pipeline = ResiliencePipeline<TranslationBatchResult>.Empty }],
            sp.GetRequiredService<ILogger<TranslationRouter>>()));
        services.AddScoped<TranslationPipeline>();
        services.AddHttpClient<IMenuDataProvider, HttpMenuDataProvider>(c => c.BaseAddress = new Uri(_menuService.Url!));
        services.AddHttpClient<ITranslationLanguagePolicySource, IdentityTranslationLanguagePolicySource>(c =>
        {
            c.BaseAddress = new Uri(_identity.Url!);
            c.DefaultRequestHeaders.Add("X-Api-Key", "identity-test-key");
        });

        IHubContext<TranslationHub> hub = Substitute.For<IHubContext<TranslationHub>>();
        IHubClients clients = Substitute.For<IHubClients>();
        hub.Clients.Returns(clients);
        clients.Group(Arg.Any<string>()).Returns(Substitute.For<IClientProxy>());
        services.AddSingleton(hub);
        services.AddScoped<TranslationNotifier>();

        services.AddMassTransitTestHarness(x =>
        {
            x.AddConsumer<StartTranslationConsumer>();
            x.AddConsumer<TranslationLanguageSettingsChangedConsumer>();
            x.UsingInMemory((context, cfg) => cfg.ConfigureEndpoints(context));
        });

        _provider = services.BuildServiceProvider(true);

        using (IServiceScope scope = _provider.CreateScope())
        {
            NeaslatorDbContext db = scope.ServiceProvider.GetRequiredService<NeaslatorDbContext>();
            await db.Database.EnsureCreatedAsync();
            // EnsureCreated applies the HasData language seed; replace it with a small,
            // deterministic set so the translation fan-out is exactly {fr, de}.
            await db.SupportedLanguages.ExecuteDeleteAsync();
            db.SupportedLanguages.AddRange(
                new SupportedLanguage { Code = "en", EnglishName = "English", NativeName = "English", IsActive = true, SortOrder = 0 },
                new SupportedLanguage { Code = "fr", EnglishName = "French", NativeName = "Francais", IsActive = true, SortOrder = 1 },
                new SupportedLanguage { Code = "de", EnglishName = "German", NativeName = "Deutsch", IsActive = true, SortOrder = 2 });
            await db.SaveChangesAsync();
        }

        _harness = _provider.GetRequiredService<ITestHarness>();
        await _harness.Start();
    }

    public async Task DisposeAsync()
    {
        if (_harness is not null)
            await _harness.Stop();
        if (_provider is not null)
            await _provider.DisposeAsync();   // disposes the registered IConnectionMultiplexer
        _menuService?.Stop();
        _identity?.Stop();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _redis.DisposeAsync().AsTask());
    }

    private static long Hash(string text) => TranslationHasher.ComputeHash(TextNormalizer.Normalize(text.AsSpan()));

    /// <summary>
    /// Stands up the menu-service response for one menu, on the route and in the shape the provider
    /// actually reads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two things here are load-bearing and both were previously wrong in this stub, which is how
    /// the end-to-end test kept passing after production changed underneath it.
    /// </para>
    /// <para>
    /// <b>The route is the editor one.</b> The public projection is trimmed for the browser budget
    /// and omits the do-not-translate flags, so reading it made them all default to <c>false</c>
    /// and translated text an author had explicitly excluded. It also only serves published menus,
    /// while translation is usually requested against a draft.
    /// </para>
    /// <para>
    /// <b>The tenant headers are matched, not ignored.</b> Requiring them here means the stub does
    /// not answer a request that lost them — so a regression in tenant scoping fails this test
    /// instead of quietly returning another venue's menu.
    /// </para>
    /// </remarks>
    private void StubMenu(
        Ulid menuId, Ulid ownerId, Ulid sectionId, Ulid itemId, string section, string item, string description)
    {
        string json = JsonSerializer.Serialize(new
        {
            smartMenuDto = new
            {
            id = menuId.ToString(),
            name = "Menu",
            sections = new[]
            {
                new
                {
                    id = sectionId.ToString(),
                    name = section,
                    doNotTranslateName = false,
                    doNotTranslateDescription = false,
                    items = new[]
                    {
                        new
                        {
                            id = itemId.ToString(),
                            name = item,
                            description,
                            doNotTranslateName = false,
                            doNotTranslateDescription = false,
                            subItems = Array.Empty<object>()
                        }
                    }
                }
            }
            }
        });

        _menuService
            .Given(WireMockRequest.Create()
                .WithPath($"/api/v1/editor/smartmenu/{menuId}")
                .WithHeader("X-Venue-Id", ownerId.ToString())
                .WithHeader("X-Tenant-Id", ownerId.ToString())
                .UsingGet())
            .RespondWith(WireMockResponse.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(json));
    }

    private async Task<MenuTranslationCompletedEvent> RunSaga(Ulid menuId, Ulid ownerId)
    {
        await _harness.Bus.Publish(new StartTranslationCommand
        {
            MenuId = menuId,
            OwnerId = ownerId,
            SourceLanguageCode = "en",
            VenueType = "Restaurant",
            CuisineType = "Italian",
            TriggeredAt = DateTimeOffset.UtcNow
        });

        (await _harness.Published.Any<MenuTranslationCompletedEvent>(m => m.Context.Message.MenuId == menuId))
            .Should().BeTrue("the saga should publish a completion event for this menu");

        return _harness.Published.Select<MenuTranslationCompletedEvent>()
            .First(m => m.Context.Message.MenuId == menuId).Context.Message;
    }

    [Fact]
    public async Task FreshMenu_TranslatesThroughRealStores_ThenIdenticalTextHitsGlobalCache()
    {
        // ── Run 1: brand-new menu, everything is a cache miss ───────────────────────────
        Ulid menu1 = Ulid.NewUlid();
        Ulid owner1 = Ulid.NewUlid();
        StubMenu(menu1, owner1, Ulid.NewUlid(), Ulid.NewUlid(), "Starters", "Soup", "Tomato soup");

        MenuTranslationCompletedEvent evt1 = await RunSaga(menu1, owner1);

        evt1.TotalLanguages.Should().Be(2);
        evt1.CompletedLanguages.Should().Be(2);
        evt1.FailedLanguages.Should().Be(0);
        evt1.TranslatedMenus.Should().HaveCount(2);

        TranslatedMenuLanguage fr = evt1.TranslatedMenus.Single(m => m.LanguageCode == "fr");
        fr.Sections.Should().ContainSingle();
        fr.Sections[0].TranslatedName.Should().Be("[fr] Starters");
        fr.Sections[0].Items.Should().ContainSingle();
        fr.Sections[0].Items[0].TranslatedName.Should().Be("[fr] Soup");
        fr.Sections[0].Items[0].TranslatedDescription.Should().Be("[fr] Tomato soup");

        // The menu's own title, which was not translated at all until contracts 1.24.0.
        //
        // MenuSnapshot carried only Sections, so the title was never a translation unit: not
        // diffed, not sent to a provider, and absent from this event. Consumers stored the source
        // text against every target language, so a menu translated into 29 languages showed one
        // title in all of them while its sections were correctly translated — and coverage reported
        // complete, truthfully, because the title had never been counted.
        fr.TranslatedName.Should().Be("[fr] Menu");

        _echo.CallCount.Should().BeGreaterThan(0, "cache was empty, so the provider was invoked");

        // Real PostgreSQL: 4 source units x 2 languages = 8 rows.
        //
        // Four, not three. The menu title joined the section name, the item name and the item
        // description. The stub menu has no description of its own, so that is the only new unit —
        // a menu with one would make it five.
        await using (NeaslatorDbContext db = NewContext())
        {
            (await db.TranslationMemory.CountAsync()).Should().Be(8);
            (await db.MenuPublishSnapshots.CountAsync(s => s.MenuId == menu1)).Should().Be(1);
        }

        // Real Garnet/Redis: the L1 key exists and holds the translated value.
        IDatabase cache = _garnet.GetDatabase();
        string key = $"neaslator:t:{Hash("Soup")}:fr";
        (await cache.KeyExistsAsync(key)).Should().BeTrue();
        CachedTranslation? stored = JsonSerializer.Deserialize<CachedTranslation>((string)(await cache.StringGetAsync(key))!);
        stored!.TranslatedText.Should().Be("[fr] Soup");

        // ── Run 2: a different menu with identical text -> global memory serves it all ──
        int callsAfterRun1 = _echo.CallCount;

        // A different tenant as well as a different menu: the global translation memory is shared
        // across venues by design (the same English text has the same French translation), and this
        // is what proves it, rather than proving one venue can read another's menu.
        Ulid menu2 = Ulid.NewUlid();
        Ulid owner2 = Ulid.NewUlid();
        StubMenu(menu2, owner2, Ulid.NewUlid(), Ulid.NewUlid(), "Starters", "Soup", "Tomato soup");

        MenuTranslationCompletedEvent evt2 = await RunSaga(menu2, owner2);

        evt2.CompletedLanguages.Should().Be(2);
        evt2.TranslatedMenus.Single(m => m.LanguageCode == "fr").Sections[0].Items[0].TranslatedName.Should().Be("[fr] Soup");

        _echo.CallCount.Should().Be(callsAfterRun1,
            "identical text across a different menu must be served entirely from the global translation memory");

        // The menu title is served from the global memory too, not re-translated.
        //
        // It is the same string ("Menu") on a different menu owned by a different tenant, which is
        // the whole point of a global translation memory keyed by text rather than by menu. Asserted
        // because the title reaches the provider through a different code path from the section and
        // item text, and a title that missed the cache would still have produced the right output
        // here — just at the cost of an LLM call per menu, forever.
        evt2.TranslatedMenus.Single(m => m.LanguageCode == "fr").TranslatedName.Should().Be("[fr] Menu");

        // No new memory rows were written; only a second snapshot row was added. Eight, not six —
        // the menu title became a fourth unit in run 1, and this run adds none.
        await using (NeaslatorDbContext db = NewContext())
        {
            (await db.TranslationMemory.CountAsync()).Should().Be(8);
            (await db.MenuPublishSnapshots.CountAsync()).Should().Be(2);
        }
    }

    private void StubMenuWithModifiers(Ulid menuId, Ulid ownerId, Ulid tenantId, object[] modifierGroups)
    {
        string json = JsonSerializer.Serialize(new
        {
            smartMenuDto = new
            {
                smartMenuId = menuId.ToString(),
                name = "Kahve",
                sections = Array.Empty<object>(),
                modifierGroups,
            },
            detailsDto = new { },
        });

        _menuService
            .Given(WireMockRequest.Create()
                .WithPath($"/api/v1/editor/smartmenu/{menuId}")
                .WithHeader("X-Venue-Id", ownerId.ToString())
                .WithHeader("X-Tenant-Id", tenantId.ToString())
                .UsingGet())
            .RespondWith(WireMockResponse.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(json));
    }

    private static object ModifierGroup(Ulid id, string name, object[] options, string[]? human = null, Dictionary<string, string>? names = null)
        => new { id = id.ToString(), name, min = 0, max = 1, required = false, displayOrder = 0, nameTranslations = names, humanEditedLanguages = human, options };

    private static object ModifierOption(Ulid id, string name, string[]? human = null, Dictionary<string, string>? names = null)
        => new { id = id.ToString(), name, priceDelta = new { amount = 10m, currency = new { code = "TRY" } }, availability = "Active", displayOrder = 0, nameTranslations = names, humanEditedLanguages = human };

    private async Task<MenuTranslationCompletedEvent> RunSagaFor(
        Ulid menuId, Ulid ownerId, Ulid tenantId, Func<MenuTranslationCompletedEvent, bool> isThisRun)
    {
        StartTranslationCommand command = new()
        {
            MenuId = menuId,
            OwnerId = ownerId,
            TenantId = tenantId,
            SourceLanguageCode = "en",
            VenueType = "Cafe",
            CuisineType = "Coffee",
            TriggeredAt = DateTimeOffset.UtcNow,
        };
        await _harness.Bus.Publish(command);

        bool Matches(MenuTranslationCompletedEvent m) => m.MenuId == menuId && isThisRun(m);
        DateTime deadline = DateTime.UtcNow.AddSeconds(60);
        while (!_harness.Published.Select<MenuTranslationCompletedEvent>().Any(m => Matches(m.Context.Message)))
        {
            string faults = string.Join("; ", _harness.Published.Select<Fault<StartTranslationCommand>>()
                .SelectMany(f => f.Context.Message.Exceptions).Select(e => e.Message));
            faults.Should().BeEmpty();
            DateTime.UtcNow.Should().BeBefore(deadline, "the saga should publish a completion event for this run");
            await Task.Delay(100);
        }

        return _harness.Published.Select<MenuTranslationCompletedEvent>().Select(m => m.Context.Message).Last(Matches);
    }

    private static string? OptionName(MenuTranslationCompletedEvent completed, string language, Ulid optionId)
        => completed.TranslatedMenus.SingleOrDefault(l => l.LanguageCode == language)?
            .ModifierGroups?.SelectMany(g => g.Options).SingleOrDefault(o => o.OptionId == optionId)?.TranslatedName;

    [Fact]
    public async Task A_menu_with_modifiers_is_translated_end_to_end_and_a_renamed_option_is_the_only_new_work()
    {
        Ulid menuId = Ulid.NewUlid();
        Ulid ownerId = Ulid.NewUlid();
        Ulid tenantId = Ulid.NewUlid();
        Ulid milk = Ulid.NewUlid();
        Ulid oat = Ulid.NewUlid();
        Ulid soy = Ulid.NewUlid();
        string tail = Ulid.NewUlid().ToString()[^6..];
        string milkName = $"Milk {tail}";
        string oatName = $"Oat {tail}";
        string soyName = $"Soy {tail}";

        StubMenuWithModifiers(menuId, ownerId, tenantId,
        [
            ModifierGroup(milk, milkName,
                [ModifierOption(oat, oatName, human: ["de"], names: new() { ["de"] = "Hafer von Hand" }), ModifierOption(soy, soyName)]),
        ]);

        int callsBefore = _echo.CallCount;
        MenuTranslationCompletedEvent first = await RunSagaFor(menuId, ownerId, tenantId, m => OptionName(m, "fr", soy) is not null);

        first.TenantId.Should().Be(tenantId);
        foreach (string language in new[] { "fr", "de" })
        {
            TranslatedModifierGroupData group = first.TranslatedMenus.Single(l => l.LanguageCode == language).ModifierGroups!.Single();
            group.GroupId.Should().Be(milk);
            group.TranslatedName.Should().Be($"[{language}] {milkName}");
            group.Options.Single(o => o.OptionId == soy).TranslatedName.Should().Be($"[{language}] {soyName}");
        }

        first.TranslatedMenus.Single(l => l.LanguageCode == "fr").ModifierGroups!.Single().Options.Single(o => o.OptionId == oat)
            .TranslatedName.Should().Be($"[fr] {oatName}");
        first.TranslatedMenus.Single(l => l.LanguageCode == "de").ModifierGroups!.Single().Options.Single(o => o.OptionId == oat)
            .TranslatedName.Should().Be("Hafer von Hand", "a name a person wrote travels instead of the translator's");
        _echo.CallCount.Should().BeGreaterThan(callsBefore);

        string renamed = $"Oat milk {tail}";
        _menuService.Reset();
        StubMenuWithModifiers(menuId, ownerId, tenantId,
            [ModifierGroup(milk, milkName, [ModifierOption(oat, renamed), ModifierOption(soy, soyName)])]);
        int callsAfterFirst = _echo.CallCount;

        MenuTranslationCompletedEvent second = await RunSagaFor(menuId, ownerId, tenantId, m => OptionName(m, "de", oat) == $"[de] {renamed}");

        second.TranslatedMenus.Single(l => l.LanguageCode == "de").ModifierGroups!.Single().Options.Single(o => o.OptionId == oat)
            .TranslatedName.Should().Be($"[de] {renamed}");
        second.TranslatedMenus.Single(l => l.LanguageCode == "fr").ModifierGroups!.Single().TranslatedName.Should().Be($"[fr] {milkName}");
        _echo.CallCount.Should().Be(callsAfterFirst + 2, "only the renamed option is new, once per language");

        await using NeaslatorDbContext db = NewContext();
        (await db.TranslationMemory.CountAsync(m => m.NormalizedSourceText.Contains(tail))).Should().Be(4 * 2,
            "three names in the first run and the renamed option in the second, in two languages each");
        MenuPublishSnapshot snapshot = await db.MenuPublishSnapshots.SingleAsync(s => s.MenuId == menuId);
        snapshot.TenantId.Should().Be(tenantId);
        snapshot.SnapshotJson.Should().Contain(renamed).And.NotContain(oatName + "\"");
    }

    private void StubTranslationLanguages(Ulid ownerId, Ulid orgId, string[]? languages)
    {
        _identity
            .Given(WireMockRequest.Create()
                .WithPath($"/internal/v1/owners/{ownerId}/menu-defaults")
                .WithHeader("X-Api-Key", "identity-test-key")
                .UsingGet())
            .RespondWith(WireMockResponse.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(new
                {
                    orgId = orgId.ToString(),
                    currency = "TRY",
                    language = "en",
                    timezone = "Europe/Istanbul",
                    translationLanguages = languages,
                })));
    }

    private int IdentityCallsFor(Ulid ownerId) =>
        _identity.LogEntries.Count(e => e.RequestMessage?.Path == $"/internal/v1/owners/{ownerId}/menu-defaults");

    private async Task<MenuTranslationCompletedEvent> TranslateAsync(Ulid menuId, Ulid ownerId, Ulid tenantId, bool everything = true)
    {
        int before = _harness.Published.Select<MenuTranslationCompletedEvent>().Count(m => m.Context.Message.MenuId == menuId);
        await _harness.Bus.Publish(new StartTranslationCommand
        {
            MenuId = menuId,
            OwnerId = ownerId,
            TenantId = tenantId,
            SourceLanguageCode = "en",
            VenueType = "Cafe",
            CuisineType = "Coffee",
            TriggeredAt = DateTimeOffset.UtcNow,
            IgnorePreviousSnapshot = everything,
        });

        DateTime deadline = DateTime.UtcNow.AddSeconds(60);
        while (_harness.Published.Select<MenuTranslationCompletedEvent>().Count(m => m.Context.Message.MenuId == menuId) <= before)
        {
            string faults = string.Join("; ", _harness.Published.Select<Fault<StartTranslationCommand>>()
                .SelectMany(f => f.Context.Message.Exceptions).Select(e => e.Message));
            faults.Should().BeEmpty();
            DateTime.UtcNow.Should().BeBefore(deadline, "the saga should publish a completion event for this run");
            await Task.Delay(100);
        }

        return _harness.Published.Select<MenuTranslationCompletedEvent>().Select(m => m.Context.Message).Last(m => m.MenuId == menuId);
    }

    private (Ulid Menu, Ulid Owner, Ulid Org, string Tail) PolicyMenu(string[]? languages)
    {
        Ulid menuId = Ulid.NewUlid();
        Ulid ownerId = Ulid.NewUlid();
        Ulid orgId = Ulid.NewUlid();
        string tail = Ulid.NewUlid().ToString()[^6..];
        StubMenuWithModifiers(menuId, ownerId, orgId, [ModifierGroup(Ulid.NewUlid(), $"Milk {tail}", [ModifierOption(Ulid.NewUlid(), $"Oat {tail}")])]);
        StubTranslationLanguages(ownerId, orgId, languages);
        return (menuId, ownerId, orgId, tail);
    }

    [Fact]
    public async Task An_organisation_restricted_to_its_source_language_never_calls_the_provider()
    {
        (Ulid menuId, Ulid ownerId, Ulid orgId, string tail) = PolicyMenu(["en"]);
        int calls = _echo.CallCount;

        MenuTranslationCompletedEvent completed = await TranslateAsync(menuId, ownerId, orgId);

        _echo.CallCount.Should().Be(calls, "no target language is left once the source is excluded");
        completed.TotalLanguages.Should().Be(0);
        completed.TranslatedMenus.Should().BeEmpty();
        await using NeaslatorDbContext db = NewContext();
        (await db.TranslationMemory.CountAsync(m => m.NormalizedSourceText.Contains(tail))).Should().Be(0);
    }

    [Fact]
    public async Task An_empty_list_translates_nothing()
    {
        (Ulid menuId, Ulid ownerId, Ulid orgId, _) = PolicyMenu([]);
        int calls = _echo.CallCount;

        (await TranslateAsync(menuId, ownerId, orgId)).TotalLanguages.Should().Be(0);

        _echo.CallCount.Should().Be(calls);
    }

    [Fact]
    public async Task A_restricted_organisation_gets_exactly_its_languages_and_the_answer_is_cached_until_identity_says_it_changed()
    {
        (Ulid menuId, Ulid ownerId, Ulid orgId, string tail) = PolicyMenu(["de", "it"]);
        int calls = _echo.CallCount;

        MenuTranslationCompletedEvent first = await TranslateAsync(menuId, ownerId, orgId);

        first.TranslatedMenus.Select(l => l.LanguageCode).Should().Equal("de");
        first.TotalLanguages.Should().Be(1, "it is not an active language here, and fr was not chosen");
        _echo.CallCount.Should().Be(calls + 1, "one batch, for German only");
        IdentityCallsFor(ownerId).Should().Be(1);

        await TranslateAsync(menuId, ownerId, orgId);
        IdentityCallsFor(ownerId).Should().Be(1, "the second run reads the cached answer");

        StubTranslationLanguages(ownerId, orgId, null);
        await _harness.Bus.Publish(new SettingsChanged
        {
            OrgId = orgId,
            OwningService = "identity",
            Keys = [TranslationLanguageSettingsChangedConsumer.SettingKey],
            Level = "org",
            ScopeId = orgId,
            Change = SettingsChanged.ValueChanged,
            Timestamp = DateTimeOffset.UtcNow,
        });
        DateTime settled = DateTime.UtcNow.AddSeconds(30);
        while (!_harness.Consumed.Select<SettingsChanged>().Any(c => c.Context.Message.OrgId == orgId))
        {
            DateTime.UtcNow.Should().BeBefore(settled, "the settings change should be consumed");
            await Task.Delay(100);
        }

        MenuTranslationCompletedEvent widened = await TranslateAsync(menuId, ownerId, orgId);
        IdentityCallsFor(ownerId).Should().Be(2);
        widened.TranslatedMenus.Select(l => l.LanguageCode).Should().BeEquivalentTo(["fr", "de"]);
        await using NeaslatorDbContext db = NewContext();
        (await db.TranslationMemory.CountAsync(m => m.NormalizedSourceText.Contains(tail))).Should().Be(2 * 2);
    }

    [Fact]
    public async Task An_identity_that_cannot_answer_fails_the_run_instead_of_translating_everything()
    {
        Ulid menuId = Ulid.NewUlid();
        Ulid ownerId = Ulid.NewUlid();
        Ulid orgId = Ulid.NewUlid();
        StubMenuWithModifiers(menuId, ownerId, orgId, []);
        _identity
            .Given(WireMockRequest.Create().WithPath($"/internal/v1/owners/{ownerId}/menu-defaults").UsingGet())
            .RespondWith(WireMockResponse.Create().WithStatusCode(503));
        int calls = _echo.CallCount;

        await _harness.Bus.Publish(new StartTranslationCommand
        {
            MenuId = menuId, OwnerId = ownerId, TenantId = orgId, SourceLanguageCode = "en",
            VenueType = "Cafe", CuisineType = "Coffee", TriggeredAt = DateTimeOffset.UtcNow, IgnorePreviousSnapshot = true,
        });

        DateTime faulted = DateTime.UtcNow.AddSeconds(30);
        while (!_harness.Published.Select<Fault<StartTranslationCommand>>().Any(f => f.Context.Message.Message.MenuId == menuId))
        {
            DateTime.UtcNow.Should().BeBefore(faulted, "a run identity cannot answer for should fault");
            await Task.Delay(100);
        }
        _echo.CallCount.Should().Be(calls);
        _harness.Published.Select<MenuTranslationCompletedEvent>().Should().NotContain(m => m.Context.Message.MenuId == menuId);
    }

    private NeaslatorDbContext NewContext()
    {
        DbContextOptions<NeaslatorDbContext> options = new DbContextOptionsBuilder<NeaslatorDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;
        return new NeaslatorDbContext(options);
    }

    private sealed class EchoProvider : ITranslationProvider
    {
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);

        public string ProviderName => "echo";
        public TranslationProviderTier Tier => TranslationProviderTier.Primary;
        public bool SupportsPrefixCaching => false;
        public int MaxBatchSize => 20;
        public int MaxConcurrentRequests => 10;

        public Task<TranslationBatchResult> TranslateBatchAsync(TranslationBatchRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(new TranslationBatchResult
            {
                IsSuccess = true,
                Translations = request.Items
                    .Select(i => new TranslatedUnit { SourceHash = i.SourceHash, TranslatedName = $"[{request.TargetLanguageCode}] {i.Name}" })
                    .ToList(),
                TokenUsage = new TokenUsage(1, 1, 0)
            });
        }

        public Task<bool> IsHealthyAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
