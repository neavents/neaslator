using System.Diagnostics.Metrics;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Neaslator.Domain.Enums;
using Neaslator.Infrastructure.Providers;
using NSubstitute;
using Polly;

namespace Neaslator.Tests.Providers;

/// <summary>
/// Validates the cost-estimate arithmetic in <see cref="TranslationRouter"/> by capturing
/// the <c>neaslator.provider.cost_estimate_cents</c> instrument with a <see cref="MeterListener"/>.
/// The static Meter is shared across parallel tests, so every assertion keys off a
/// unique provider name or a signature token value that no other test emits.
/// </summary>
public sealed class TranslationRouterCostTests
{
    private readonly ILogger<TranslationRouter> _logger = Substitute.For<ILogger<TranslationRouter>>();

    private sealed record CostMeasurement(double Value, string? Provider);

    private static List<CostMeasurement> Capture(Action action)
    {
        var captured = new List<CostMeasurement>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == "Neaslator" &&
                    instrument.Name == "neaslator.provider.cost_estimate_cents")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            string? provider = null;
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (tag.Key == "provider")
                    provider = tag.Value?.ToString();
            }
            lock (captured)
            {
                captured.Add(new CostMeasurement(value, provider));
            }
        });
        listener.Start();

        action();

        listener.Dispose();
        return captured;
    }

    private static ITranslationProvider Provider(string name, TokenUsage usage)
    {
        ITranslationProvider provider = Substitute.For<ITranslationProvider>();
        provider.ProviderName.Returns(name);
        provider.Tier.Returns(TranslationProviderTier.Primary);
        provider.TranslateBatchAsync(Arg.Any<TranslationBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TranslationBatchResult
            {
                IsSuccess = true,
                Translations = [new TranslatedUnit { SourceHash = 1, TranslatedName = "x" }],
                TokenUsage = usage
            });
        return provider;
    }

    private static TranslationRouter Router(ITranslationProvider provider, ILogger<TranslationRouter> logger) =>
        new([new ProviderRegistration { Provider = provider, Pipeline = ResiliencePipeline.Empty }], logger);

    private static TranslationBatchRequest Request() => new()
    {
        SourceLanguageCode = "en",
        TargetLanguageCode = "fr",
        VenueType = "Restaurant",
        CuisineType = "Italian",
        SectionName = "Starters",
        Items = [new TranslationBatchItem { SourceHash = 1, Name = "Soup" }]
    };

    [Fact]
    public void DeepSeek_CostIsInputPlusOutputInCents()
    {
        // deepseek pricing: input 0.14/M, output 0.28/M.
        // 1M input (0 cached) -> 14c, 1M output -> 28c, total 42c (a value no other test emits).
        TranslationRouter router = Router(Provider("deepseek", new TokenUsage(1_000_000, 1_000_000, 0)), _logger);

        List<CostMeasurement> costs = Capture(() => router.TranslateAsync(Request(), CancellationToken.None).GetAwaiter().GetResult());

        costs.Should().Contain(m => m.Provider == "deepseek" && Math.Abs(m.Value - 42.0) < 1e-6);
    }

    [Fact]
    public void CachedTokens_ReduceBillableInput()
    {
        // 2M input, 1M cached -> 1M billable input -> 14c; 0 output -> 0c. Total 14c.
        TranslationRouter router = Router(Provider("deepseek", new TokenUsage(2_000_000, 0, 1_000_000)), _logger);

        List<CostMeasurement> costs = Capture(() => router.TranslateAsync(Request(), CancellationToken.None).GetAwaiter().GetResult());

        // 14c is also emitted by the 42c test's input portion? No - that test emits a single 42c total.
        // This exact 14.0 total with provider deepseek is this test's signature.
        costs.Should().Contain(m => m.Provider == "deepseek" && Math.Abs(m.Value - 14.0) < 1e-6);
    }

    [Fact]
    public void AllInputCached_CostIsZeroForInputPortion()
    {
        // 500k input fully cached -> billable 0 -> 0c input; 0 output -> total 0c.
        TranslationRouter router = Router(Provider("deepseek", new TokenUsage(500_000, 0, 500_000)), _logger);

        List<CostMeasurement> costs = Capture(() => router.TranslateAsync(Request(), CancellationToken.None).GetAwaiter().GetResult());

        costs.Should().Contain(m => m.Provider == "deepseek" && Math.Abs(m.Value) < 1e-9);
    }

    [Fact]
    public void UnknownProvider_EmitsNoCost()
    {
        TranslationRouter router = Router(Provider("provider-not-in-pricing-table", new TokenUsage(1_000_000, 1_000_000, 0)), _logger);

        List<CostMeasurement> costs = Capture(() => router.TranslateAsync(Request(), CancellationToken.None).GetAwaiter().GetResult());

        costs.Should().NotContain(m => m.Provider == "provider-not-in-pricing-table");
    }

    [Fact]
    public void CachedTokensExceedingInput_DoesNotProduceNegativeCost()
    {
        // Defensive: cached > input should clamp billable input to 0, never negative.
        TranslationRouter router = Router(Provider("deepseek", new TokenUsage(100_000, 0, 999_000_000)), _logger);

        List<CostMeasurement> costs = Capture(() => router.TranslateAsync(Request(), CancellationToken.None).GetAwaiter().GetResult());

        costs.Where(m => m.Provider == "deepseek").Should().OnlyContain(m => m.Value >= 0);
    }
}
