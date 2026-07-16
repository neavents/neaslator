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
    cfg.AddConsumer<StartTranslationConsumer>();

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

        rabbit.ConfigureEndpoints(context);
    });
});

builder.Services.AddNeaslatorHealthChecks(builder.Configuration);

builder.Services.AddHttpClient<IMenuDataProvider, HttpMenuDataProvider>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["MenuService:BaseUrl"] ?? "http://menu-api:8080");
});

builder.Services.AddSignalR();
builder.Services.AddScoped<TranslationNotifier>();

builder.Services.AddHostedService<QualityUpgradeJob>();
builder.Services.AddOpenApi();

WebApplication app = builder.Build();

app.UseMiddleware<Neaslator.Observability.TelemetryEnrichmentMiddleware>();
app.UseSerilogRequestLogging();

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
