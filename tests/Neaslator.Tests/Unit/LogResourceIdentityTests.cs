using FluentAssertions;
using Neaslator.ServiceDefaults;
using OpenTelemetry;
using OpenTelemetry.Resources;
using Xunit;

namespace Neaslator.Tests.Unit;

/// <summary>
/// The log pipeline and the trace pipeline must agree on what this service is called.
/// </summary>
/// <remarks>
/// <para>
/// <b>What breaks in production.</b> Telemetry leaves this process through two pipelines that share
/// nothing. The OpenTelemetry SDK builds a resource for traces and metrics. Serilog's OTLP sink
/// builds a separate one for logs. Each needs a service name, and the log pipeline was never given
/// one — so every exported log record carried the SDK's fallback, <c>unknown_service:dotnet</c>.
/// </para>
/// <para>
/// <b>Why nothing notices.</b> Nothing is lost and nothing errors. The logs arrive, are stored, and
/// are queryable — under a name that matches no service, no dashboard and no trace. Filter SigNoz by
/// <c>Neaslator</c> and you get spans and no logs, which reads as "logging is not wired
/// up yet" rather than "the logs are here under a different name". It also silently defeats the
/// point of correlation: a trace id on a log record is worthless if the record is filed under a
/// service nobody looks at, so no amount of correlation-id plumbing would have connected them.
/// </para>
/// <para>
/// <b>Found live on 2026-07-31.</b> The proof came from the subscription service, whose logs were
/// visible in ClickHouse as 1,904 lines in two hours under <c>unknown_service:dotnet</c>. This
/// service had the identical omission — the same sink configured with an endpoint and no resource —
/// and had simply not exported anything yet to demonstrate it. Fixed here on the same evidence
/// rather than waiting for its own outage; the code was the same code.
/// </para>
/// <para>
/// <b>Why a shared constant rather than a comparison.</b> A near-miss between the two names is not
/// better than a missing one — it is two services in the UI, each holding half the story. Both
/// pipelines now read <see cref="ServiceIdentity.Name"/>, so they cannot disagree. These tests pin
/// the two remaining ways that can still be undone: someone reintroducing a literal in one pipeline,
/// and someone changing the constant's value out from under the dashboards that query it.
/// </para>
/// </remarks>
public class LogResourceIdentityTests
{
    [Fact]
    public void The_log_exporter_declares_a_service_name()
    {
        // The direct regression. Before this, the attribute dictionary did not exist at all and the
        // sink was configured with an endpoint and nothing else.
        var attributes = LoggingExtensions.BuildLogResourceAttributes();

        attributes.Should().ContainKey(
            "service.name",
            "a log record with no service name is exported as unknown_service:dotnet — delivered, "
            + "stored, queryable, and attributed to nothing. That is harder to notice than losing it, "
            + "because the service it came from simply looks uninstrumented");

        attributes["service.name"].Should().Be(ServiceIdentity.Name);
    }

    [Fact]
    public void The_trace_pipeline_reports_the_same_name_as_the_log_pipeline()
    {
        // Reads the name off a resource the SDK actually built, rather than off the source. If
        // ConfigureResource stops calling AddService, or calls it with something else, this fails —
        // whereas comparing two constants to each other would not.
        var resource = ResourceBuilder.CreateEmpty().AddService(ServiceIdentity.Name).Build();

        var serviceName = resource.Attributes
            .Where(a => a.Key == "service.name")
            .Select(a => a.Value?.ToString())
            .SingleOrDefault();

        serviceName.Should().Be(
            LoggingExtensions.BuildLogResourceAttributes()["service.name"].ToString(),
            "traces and logs must land under one name or the UI shows two services, each with half "
            + "the story — which is worse than one service with no logs, because it looks correct");
    }

    [Fact]
    public void The_service_name_is_the_one_the_dashboards_already_query()
    {
        // Pins the literal value, deliberately. The constant exists so the two pipelines cannot
        // drift apart; it does not stop them drifting away TOGETHER from the name SigNoz already has
        // months of data under. Renaming this is a real decision with a dashboard migration attached,
        // not a refactor, so it should require editing a test that says so.
        ServiceIdentity.Name.Should().Be(
            "Neaslator",
            "this is the serviceName SigNoz already holds this service's traces under; changing it "
            + "splits the history rather than renaming it");
    }
}
