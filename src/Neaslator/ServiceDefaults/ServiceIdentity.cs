namespace Neaslator.ServiceDefaults;

/// <summary>
/// The one place this service's name is written down.
/// </summary>
/// <remarks>
/// <para>
/// Telemetry leaves this process through two independent pipelines that share nothing: the
/// OpenTelemetry SDK builds a resource for traces and metrics, and Serilog's OTLP sink builds a
/// separate one for logs. Each needs a service name, and each was given one separately — or, in the
/// log pipeline's case, not given one at all.
/// </para>
/// <para>
/// The consequence is not lost logs. It is logs delivered under the SDK's fallback name,
/// <c>unknown_service:dotnet</c> — found live in SigNoz, 1,904 lines in two hours, every one from
/// this service, filed under a name matching no service and no trace. Missing logs prompt a search;
/// logs in an "unknown" bucket look like somebody else's problem, and the service they came from
/// reads as having no logging at all.
/// </para>
/// <para>
/// A near-miss between the two names is just as bad in a different way: two services in the UI, each
/// holding half the story. So the name is a constant both pipelines read, rather than a string each
/// pipeline spells. Detecting drift needs a test; not being able to drift needs nothing.
/// </para>
/// </remarks>
internal static class ServiceIdentity
{
    /// <summary>The service name reported on every span, metric and log record.</summary>
    internal const string Name = "Neaslator";
}
