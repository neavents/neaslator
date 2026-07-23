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

    private void StubMenu(Ulid menuId, Ulid sectionId, Ulid itemId, string section, string item, string description)
    {
        string json = JsonSerializer.Serialize(new
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
        });

        _menuService
            .Given(WireMockRequest.Create().WithPath($"/api/v1/smartmenu/{menuId}").UsingGet())
            .RespondWith(WireMockResponse.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(json));
    }

    private async Task<MenuTranslationCompletedEvent> RunSaga(Ulid menuId)
    {
        await _harness.Bus.Publish(new StartTranslationCommand
        {
            MenuId = menuId,
            OwnerId = Ulid.NewUlid(),
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
        StubMenu(menu1, Ulid.NewUlid(), Ulid.NewUlid(), "Starters", "Soup", "Tomato soup");

        MenuTranslationCompletedEvent evt1 = await RunSaga(menu1);

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

        _echo.CallCount.Should().BeGreaterThan(0, "cache was empty, so the provider was invoked");

        // Real PostgreSQL: 3 source units (section, name, description) x 2 languages = 6 rows.
        await using (NeaslatorDbContext db = NewContext())
        {
            (await db.TranslationMemory.CountAsync()).Should().Be(6);
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

        Ulid menu2 = Ulid.NewUlid();
        StubMenu(menu2, Ulid.NewUlid(), Ulid.NewUlid(), "Starters", "Soup", "Tomato soup");

        MenuTranslationCompletedEvent evt2 = await RunSaga(menu2);

        evt2.CompletedLanguages.Should().Be(2);
        evt2.TranslatedMenus.Single(m => m.LanguageCode == "fr").Sections[0].Items[0].TranslatedName.Should().Be("[fr] Soup");

        _echo.CallCount.Should().Be(callsAfterRun1,
            "identical text across a different menu must be served entirely from the global translation memory");

        // No new memory rows were written; only a second snapshot row was added.
        await using (NeaslatorDbContext db = NewContext())
        {
            (await db.TranslationMemory.CountAsync()).Should().Be(6);
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
