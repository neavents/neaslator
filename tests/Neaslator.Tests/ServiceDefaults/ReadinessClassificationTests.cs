using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using FluentAssertions;
using Neaslator.ServiceDefaults;

namespace Neaslator.Tests.ServiceDefaults;

/// <summary>
/// Which dependencies may take this service out of rotation.
/// </summary>
/// <remarks>
/// <para>
/// Readiness decides whether Kubernetes sends traffic to a pod, so it may contain only what a
/// single request cannot be served without. The database qualifies, because it is authoritative.
/// A cache does not, because every read through it falls back to the database. A broker does not,
/// because publishing is transactional — an event is staged in the outbox in the same transaction
/// as the write that caused it, so a broker this service cannot reach fails no request.
/// </para>
/// <para>
/// Tagged for readiness, either one turns a dependency restart into a total outage: every replica
/// answers 503, Kubernetes empties the Service's endpoint list, and a fleet of processes that
/// could all have served becomes unreachable. Verified on a live cluster — pods went 0/1 Running
/// with zero restarts and the endpoint list emptied, while the processes stayed healthy.
/// </para>
/// <para>
/// The broker case stopped being hypothetical with clustering: a three-node quorum cluster is
/// rolling-restarted for ordinary upgrades, so readiness that follows the broker would make every
/// routine maintenance an estate-wide outage.
/// </para>
/// </remarks>
public class ReadinessClassificationTests
{
    private static IReadOnlyList<HealthCheckRegistration> Registrations()
    {
        ServiceCollection services = new();
        services.AddLogging();

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:NeaslatorDb"] = "Host=localhost;Database=d;Username=u;Password=p",
                ["ConnectionStrings:Garnet"] = "localhost:6379",
                ["RabbitMq:Host"] = "localhost",
            })
            .Build();

        services.AddNeaslatorHealthChecks(configuration);

        return services.BuildServiceProvider()
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations.ToList();
    }

    [Fact]
    public void The_database_gates_readiness()
    {
        // The one dependency no request survives, because it is the authoritative store everything
        // else falls back to.
        Registrations().Single(r => r.Name == "postgres").Tags.Should().Contain("ready");
    }

    [Fact]
    public void The_cache_does_not_gate_readiness()
    {
        Registrations().Single(r => r.Name == "garnet").Tags
            .Should().NotContain("ready",
                "a cache restart must not empty the Service's endpoint list while every pod can still serve");
    }

    [Fact]
    public void The_cache_is_still_reported()
    {
        // Off the readiness path, not out of sight: /health still shows it, so a cache problem is
        // visible without being fatal.
        Registrations().Should().ContainSingle(r => r.Name == "garnet");
    }

    [Fact]
    public void The_broker_does_not_gate_readiness()
    {
        Registrations().Single(r => r.Name == "rabbitmq").Tags
            .Should().NotContain("ready",
                "publishing is transactional, so a broker outage fails no request");
    }


    [Fact]
    public void The_database_is_the_only_thing_that_gates_readiness()
    {
        // An allowlist, not a list of known offenders. Written the other way round this test passes
        // for every dependency nobody thought to name — which is how a cache stayed on the
        // readiness path in one service after the sweep that removed it from the others. Adding a
        // name here should require arguing that no request can be served without it.
        string[] ready = [.. Registrations().Where(r => r.Tags.Contains("ready")).Select(r => r.Name)];

        ready.Should().BeEquivalentTo(
            ["postgres"],
            "readiness may contain only what a single request cannot be served without");
    }
}
