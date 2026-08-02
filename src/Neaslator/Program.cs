using MassTransit;
using Microsoft.EntityFrameworkCore;
using Neaslator.Features.TranslateMenu;
using Neaslator.Infrastructure.Cache;
using Neaslator.Infrastructure.MenuData;
using Neaslator.Infrastructure.Providers;
using Neaslator.Persistence;
using Neaslator.ServiceDefaults;
using Polly;
using Polly.Retry;
using Polly.Timeout;
using Serilog;
using StackExchange.Redis;
using Neaslator.Infrastructure.Notifications;
using Neaslator.Features.QualityUpgrade;
using Neaslator.Features.OnDemandTranslation;
using Neaslator.Features.TranslationStatus;
using Neaslator.Features.RetryFailedTranslations;
using Neaslator.Features.ProviderHealth;
using Neaslator.Features.TranslationMemoryStats;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls("http://0.0.0.0:5300");

builder.AddNeaslatorLogging();

builder.Services.AddDbContext<NeaslatorDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("NeaslatorDb")));

builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
    ConnectionMultiplexer.Connect(builder.Configuration.GetConnectionString("Garnet") ?? "localhost:6379"));

builder.Services.AddScoped<TranslationCache>();
builder.Services.AddScoped<ITranslationCache>(sp => sp.GetRequiredService<TranslationCache>());
builder.Services.AddSingleton<DistributedTranslationLock>();

builder.Services.Configure<DeepSeekOptions>(builder.Configuration.GetSection("Neaslator:Providers:DeepSeek"));
builder.Services.AddHttpClient<ITranslationProvider, DeepSeekProvider>(client =>
{
    client.BaseAddress = new Uri("https://api.deepseek.com/v1");
    client.DefaultRequestHeaders.Add("Authorization",
        $"Bearer {Environment.GetEnvironmentVariable("NEASLATOR_DEEPSEEK_API_KEY")}");
});

// Typed on TranslationBatchResult, which is the whole reason the retry now does anything.
//
// A bare ResiliencePipeline retries thrown exceptions. The provider does not throw for the failure
// that actually occurs in production: a model response in an envelope we did not expect comes back
// as IsSuccess = false. So this looked like two retries with exponential backoff and was, for the
// dominant failure mode, no retry at all — one bad response permanently failed that language.
//
// ShouldHandle below covers exactly the outcomes a second attempt can fix. A malformed response is
// nondeterministic — the same call succeeded ~92% of the time when measured against the live API —
// so retrying converts most of those failures into translations.
builder.Services.AddKeyedSingleton<ResiliencePipeline<TranslationBatchResult>>("provider-pipeline", (sp, key) =>
    new ResiliencePipelineBuilder<TranslationBatchResult>()
        .AddRetry(new RetryStrategyOptions<TranslationBatchResult>
        {
            MaxRetryAttempts = 2,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            ShouldHandle = new PredicateBuilder<TranslationBatchResult>()
                .Handle<Exception>(ex => ex is not OperationCanceledException)
                .HandleResult(static result => !result.IsSuccess && result.IsRetryable)
        })
        .AddTimeout(new TimeoutStrategyOptions
        {
            Timeout = TimeSpan.FromSeconds(60)
        })
        .Build());

builder.Services.AddScoped<TranslationRouter>(sp =>
{
    ITranslationProvider provider = sp.GetRequiredService<ITranslationProvider>();
    ResiliencePipeline<TranslationBatchResult> pipeline =
        sp.GetRequiredKeyedService<ResiliencePipeline<TranslationBatchResult>>("provider-pipeline");
    ProviderRegistration[] registrations = [new() { Provider = provider, Pipeline = pipeline }];
    return new TranslationRouter(registrations, sp.GetRequiredService<ILogger<TranslationRouter>>());
});
builder.Services.AddScoped<ITranslationRouter>(sp => sp.GetRequiredService<TranslationRouter>());

builder.Services.AddScoped<TranslationPipeline>();

builder.Services.AddNeaslatorTelemetry(builder.Configuration);

builder.Services.AddMassTransit(cfg =>
{
    cfg.AddConsumer<MenuPublishedConsumer>();
    cfg.AddConsumer<MenuTranslationRequestedConsumer>();
    cfg.AddConsumer<StartTranslationConsumer>();

    // Transactional outbox and inbox. This service had NEITHER.
    //
    // Outbound: an integration event is now written to the outbox in the same transaction as the row
    // it describes. Before this, neaslator saved and then called the broker as a separate, unretried
    // step — anything interrupting that gap lost the event permanently with no record it existed.
    //
    // Inbound: the inbox deduplicates redelivery. RabbitMQ redelivers on connection loss, on a
    // consumer that faults after its work has landed, and on each configured retry. Without it,
    // StartTranslationConsumer re-ran a whole translation — real money at a translation provider —
    // every time a message came back.
    cfg.AddEntityFrameworkOutbox<NeaslatorDbContext>(o =>
    {
        o.UsePostgres();
        o.UseBusOutbox();
    });

    // The INBOX half, which AddEntityFrameworkOutbox does NOT provide on its own — it registers the
    // outbox services and enables UseBusOutbox for the send side only. Putting an inbox on a receive
    // endpoint takes UseEntityFrameworkOutbox on the endpoint. That exact misunderstanding left
    // identity, subscription and messaging with zero inbox filters on every endpoint for a long time
    // (task #10), which is why this is a separate, explicit call and why the test asserts it against
    // the running bus rather than against this source.
    cfg.AddConfigureEndpointsCallback((registrationContext, _, endpointConfigurator) =>
        endpointConfigurator.UseEntityFrameworkOutbox<NeaslatorDbContext>(registrationContext));

    cfg.UsingRabbitMq((context, rabbit) =>
    {
        string host = builder.Configuration["RabbitMq:Host"] ?? "localhost";
        string username = builder.Configuration["RabbitMq:Username"] ?? "guest";
        string password = builder.Configuration["RabbitMq:Password"] ?? "guest";

        rabbit.Host(host, h =>
        {
            h.Username(username);
            h.Password(password);
        });

        rabbit.UseDelayedMessageScheduler();

        // No retry policy was configured, so the first transient failure dead-lettered the
        // message permanently. StartTranslationConsumer reads the menu from menu-service over
        // HTTP; restarting menu-api during a publish produced a single "Connection refused" and
        // the translation was lost — it never came back when menu-api returned seconds later.
        //
        // Immediate retries cover a blip. Delayed redelivery releases the message back to the
        // broker between attempts, so a dependency that is genuinely restarting has minutes to
        // come back without this consumer holding a thread. Only after both are exhausted does
        // the message reach _error, which is what the publish-spine smoke asserts on.
        rabbit.UseMessageRetry(r => r.Interval(3, TimeSpan.FromSeconds(5)));
        rabbit.UseDelayedRedelivery(r => r.Intervals(
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(2),
            TimeSpan.FromMinutes(10)));

        rabbit.ConfigureEndpoints(context);
    });
});

builder.Services.AddNeaslatorHealthChecks(builder.Configuration);

builder.Services.AddHttpClient<IMenuDataProvider, HttpMenuDataProvider>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["MenuService:BaseUrl"] ?? "http://menu-api:8080");

    // Service credentials for menu-service's TrustedHeader scheme. neaslator reads the EDITOR
    // endpoint (it needs the do-not-translate flags the public projection drops), which is
    // authenticated — so the shared secret is required, not optional. It reaches menu-api
    // directly rather than through the gateway, so nothing else would vouch for it.
    string serviceUserId = builder.Configuration["MenuService:ServiceUserId"] ?? "neaslator-service";
    client.DefaultRequestHeaders.Add("X-User-Id", serviceUserId);

    string? internalKey = builder.Configuration["MenuService:InternalApiKey"];
    if (!string.IsNullOrWhiteSpace(internalKey))
        client.DefaultRequestHeaders.Add("X-Internal-Key", internalKey);
});

builder.Services.AddSignalR();
builder.Services.AddScoped<TranslationNotifier>();

builder.Services.AddHostedService<QualityUpgradeJob>();
builder.Services.AddOpenApi();

WebApplication app = builder.Build();

// Apply pending migrations on boot.
//
// This service had NO migration step. Three migrations existed in source and exactly ONE —
// InitialCreate, from 14 June — had ever reached the database. The schema froze there, so
// AddMassTransitInboxOutbox never ran, so OutboxState did not exist, so every publish staged
// through UseBusOutbox() failed at SaveChangesAsync. Neaslator could not emit a single integration
// event: that is why translations never reached KV, and why menu.smart_menu_translations held one
// row while qrmenu-edge sat waiting on MenuTranslationCompleted.
//
// Advisory-locked because every service here migrates against the same Postgres on boot with no
// coordination, and replicas start together. See MigrationLockExtensions for the four incidents
// that pattern exists to prevent.
using (IServiceScope scope = app.Services.CreateScope())
{
    NeaslatorDbContext db = scope.ServiceProvider.GetRequiredService<NeaslatorDbContext>();
    await db.MigrateWithAdvisoryLockAsync("neaslator");
}


app.UseMiddleware<Neaslator.Observability.TelemetryEnrichmentMiddleware>();
app.UseSerilogRequestLogging();

// Everything below except /, /health and the hub requires the shared gateway secret. This service
// enforced nothing before, and its container publishes a port. See InternalKeyMiddleware.
app.UseMiddleware<Neaslator.InternalKeyMiddleware>();

app.MapOpenApi();
app.MapHub<TranslationHub>("/hubs/translation");

RouteGroupBuilder api = app.MapGroup("/");
ListLanguagesEndpoint.Map(api);
TranslationStatusEndpoint.Map(api);
OnDemandTranslationEndpoint.Map(api);
RetryEndpoint.Map(api);
ProviderHealthEndpoint.Map(api);
MemoryStatsEndpoint.Map(api);
app.MapHealthChecks("/health");
app.MapGet("/", () => Results.Ok(new { service = "neaslator", status = "running" }));

app.Run();
