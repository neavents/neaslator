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

builder.Services.AddKeyedSingleton<ResiliencePipeline>("provider-pipeline", (sp, key) =>
    new ResiliencePipelineBuilder()
        .AddRetry(new RetryStrategyOptions
        {
            MaxRetryAttempts = 2,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true
        })
        .AddTimeout(new TimeoutStrategyOptions
        {
            Timeout = TimeSpan.FromSeconds(60)
        })
        .Build());

builder.Services.AddScoped<TranslationRouter>(sp =>
{
    ITranslationProvider provider = sp.GetRequiredService<ITranslationProvider>();
    ResiliencePipeline pipeline = sp.GetRequiredKeyedService<ResiliencePipeline>("provider-pipeline");
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
