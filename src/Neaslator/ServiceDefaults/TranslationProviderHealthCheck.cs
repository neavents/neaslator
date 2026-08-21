namespace Neaslator.ServiceDefaults;

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Neaslator.Infrastructure.Providers;

/// <summary>
/// Reports whether this service can still reach a translation provider.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every provider already implemented <see cref="ITranslationProvider.IsHealthyAsync"/> and
/// nothing ever called it.</b> Health covered Postgres, Garnet and RabbitMQ — the three things this
/// service uses to move work around — and not the one thing it uses to do the work. An expired or
/// exhausted API key left all three green while every translation failed, and the first sign was a
/// menu that stayed in one language.
/// </para>
/// <para>
/// <b>Degraded when a tier is lost, unhealthy only when all are.</b> Providers are arranged as a
/// fallback chain, so losing the preferred one is a real problem — quality drops, cost changes —
/// without being an outage. Collapsing both into "unhealthy" would either cry wolf or hide the
/// difference; they are genuinely different states and the data says which is which.
/// </para>
/// <para>
/// Not tagged <c>ready</c>: an instance that cannot reach a provider can still serve every cached
/// translation, which is the majority of its traffic. Taking it out of rotation would convert a
/// degraded service into a smaller one.
/// </para>
/// </remarks>
public sealed class TranslationProviderHealthCheck : IHealthCheck
{
    /// <summary>
    /// A health poll must not inherit a translation call's patience. Providers are reached over the
    /// public internet and the probe answers "cannot reach it" rather than holding the endpoint open.
    /// </summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    private readonly IEnumerable<ITranslationProvider> _providers;
    private readonly ILogger<TranslationProviderHealthCheck> _logger;

    public TranslationProviderHealthCheck(
        IEnumerable<ITranslationProvider> providers,
        ILogger<TranslationProviderHealthCheck> logger)
    {
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        List<ITranslationProvider> providers = [.. _providers];

        if (providers.Count == 0)
        {
            return HealthCheckResult.Unhealthy(
                "No translation provider is registered; nothing can be translated.");
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);

        Dictionary<string, object> data = new(StringComparer.Ordinal);
        List<string> healthy = [];
        List<string> unhealthy = [];

        foreach (ITranslationProvider provider in providers)
        {
            bool ok;
            try
            {
                ok = await provider.IsHealthyAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // A provider that throws when asked whether it is well is not well.
                _logger.LogWarning(ex, "Translation provider {Provider} failed its health probe", provider.ProviderName);
                ok = false;
            }

            (ok ? healthy : unhealthy).Add(provider.ProviderName);
            data[provider.ProviderName] = ok ? "healthy" : "unhealthy";
        }

        if (healthy.Count == 0)
        {
            return HealthCheckResult.Unhealthy(
                $"No translation provider is reachable ({string.Join(", ", unhealthy)}). Translation is failing.",
                data: data);
        }

        if (unhealthy.Count > 0)
        {
            return HealthCheckResult.Degraded(
                $"Translation is running on a fallback: {string.Join(", ", unhealthy)} unreachable, "
                + $"{string.Join(", ", healthy)} still answering.",
                data: data);
        }

        return HealthCheckResult.Healthy($"All {healthy.Count} translation provider(s) reachable.", data: data);
    }
}
