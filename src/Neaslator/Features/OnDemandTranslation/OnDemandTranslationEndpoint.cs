using System.Diagnostics;
using Neaslator.Infrastructure.Cache;
using Neaslator.Infrastructure.Hashing;
using Neaslator.Infrastructure.Normalization;
using Neaslator.Infrastructure.Providers;
using Neaslator.Observability;

namespace Neaslator.Features.OnDemandTranslation;

public static class OnDemandTranslationEndpoint
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/api/v1/translate/on-demand", (
            OnDemandTranslationRequest request,
            ITranslationCache cache,
            ITranslationRouter router,
            CancellationToken ct) => HandleAsync(request, cache, router, ct));
    }

    internal static async Task<IResult> HandleAsync(
        OnDemandTranslationRequest request,
        ITranslationCache cache,
        ITranslationRouter router,
        CancellationToken ct)
    {
        using Activity? activity = NeaslatorActivitySources.OnDemand.StartActivity("OnDemandTranslation");
        long startTicks = Stopwatch.GetTimestamp();

        activity?.SetTag("neaslator.on_demand.source_language", request.SourceLanguageCode);
        activity?.SetTag("neaslator.on_demand.target_language", request.TargetLanguageCode);
        activity?.SetTag("neaslator.on_demand.text_length", request.Text?.Length ?? 0);
        activity?.SetTag("neaslator.on_demand.venue_type", request.VenueType ?? "General");
        activity?.SetTag("neaslator.on_demand.cuisine_type", request.CuisineType ?? "General");

        if (string.IsNullOrWhiteSpace(request.Text))
        {
            activity?.SetStatus(ActivityStatusCode.Error, "Text is required");
            return Results.BadRequest(new { error = "Text is required" });
        }

        if (string.IsNullOrWhiteSpace(request.SourceLanguageCode))
        {
            activity?.SetStatus(ActivityStatusCode.Error, "SourceLanguageCode is required");
            return Results.BadRequest(new { error = "SourceLanguageCode is required" });
        }

        if (string.IsNullOrWhiteSpace(request.TargetLanguageCode))
        {
            activity?.SetStatus(ActivityStatusCode.Error, "TargetLanguageCode is required");
            return Results.BadRequest(new { error = "TargetLanguageCode is required" });
        }

        string normalized = TextNormalizer.Normalize(request.Text);
        long hash = TranslationHasher.ComputeHash(normalized);

        activity?.SetTag("neaslator.on_demand.source_hash", hash);
        activity?.SetTag("neaslator.on_demand.normalized_length", normalized.Length);

        var lookups = await cache.LookupAsync(
            hash,
            normalized,
            request.SourceLanguageCode,
            [request.TargetLanguageCode],
            ct);

        CacheLookupResult lookup = lookups[0];
        if (lookup.Translation is not null)
        {
            double cacheLatency = Stopwatch.GetElapsedTime(startTicks).TotalSeconds;
            activity?.SetTag("neaslator.on_demand.source", lookup.Source.ToString());
            activity?.SetTag("neaslator.on_demand.cache_hit", true);
            activity?.SetTag("neaslator.on_demand.latency_ms", cacheLatency * 1000);
            activity?.AddEvent(new ActivityEvent("cache_hit",
                tags: new ActivityTagsCollection([
                    new("cache_level", lookup.Source.ToString()),
                    new("provider_tier", lookup.Translation.ProviderTier.ToString())
                ])));

            NeaslatorMetrics.OnDemandRequestsTotal.Add(1,
                new KeyValuePair<string, object?>("source", lookup.Source == CacheSource.L1Garnet ? "cache_l1" : "cache_l2"));
            NeaslatorMetrics.OnDemandLatencySeconds.Record(cacheLatency,
                new KeyValuePair<string, object?>("source", "cache"));

            return Results.Ok(new OnDemandTranslationResponse(
                lookup.Translation.TranslatedText,
                lookup.Source.ToString(),
                0));
        }

        activity?.SetTag("neaslator.on_demand.cache_hit", false);
        activity?.AddEvent(new ActivityEvent("cache_miss"));

        TranslationBatchRequest batchRequest = new()
        {
            SourceLanguageCode = request.SourceLanguageCode,
            TargetLanguageCode = request.TargetLanguageCode,
            VenueType = request.VenueType ?? "General",
            CuisineType = request.CuisineType ?? "General",
            SectionName = "On-Demand",
            Items =
            [
                new TranslationBatchItem
                {
                    SourceHash = hash,
                    Name = normalized,
                    Description = null
                }
            ]
        };

        TranslationBatchResult result;
        try
        {
            result = await router.TranslateAsync(batchRequest, ct);
        }
        catch (Exception ex)
        {
            activity?.AddEvent(new ActivityEvent("exception",
                tags: new ActivityTagsCollection([
                    new("exception.type", ex.GetType().FullName ?? ex.GetType().Name),
                    new("exception.message", ex.Message)
                ])));
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            NeaslatorMetrics.OnDemandRequestsTotal.Add(1, new KeyValuePair<string, object?>("source", "error"));
            return Results.BadRequest(new { error = ex.Message });
        }

        if (!result.IsSuccess)
        {
            activity?.SetStatus(ActivityStatusCode.Error, result.ErrorMessage);
            NeaslatorMetrics.OnDemandRequestsTotal.Add(1, new KeyValuePair<string, object?>("source", "error"));
            return Results.BadRequest(new { error = result.ErrorMessage });
        }

        TranslatedUnit translated = result.Translations[0];
        await cache.StoreAsync(
            hash, normalized,
            request.SourceLanguageCode,
            request.TargetLanguageCode,
            translated.TranslatedName,
            result.ProviderTier,
            result.ProviderName,
            translated.ConfidenceScore,
            ct);

        double providerLatency = Stopwatch.GetElapsedTime(startTicks).TotalSeconds;
        activity?.SetTag("neaslator.on_demand.source", "provider");
        activity?.SetTag("neaslator.on_demand.provider", result.ProviderName);
        activity?.SetTag("neaslator.on_demand.provider_tier", result.ProviderTier.ToString());
        activity?.SetTag("neaslator.on_demand.latency_ms", providerLatency * 1000);
        activity?.SetTag("neaslator.on_demand.input_tokens", result.TokenUsage.InputTokens);
        activity?.SetTag("neaslator.on_demand.output_tokens", result.TokenUsage.OutputTokens);

        NeaslatorMetrics.OnDemandRequestsTotal.Add(1, new KeyValuePair<string, object?>("source", "provider"));
        NeaslatorMetrics.OnDemandLatencySeconds.Record(providerLatency,
            new KeyValuePair<string, object?>("source", "provider"));

        return Results.Ok(new OnDemandTranslationResponse(
            translated.TranslatedName,
            "provider",
            (int)result.Latency.TotalMilliseconds));
    }
}

public sealed record OnDemandTranslationRequest(
    string Text,
    string SourceLanguageCode,
    string TargetLanguageCode,
    string? VenueType = null,
    string? CuisineType = null);

public sealed record OnDemandTranslationResponse(
    string TranslatedText,
    string Source,
    int LatencyMs);
