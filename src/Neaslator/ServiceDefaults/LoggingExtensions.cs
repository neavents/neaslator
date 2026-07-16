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
                });
        });

        return builder;
    }
}
