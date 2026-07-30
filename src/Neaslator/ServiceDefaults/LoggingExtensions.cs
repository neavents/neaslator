using Serilog.Sinks.OpenTelemetry;
using Serilog;
using Serilog.Events;

namespace Neaslator.ServiceDefaults;

public static class LoggingExtensions
{
    public static WebApplicationBuilder AddNeaslatorLogging(this WebApplicationBuilder builder)
    {
        builder.Host.UseSerilog((context, services, configuration) =>
        {
            configuration
                .MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .MinimumLevel.Override("System", LogEventLevel.Warning)
                .ReadFrom.Configuration(context.Configuration)
                .Enrich.FromLogContext()
                .Enrich.WithMachineName()
                .Enrich.WithThreadId()
                .WriteTo.Console()
                .WriteTo.OpenTelemetry(options =>
                {
                    options.Endpoint = context.Configuration["OpenTelemetry:OtlpEndpoint"] ?? "http://localhost:4318";

                    // Protocol resolved from the same variable the traces exporter uses, NOT left to
                    // the sink's default.
                    //
                    // Serilog's OpenTelemetry sink defaults to gRPC, and the endpoint above is :4318 —
                    // the HTTP/protobuf port. gRPC to an HTTP/protobuf port delivers NOTHING, silently:
                    // the sink retries in the background and the only symptom is an empty log view,
                    // which reads as "not wired up yet" rather than "disconnected".
                    //
                    // identity, messaging and subscription each shipped this exact mismatch. The
                    // endpoint and the protocol are set in different places by different people, and
                    // neither looks wrong on its own.
                    options.Protocol =
                        Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL")
                            ?.Trim().ToLowerInvariant() switch
                        {
                            "grpc" => OtlpProtocol.Grpc,
                            "http/protobuf" => OtlpProtocol.HttpProtobuf,

                            // No explicit protocol: INFER FROM THE PORT rather than defaulting.
                            // A default is a guess that can disagree with the endpoint, and that
                            // disagreement is what shipped in four services here. The port is not a
                            // guess — 4317 IS gRPC, 4318 IS HTTP/protobuf — so it cannot contradict
                            // where the collector actually listens. Pattern taken from qrmenu-edge.
                            _ => options.Endpoint?.Contains(":4317", StringComparison.Ordinal) == true
                                ? OtlpProtocol.Grpc
                                : OtlpProtocol.HttpProtobuf,
                        };

                    // The log pipeline has its OWN resource, and without this it has no service name.
                    //
                    // The OpenTelemetry SDK builds a resource for traces and metrics; Serilog's sink
                    // builds a separate one for logs, and shares nothing with it. Leaving this unset
                    // does not drop the logs — it delivers them under the SDK's fallback name,
                    // "unknown_service:dotnet". Found live in SigNoz: 1,904 log lines in two hours,
                    // every one of them from this service, all filed under a name that matches no
                    // service and no trace.
                    //
                    // That is worse than losing them. Missing logs prompt a search; logs sitting in an
                    // "unknown" bucket look like somebody else's problem, and the service they came
                    // from reads as having no logging at all. It also breaks the join that makes
                    // tracing useful — traces arrive as "Neaslator" and its logs did not, so no
                    // amount of correlation-id plumbing would have connected them.
                    //
                    // Must match the name the tracer registers, exactly. A near-miss is two services
                    // in the UI.
                    options.ResourceAttributes = BuildLogResourceAttributes();
                });
        });

        return builder;
    }

    /// <summary>The resource attributes stamped on every exported log record.</summary>
    /// <remarks>
    /// A method rather than an inline object literal so there is something to assert against. The
    /// defect this guards produced no error and no missing data: logs arrived under
    /// <c>unknown_service:dotnet</c>, the SDK's fallback, which is indistinguishable from a service
    /// that has not been instrumented yet unless you go looking in the bucket. Nothing inside a
    /// configuration lambda can be tested, so the lambda now calls this.
    /// </remarks>
    internal static Dictionary<string, object> BuildLogResourceAttributes() => new()
    {
        ["service.name"] = ServiceIdentity.Name,
    };

}
