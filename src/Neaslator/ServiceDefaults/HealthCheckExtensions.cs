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
            // NOT tagged "ready", deliberately.
            //
            // Readiness decides whether this pod receives traffic at all, so it may contain only
            // what a single request cannot be served without. The database qualifies. A cache does
            // not: every read through it falls back to PostgreSQL, which is authoritative.
            //
            // Tagged "ready" it turned a cache restart into a total outage. Every replica answers
            // 503, Kubernetes empties the Service's endpoint list, and nobody can reach a fleet of
            // healthy processes that were all able to serve. Verified on a live cluster with the
            // broker check, which behaves identically: pods went 0/1 Running with zero restarts and
            // the endpoint list emptied.
            //
            // The trade is real and worth naming: without the cache, reads land on PostgreSQL and
            // it carries load it normally does not. That is a risk of an outage. Keeping the tag
            // was a guarantee of one.
            .AddRedis(garnetConnectionString, name: "garnet", tags: ["cache"])
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
            // NOT tagged "ready", deliberately — the cache reasoning above, applied to the broker.
            //
            // Every integration event this service publishes is staged in the outbox, inside the
            // same transaction as the write that caused it, and delivered later by a sweep. A
            // broker this service cannot reach therefore fails no request: the write commits and
            // the event waits. That is the entire point of the outbox, and it is why the outbox
            // backlog check below exists — to see the condition that this check cannot.
            //
            // Tagged "ready", a broker restart took every replica of every service out of rotation
            // at once: the estate's APIs going dark together for a dependency none of them needs in
            // order to answer a request. Verified on a live cluster — scaling RabbitMQ to zero left
            // the pods 0/1 Running with zero restarts and emptied the Service's endpoint list.
            //
            // This matters more than it did. A three-node quorum cluster is rolling-restarted for
            // ordinary upgrades, and readiness that follows the broker would turn routine
            // maintenance into an estate-wide outage every time.
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
            }, name: "rabbitmq", tags: ["messaging"]);

        return services;
    }
}
