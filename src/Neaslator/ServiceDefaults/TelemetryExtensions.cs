using System.Reflection;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Neaslator.Observability;
using OpenTelemetry;

namespace Neaslator.ServiceDefaults;

public static class TelemetryExtensions
{
    public static IServiceCollection AddNeaslatorTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        string? otlpEndpoint = configuration["OpenTelemetry:OtlpEndpoint"];

        // Protocol and signal path, neither of which was set, and between them this service has never
        // delivered a span or a metric. The OTLP exporter defaults to gRPC (port 4317) while compose
        // points every service at 4318, the http/protobuf port; and assigning Endpoint in code stops
        // the SDK appending the per-signal path, so exports went to the collector root and came back
        // 404. The exporter raises its own failures on an EventSource with no listener attached, so
        // none of it ever surfaced.
        OtlpExportProtocol otlpProtocol = ResolveOtlpProtocol();

        string environment = configuration["DOTNET_ENVIRONMENT"]
            ?? configuration["ASPNETCORE_ENVIRONMENT"]
            ?? "Production";
        string serviceVersion = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            ?? "0.0.0";

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(
                    serviceName: ServiceIdentity.Name,
                    serviceVersion: serviceVersion,
                    serviceInstanceId: Environment.MachineName)
                .AddAttributes(
                [
                    new("deployment.environment", environment),
                    new("service.namespace", "neavents"),
                    new("host.name", Environment.MachineName),
                    new("process.runtime.name", ".NET"),
                    new("process.runtime.version", Environment.Version.ToString()),
                    new("process.pid", Environment.ProcessId)
                ]))
            .WithTracing(tracing =>
            {
                foreach (string sourceName in NeaslatorActivitySources.AllSourceNames)
                    tracing.AddSource(sourceName);

                tracing.AddSource("MassTransit");

                tracing
                    .AddAspNetCoreInstrumentation(options =>
                    {
                        options.RecordException = true;
                        options.Filter = httpContext =>
                            !httpContext.Request.Path.StartsWithSegments("/health")
                            && !httpContext.Request.Path.StartsWithSegments("/healthz");
                    })
                    .AddHttpClientInstrumentation(options =>
                    {
                        options.RecordException = true;
                    })
                    .AddEntityFrameworkCoreInstrumentation()
                    .AddRedisInstrumentation();

                string samplerStrategy = configuration["OpenTelemetry:Sampler"] ?? "always_on";
                double samplerRatio = double.TryParse(configuration["OpenTelemetry:SamplerRatio"], out double r) ? r : 1.0;

                tracing.SetSampler(samplerStrategy.ToLowerInvariant() switch
                {
                    "always_off" => new AlwaysOffSampler(),
                    "trace_id_ratio" => new TraceIdRatioBasedSampler(samplerRatio),
                    "parent_based" => new ParentBasedSampler(new TraceIdRatioBasedSampler(samplerRatio)),
                    _ => new AlwaysOnSampler()
                });

                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    tracing.AddOtlpExporter(options =>
                        ConfigureOtlpExporter(options, otlpEndpoint, otlpProtocol, "v1/traces"));
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter("Neaslator")
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();

                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    metrics.AddOtlpExporter(options =>
                        ConfigureOtlpExporter(options, otlpEndpoint, otlpProtocol, "v1/metrics"));
                }
            });

        return services;
    }

    /// <summary>
    /// The protocol to export with, from the standard OTLP environment variable.
    /// </summary>
    /// <remarks>
    /// Defaults to http/protobuf rather than the SDK's gRPC, because that is the port this estate's
    /// collector is addressed on everywhere. Both mismatches — gRPC to 4318, http/protobuf to 4317 —
    /// fail silently, so the default that matches the deployment is the one that cannot be quietly
    /// wrong.
    /// </remarks>
    internal static OtlpExportProtocol ResolveOtlpProtocol() =>
        Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL")?.Trim().ToLowerInvariant() switch
        {
            "grpc" => OtlpExportProtocol.Grpc,
            _ => OtlpExportProtocol.HttpProtobuf,
        };

    /// <summary>
    /// Points one exporter at the collector, with the signal path the protocol requires.
    /// </summary>
    /// <remarks>
    /// http/protobuf addresses each signal separately — <c>/v1/traces</c>, <c>/v1/metrics</c> — and the
    /// SDK only appends that itself when it read the endpoint from the environment. Setting
    /// <c>Endpoint</c> in code means supplying the path too; without it every export is a POST to the
    /// collector root and comes back 404. gRPC multiplexes signals over one address, so it takes the
    /// endpoint unchanged.
    /// </remarks>
    internal static void ConfigureOtlpExporter(
        OtlpExporterOptions options, string endpoint, OtlpExportProtocol protocol, string signalPath)
    {
        options.Protocol = protocol;
        options.Endpoint = protocol == OtlpExportProtocol.HttpProtobuf
            ? new Uri($"{endpoint.TrimEnd('/')}/{signalPath}")
            : new Uri(endpoint);
    }
}
