using FluentAssertions;
using Microsoft.Extensions.Logging;
using Neaslator.Domain.Enums;
using Neaslator.Infrastructure.Providers;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Polly;
using Polly.CircuitBreaker;
using Polly.RateLimiting;

namespace Neaslator.Tests.Providers;

public sealed class TranslationRouterTests
{
    private readonly ILogger<TranslationRouter> _logger = Substitute.For<ILogger<TranslationRouter>>();

    private static TranslationBatchRequest CreateRequest()
    {
        return new TranslationBatchRequest
        {
            SourceLanguageCode = "en",
            TargetLanguageCode = "fr",
            VenueType = "Restaurant",
            CuisineType = "Italian",
            SectionName = "Starters",
            Items =
            [
                new TranslationBatchItem
                {
                    SourceHash = 12345L,
                    Name = "Grilled Chicken"
                }
            ]
        };
    }

    private static TranslationBatchResult CreateSuccessResult()
    {
        return new TranslationBatchResult
        {
            IsSuccess = true,
            Translations =
            [
                new TranslatedUnit
                {
                    SourceHash = 12345L,
                    TranslatedName = "Poulet Grille"
                }
            ],
            TokenUsage = new TokenUsage(100, 50, 0)
        };
    }

    private static TranslationBatchResult CreateFailureResult()
    {
        return new TranslationBatchResult
        {
            IsSuccess = false,
            Translations = [],
            TokenUsage = new TokenUsage(100, 0, 0),
            ErrorMessage = "Provider error"
        };
    }

    private static ProviderRegistration CreateRegistration(
        ITranslationProvider provider,
        bool isAvailable = true)
    {
        return new ProviderRegistration
        {
            Provider = provider,
            Pipeline = ResiliencePipeline.Empty,
            IsAvailable = isAvailable
        };
    }

    private static ITranslationProvider CreateMockProvider(
        string name = "test-provider",
        TranslationProviderTier tier = TranslationProviderTier.Primary)
    {
        ITranslationProvider provider = Substitute.For<ITranslationProvider>();
        provider.ProviderName.Returns(name);
        provider.Tier.Returns(tier);
        return provider;
    }

    [Fact]
    public async Task SingleProviderSuccess_ReturnsResult()
    {
        ITranslationProvider provider = CreateMockProvider("deepseek");
        provider.TranslateBatchAsync(Arg.Any<TranslationBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(CreateSuccessResult());

        TranslationRouter router = new([CreateRegistration(provider)], _logger);

        TranslationBatchResult result = await router.TranslateAsync(CreateRequest(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Translations.Should().HaveCount(1);
        result.ProviderName.Should().Be("deepseek");
        result.ProviderTier.Should().Be(TranslationProviderTier.Primary);
    }

    [Fact]
    public async Task SingleProviderFailure_ThrowsInvalidOperationException()
    {
        ITranslationProvider provider = CreateMockProvider();
        provider.TranslateBatchAsync(Arg.Any<TranslationBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(CreateFailureResult());

        TranslationRouter router = new([CreateRegistration(provider)], _logger);

        Func<Task> act = async () => await router.TranslateAsync(CreateRequest(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*All translation providers exhausted*");
    }

    [Fact]
    public async Task UnavailableProviderSkipped_NextProviderUsed()
    {
        ITranslationProvider unavailable = CreateMockProvider("unavailable");
        ITranslationProvider available = CreateMockProvider("available", TranslationProviderTier.Secondary);
        available.TranslateBatchAsync(Arg.Any<TranslationBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(CreateSuccessResult());

        TranslationRouter router = new(
        [
            CreateRegistration(unavailable, isAvailable: false),
            CreateRegistration(available)
        ], _logger);

        TranslationBatchResult result = await router.TranslateAsync(CreateRequest(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.ProviderName.Should().Be("available");
        result.ProviderTier.Should().Be(TranslationProviderTier.Secondary);
        await unavailable.DidNotReceive().TranslateBatchAsync(Arg.Any<TranslationBatchRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FirstProviderFails_FallsBackToSecond()
    {
        ITranslationProvider primary = CreateMockProvider("primary");
        primary.TranslateBatchAsync(Arg.Any<TranslationBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(CreateFailureResult());

        ITranslationProvider secondary = CreateMockProvider("secondary", TranslationProviderTier.Secondary);
        secondary.TranslateBatchAsync(Arg.Any<TranslationBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(CreateSuccessResult());

        TranslationRouter router = new(
        [
            CreateRegistration(primary),
            CreateRegistration(secondary)
        ], _logger);

        TranslationBatchResult result = await router.TranslateAsync(CreateRequest(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.ProviderName.Should().Be("secondary");
        await primary.Received(1).TranslateBatchAsync(Arg.Any<TranslationBatchRequest>(), Arg.Any<CancellationToken>());
        await secondary.Received(1).TranslateBatchAsync(Arg.Any<TranslationBatchRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BrokenCircuitException_CaughtAndNextProviderTried()
    {
        ITranslationProvider broken = CreateMockProvider("broken");
        broken.TranslateBatchAsync(Arg.Any<TranslationBatchRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new BrokenCircuitException());

        ITranslationProvider healthy = CreateMockProvider("healthy", TranslationProviderTier.Secondary);
        healthy.TranslateBatchAsync(Arg.Any<TranslationBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(CreateSuccessResult());

        TranslationRouter router = new(
        [
            CreateRegistration(broken),
            CreateRegistration(healthy)
        ], _logger);

        TranslationBatchResult result = await router.TranslateAsync(CreateRequest(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.ProviderName.Should().Be("healthy");
    }

    [Fact]
    public async Task RateLimiterRejectedException_CaughtAndNextProviderTried()
    {
        ITranslationProvider rateLimited = CreateMockProvider("rate-limited");
        rateLimited.TranslateBatchAsync(Arg.Any<TranslationBatchRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new RateLimiterRejectedException());

        ITranslationProvider fallback = CreateMockProvider("fallback", TranslationProviderTier.Secondary);
        fallback.TranslateBatchAsync(Arg.Any<TranslationBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(CreateSuccessResult());

        TranslationRouter router = new(
        [
            CreateRegistration(rateLimited),
            CreateRegistration(fallback)
        ], _logger);

        TranslationBatchResult result = await router.TranslateAsync(CreateRequest(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.ProviderName.Should().Be("fallback");
    }

    [Fact]
    public async Task AllProvidersUnavailable_ThrowsWithEmptyAttempted()
    {
        ITranslationProvider p1 = CreateMockProvider("p1");
        ITranslationProvider p2 = CreateMockProvider("p2");

        TranslationRouter router = new(
        [
            CreateRegistration(p1, isAvailable: false),
            CreateRegistration(p2, isAvailable: false)
        ], _logger);

        Func<Task> act = async () => await router.TranslateAsync(CreateRequest(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*All translation providers exhausted*");
    }

    [Fact]
    public async Task AllProvidersFail_ExceptionMessageContainsAttemptedNames()
    {
        ITranslationProvider p1 = CreateMockProvider("alpha");
        p1.TranslateBatchAsync(Arg.Any<TranslationBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(CreateFailureResult());

        ITranslationProvider p2 = CreateMockProvider("beta");
        p2.TranslateBatchAsync(Arg.Any<TranslationBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(CreateFailureResult());

        TranslationRouter router = new([CreateRegistration(p1), CreateRegistration(p2)], _logger);

        Func<Task> act = async () => await router.TranslateAsync(CreateRequest(), CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("alpha").And.Contain("beta");
    }

    [Fact]
    public async Task SuccessResult_IncludesProviderTierFromRegistration()
    {
        ITranslationProvider degraded = CreateMockProvider("degraded", TranslationProviderTier.Degraded);
        degraded.TranslateBatchAsync(Arg.Any<TranslationBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(CreateSuccessResult());

        TranslationRouter router = new([CreateRegistration(degraded)], _logger);

        TranslationBatchResult result = await router.TranslateAsync(CreateRequest(), CancellationToken.None);

        result.ProviderTier.Should().Be(TranslationProviderTier.Degraded);
    }

    [Fact]
    public async Task EmptyProviderChain_ThrowsInvalidOperationException()
    {
        TranslationRouter router = new([], _logger);

        Func<Task> act = async () => await router.TranslateAsync(CreateRequest(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ProviderThrowsGenericException_NotCaught_PropagatesUp()
    {
        ITranslationProvider provider = CreateMockProvider();
        provider.TranslateBatchAsync(Arg.Any<TranslationBatchRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Network error"));

        TranslationRouter router = new([CreateRegistration(provider)], _logger);

        Func<Task> act = async () => await router.TranslateAsync(CreateRequest(), CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }
}
