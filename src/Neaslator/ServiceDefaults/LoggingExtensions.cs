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
                });
        });

        return builder;
    }
}
