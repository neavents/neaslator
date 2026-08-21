using Microsoft.Extensions.Diagnostics.HealthChecks;
using RabbitMQ.Client;

namespace Neaslator.ServiceDefaults;

public static class HealthCheckExtensions
{
    private static IConnection? _cachedConnection;
    private static readonly object _lock = new();

    public static IServiceCollection AddNeaslatorHealthChecks(this IServiceCollection services, IConfiguration configuration)
    {
        string postgresConnectionString = configuration.GetConnectionString("NeaslatorDb")
            ?? throw new InvalidOperationException("Connection string 'NeaslatorDb' is not configured.");

        string garnetConnectionString = configuration.GetConnectionString("Garnet")
            ?? throw new InvalidOperationException("Connection string 'Garnet' is not configured.");

        string rabbitMqHost = configuration["RabbitMq:Host"] ?? "localhost";
        string rabbitMqUsername = configuration["RabbitMq:Username"] ?? "guest";
        string rabbitMqPassword = configuration["RabbitMq:Password"] ?? "guest";

        services.AddHealthChecks()
            .AddNpgSql(postgresConnectionString, name: "postgres", tags: ["db", "ready"])
            .AddRedis(garnetConnectionString, name: "garnet", tags: ["cache", "ready"])
            // The three checks here cover what this service uses to move work around and not the one
            // thing it uses to do the work. Every provider already implemented IsHealthyAsync and
            // nothing called it, so an exhausted API key left health green while every translation
            // failed — visible only as a menu that stayed in one language.
            //
            // Not tagged "ready": cached translations still serve, which is most of the traffic.
            .AddCheck<TranslationProviderHealthCheck>(
                name: "translation-providers",
                failureStatus: HealthStatus.Unhealthy,
                tags: ["providers"])
            .AddRabbitMQ(async _ =>
            {
                if (_cachedConnection is { IsOpen: true })
                    return _cachedConnection;

                lock (_lock)
                {
                    if (_cachedConnection is { IsOpen: true })
                        return _cachedConnection;

                    _cachedConnection?.Dispose();

                    ConnectionFactory factory = new()
                    {
                        HostName = rabbitMqHost,
                        UserName = rabbitMqUsername,
                        Password = rabbitMqPassword
                    };

                    _cachedConnection = factory.CreateConnectionAsync().GetAwaiter().GetResult();
                    return _cachedConnection;
                }
            }, name: "rabbitmq", tags: ["messaging", "ready"]);

        return services;
    }
}
