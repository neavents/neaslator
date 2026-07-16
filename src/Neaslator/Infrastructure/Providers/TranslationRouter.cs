using System.Diagnostics;
using Neaslator.Observability;
using Polly;
using Polly.CircuitBreaker;
using Polly.RateLimiting;

namespace Neaslator.Infrastructure.Providers;

public sealed class TranslationRouter : ITranslationRouter
{
    private static readonly Dictionary<string, (double InputPerMillion, double OutputPerMillion)> ProviderPricing = new()
    {
        ["deepseek"] = (0.14, 0.28),
        ["openai"] = (2.50, 10.00),
        ["anthropic"] = (3.00, 15.00),
        ["google"] = (0.075, 0.30)
    };

    private readonly IReadOnlyList<ProviderRegistration> _providerChain;
    private readonly ILogger<TranslationRouter> _logger;

    public TranslationRouter(IReadOnlyList<ProviderRegistration> providerChain, ILogger<TranslationRouter> logger)
    {
        _providerChain = providerChain;
        _logger = logger;
    }

    public async Task<TranslationBatchResult> TranslateAsync(
        TranslationBatchRequest request,
        CancellationToken cancellationToken)
    {
        using Activity? activity = NeaslatorActivitySources.Provider.StartActivity("TranslationRouter.Translate");
        activity?.SetTag("neaslator.source_language", request.SourceLanguageCode);
        activity?.SetTag("neaslator.target_language", request.TargetLanguageCode);
        activity?.SetTag("neaslator.batch_size", request.Items.Count);
        activity?.SetTag("neaslator.venue_type", request.VenueType);
        activity?.SetTag("neaslator.cuisine_type", request.CuisineType);
        activity?.SetTag("neaslator.provider_chain_length", _providerChain.Count);

        List<string> attempted = [];
        int skippedCount = 0;

        foreach (ProviderRegistration registration in _providerChain)
        {
            if (!registration.IsAvailable)
            {
                skippedCount++;
                activity?.AddEvent(new ActivityEvent("provider_skipped",
                    tags: new ActivityTagsCollection([
                        new("provider", registration.Provider.ProviderName),
                        new("reason", "circuit_open_or_rate_limited"),
                        new("tier", registration.Provider.Tier.ToString())
                    ])));
                continue;
            }

            attempted.Add(registration.Provider.ProviderName);

            try
            {
                Stopwatch sw = Stopwatch.StartNew();

                TranslationBatchResult result = await registration.Pipeline
                    .ExecuteAsync(async token =>
                        await registration.Provider.TranslateBatchAsync(request, token),
                        cancellationToken);

                sw.Stop();

                NeaslatorMetrics.ProviderRequests.Add(1,
                    new("provider", registration.Provider.ProviderName),
                    new("status", result.IsSuccess ? "success" : "failure"));
                NeaslatorMetrics.ProviderLatencySeconds.Record(sw.Elapsed.TotalSeconds,
                    new KeyValuePair<string, object?>("provider", registration.Provider.ProviderName));

                if (result.IsSuccess)
                {
                    NeaslatorMetrics.ProviderTokensUsed.Add(result.TokenUsage.InputTokens,
                        new("provider", registration.Provider.ProviderName), new("type", "input"));
                    NeaslatorMetrics.ProviderTokensUsed.Add(result.TokenUsage.OutputTokens,
                        new("provider", registration.Provider.ProviderName), new("type", "output"));
                    NeaslatorMetrics.ProviderTokensUsed.Add(result.TokenUsage.CachedTokens,
                        new("provider", registration.Provider.ProviderName), new("type", "cached"));

                    RecordCostEstimate(registration.Provider.ProviderName, result.TokenUsage);

                    activity?.SetTag("neaslator.provider_used", registration.Provider.ProviderName);
                    activity?.SetTag("neaslator.provider_tier", registration.Provider.Tier.ToString());
                    activity?.SetTag("neaslator.provider_latency_ms", sw.Elapsed.TotalMilliseconds);
                    activity?.SetTag("neaslator.provider_input_tokens", result.TokenUsage.InputTokens);
                    activity?.SetTag("neaslator.provider_output_tokens", result.TokenUsage.OutputTokens);
                    activity?.SetTag("neaslator.provider_cached_tokens", result.TokenUsage.CachedTokens);
                    activity?.SetTag("neaslator.providers_attempted", attempted.Count);
                    activity?.SetTag("neaslator.providers_skipped", skippedCount);

                    if (attempted.Count > 1)
                    {
                        NeaslatorMetrics.ProviderFallbacks.Add(1,
                            new("from", attempted[^2]),
                            new("to", registration.Provider.ProviderName));
                        activity?.AddEvent(new ActivityEvent("provider_fallback",
                            tags: new ActivityTagsCollection([
                                new("fallback_from", string.Join(",", attempted.Take(attempted.Count - 1))),
                                new("fallback_to", registration.Provider.ProviderName)
                            ])));
                    }

                    return result with { ProviderName = registration.Provider.ProviderName, ProviderTier = registration.Provider.Tier };
                }

                activity?.AddEvent(new ActivityEvent("provider_failure",
                    tags: new ActivityTagsCollection([
                        new("provider", registration.Provider.ProviderName),
                        new("error", result.ErrorMessage ?? "unknown"),
                        new("latency_ms", sw.Elapsed.TotalMilliseconds)
                    ])));

                _logger.LogWarning("Provider {Provider} returned failure: {Error}",
                    registration.Provider.ProviderName, result.ErrorMessage);
            }
            catch (BrokenCircuitException bce)
            {
                activity?.AddEvent(new ActivityEvent("circuit_breaker_open",
                    tags: new ActivityTagsCollection([
                        new("provider", registration.Provider.ProviderName),
                        new("exception_type", bce.GetType().Name)
                    ])));
                _logger.LogWarning("Circuit breaker opened for {Provider}",
                    registration.Provider.ProviderName);
            }
            catch (RateLimiterRejectedException rle)
            {
                activity?.AddEvent(new ActivityEvent("rate_limited",
                    tags: new ActivityTagsCollection([
                        new("provider", registration.Provider.ProviderName),
                        new("retry_after_ms", rle.RetryAfter?.TotalMilliseconds ?? -1)
                    ])));
                _logger.LogWarning("Rate limiter rejected for {Provider}",
                    registration.Provider.ProviderName);
            }
        }

        activity?.SetStatus(ActivityStatusCode.Error, "All providers exhausted");
        activity?.SetTag("neaslator.providers_attempted", attempted.Count);
        activity?.SetTag("neaslator.providers_skipped", skippedCount);
        activity?.SetTag("neaslator.all_providers_exhausted", true);

        throw new InvalidOperationException(
            $"All translation providers exhausted. Attempted: [{string.Join(", ", attempted)}]");
    }

    private static void RecordCostEstimate(string providerName, TokenUsage usage)
    {
        if (!ProviderPricing.TryGetValue(providerName.ToLowerInvariant(), out (double InputPerMillion, double OutputPerMillion) pricing))
            return;

        int billableInputTokens = Math.Max(0, usage.InputTokens - usage.CachedTokens);
        double inputCostCents = billableInputTokens / 1_000_000.0 * pricing.InputPerMillion * 100;
        double outputCostCents = usage.OutputTokens / 1_000_000.0 * pricing.OutputPerMillion * 100;
        double totalCostCents = inputCostCents + outputCostCents;

        NeaslatorMetrics.ProviderCostEstimateCents.Add(totalCostCents,
            new("provider", providerName),
            new("cost_type", "total"));
    }
}
