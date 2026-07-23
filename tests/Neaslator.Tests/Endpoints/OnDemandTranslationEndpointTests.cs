using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Neaslator.Domain.Enums;
using Neaslator.Features.OnDemandTranslation;
using Neaslator.Infrastructure.Cache;
using Neaslator.Infrastructure.Hashing;
using Neaslator.Infrastructure.Normalization;
using Neaslator.Infrastructure.Providers;
using NSubstitute;

namespace Neaslator.Tests.Endpoints;

/// <summary>
/// Unit coverage for the on-demand translation endpoint's handler, exercised directly and
/// executed against a <see cref="DefaultHttpContext"/> so the returned <see cref="IResult"/>
/// yields a real status code + body. Covers validation, cache hit, cache-miss provider
/// round-trip (with store), and provider failure/exception mapping.
/// </summary>
public sealed class OnDemandTranslationEndpointTests
{
    private readonly ITranslationCache _cache = Substitute.For<ITranslationCache>();
    private readonly ITranslationRouter _router = Substitute.For<ITranslationRouter>();
    private static readonly IServiceProvider Services = new ServiceCollection().AddLogging().BuildServiceProvider();

    private static async Task<(int status, string body)> Execute(IResult result)
    {
        var ctx = new DefaultHttpContext { RequestServices = Services };
        using var stream = new MemoryStream();
        ctx.Response.Body = stream;
        await result.ExecuteAsync(ctx);
        stream.Position = 0;
        string body = await new StreamReader(stream).ReadToEndAsync();
        return (ctx.Response.StatusCode, body);
    }

    private static OnDemandTranslationRequest Request(string text = "Grilled Chicken", string src = "en", string tgt = "tr")
        => new(text, src, tgt);

    private void CacheMiss()
    {
        _cache.LookupAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                IReadOnlyList<string> targets = ci.ArgAt<IReadOnlyList<string>>(3);
                return (IReadOnlyList<CacheLookupResult>)targets.Select(t => new CacheLookupResult(t, null, CacheSource.Miss)).ToList();
            });
    }

    private void CacheHit(string translated)
    {
        _cache.LookupAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                string text = ci.ArgAt<string>(1);
                IReadOnlyList<string> targets = ci.ArgAt<IReadOnlyList<string>>(3);
                return (IReadOnlyList<CacheLookupResult>)targets
                    .Select(t => new CacheLookupResult(t, new CachedTranslation(translated, TranslationProviderTier.Primary, 1f, text), CacheSource.L1Garnet))
                    .ToList();
            });
    }

    [Theory]
    [InlineData("", "en", "tr", "Text is required")]
    [InlineData("   ", "en", "tr", "Text is required")]
    [InlineData("Soup", "", "tr", "SourceLanguageCode is required")]
    [InlineData("Soup", "en", "", "TargetLanguageCode is required")]
    public async Task Validation_MissingFields_Returns400(string text, string src, string tgt, string expected)
    {
        IResult result = await OnDemandTranslationEndpoint.HandleAsync(Request(text, src, tgt), _cache, _router, CancellationToken.None);
        (int status, string body) = await Execute(result);

        status.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain(expected);
        await _router.DidNotReceive().TranslateAsync(Arg.Any<TranslationBatchRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CacheHit_Returns200_WithCachedText_NoProviderCall()
    {
        CacheHit("Izgara Tavuk");

        IResult result = await OnDemandTranslationEndpoint.HandleAsync(Request(), _cache, _router, CancellationToken.None);
        (int status, string body) = await Execute(result);

        status.Should().Be(StatusCodes.Status200OK);
        body.Should().Contain("Izgara Tavuk");
        await _router.DidNotReceive().TranslateAsync(Arg.Any<TranslationBatchRequest>(), Arg.Any<CancellationToken>());
        await _cache.DidNotReceive().StoreAsync(
            Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<TranslationProviderTier>(), Arg.Any<string>(),
            Arg.Any<float>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CacheMiss_ProviderSuccess_StoresAndReturns200()
    {
        CacheMiss();
        long hash = TranslationHasher.ComputeHash(TextNormalizer.Normalize("Grilled Chicken".AsSpan()));
        _router.TranslateAsync(Arg.Any<TranslationBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TranslationBatchResult
            {
                IsSuccess = true,
                Translations = [new TranslatedUnit { SourceHash = hash, TranslatedName = "Izgara Tavuk" }],
                TokenUsage = new TokenUsage(10, 5, 0),
                ProviderName = "deepseek",
                ProviderTier = TranslationProviderTier.Primary
            });

        IResult result = await OnDemandTranslationEndpoint.HandleAsync(Request(), _cache, _router, CancellationToken.None);
        (int status, string body) = await Execute(result);

        status.Should().Be(StatusCodes.Status200OK);
        body.Should().Contain("Izgara Tavuk");
        await _cache.Received(1).StoreAsync(
            hash, "Grilled Chicken", "en", "tr", "Izgara Tavuk",
            TranslationProviderTier.Primary, "deepseek", Arg.Any<float>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CacheMiss_ProviderFailure_Returns400()
    {
        CacheMiss();
        _router.TranslateAsync(Arg.Any<TranslationBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TranslationBatchResult
            {
                IsSuccess = false,
                Translations = [],
                TokenUsage = new TokenUsage(0, 0, 0),
                ErrorMessage = "all providers exhausted"
            });

        IResult result = await OnDemandTranslationEndpoint.HandleAsync(Request(), _cache, _router, CancellationToken.None);
        (int status, string body) = await Execute(result);

        status.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("all providers exhausted");
    }

    [Fact]
    public async Task CacheMiss_ProviderThrows_Returns400WithMessage()
    {
        CacheMiss();
        _router.TranslateAsync(Arg.Any<TranslationBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns<TranslationBatchResult>(_ => throw new InvalidOperationException("boom-network"));

        IResult result = await OnDemandTranslationEndpoint.HandleAsync(Request(), _cache, _router, CancellationToken.None);
        (int status, string body) = await Execute(result);

        status.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("boom-network");
    }

    [Fact]
    public async Task Response_IsWellFormedJson()
    {
        CacheHit("Izgara Tavuk");

        IResult result = await OnDemandTranslationEndpoint.HandleAsync(Request(), _cache, _router, CancellationToken.None);
        (_, string body) = await Execute(result);

        using JsonDocument doc = JsonDocument.Parse(body);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
    }
}
