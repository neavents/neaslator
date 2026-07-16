using System.Reflection;
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
                    serviceName: "Neaslator",
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
                    {
                        options.Endpoint = new Uri(otlpEndpoint);
                    });
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
                    {
                        options.Endpoint = new Uri(otlpEndpoint);
                    });
                }
            });

        return services;
    }
}
