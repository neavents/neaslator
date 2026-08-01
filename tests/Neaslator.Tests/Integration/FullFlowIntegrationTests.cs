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
using Neavents.Messaging.Contracts.Translation;
using Neaslator.Domain.Entities;
using Neaslator.Domain.Enums;
using Neaslator.Features.TranslateMenu;
using Neaslator.Infrastructure.Cache;
using Neaslator.Infrastructure.Hashing;
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
    private IConnectionMultiplexer _garnet = null!;
    private ServiceProvider _provider = null!;
    private ITestHarness _harness = null!;
    private readonly EchoProvider _echo = new();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());
        _menuService = WireMockServer.Start();

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
            [new ProviderRegistration { Provider = sp.GetRequiredService<ITranslationProvider>(), Pipeline = ResiliencePipeline.Empty }],
            sp.GetRequiredService<ILogger<TranslationRouter>>()));
        services.AddScoped<TranslationPipeline>();
        services.AddHttpClient<IMenuDataProvider, HttpMenuDataProvider>(c => c.BaseAddress = new Uri(_menuService.Url!));

        IHubContext<TranslationHub> hub = Substitute.For<IHubContext<TranslationHub>>();
        IHubClients clients = Substitute.For<IHubClients>();
        hub.Clients.Returns(clients);
        clients.Group(Arg.Any<string>()).Returns(Substitute.For<IClientProxy>());
        services.AddSingleton(hub);
        services.AddScoped<TranslationNotifier>();

        services.AddMassTransitTestHarness(x =>
        {
            x.AddConsumer<StartTranslationConsumer>();
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
