using Neaslator.Infrastructure.Providers;

namespace Neaslator.Features.ProviderHealth;

public static class ProviderHealthEndpoint
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/api/v1/providers/health", async (
            ITranslationProvider provider,
            CancellationToken ct) =>
        {
            bool healthy = await provider.IsHealthyAsync(ct);

            return Results.Ok(new
            {
                provider = provider.ProviderName,
                tier = provider.Tier.ToString(),
                isHealthy = healthy,
                supportsPrefixCaching = provider.SupportsPrefixCaching,
                maxBatchSize = provider.MaxBatchSize,
                maxConcurrentRequests = provider.MaxConcurrentRequests
            });
        });
    }
}
