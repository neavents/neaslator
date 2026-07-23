using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Neaslator.Domain.Entities;
using Neaslator.Domain.Enums;
using Neaslator.Features.TranslateMenu;
using Neaslator.Infrastructure.Cache;
using Neaslator.Infrastructure.Diff;
using Neaslator.Infrastructure.Hashing;
using Neaslator.Infrastructure.Normalization;
using Neaslator.Infrastructure.Providers;
using Neaslator.Persistence;
using NSubstitute;

namespace Neaslator.Tests.Pipeline;

/// <summary>
/// Coverage for the pipeline's fan-out mechanics that the base suite does not touch:
/// the 20-unit batch chunking, first-chunk failure short-circuit, provider-tier
/// propagation into the cache store, and partial per-language cache resolution.
/// Uses a mocked <see cref="ITranslationCache"/> so store calls can be asserted exactly.
/// </summary>
public sealed class TranslationPipelineBatchingTests : IDisposable
{
    private readonly NeaslatorDbContext _db;
    private readonly ITranslationCache _cache = Substitute.For<ITranslationCache>();
    private readonly ITranslationRouter _router = Substitute.For<ITranslationRouter>();
    private readonly TranslationPipeline _pipeline;

    public TranslationPipelineBatchingTests()
    {
        DbContextOptions<NeaslatorDbContext> options = new DbContextOptionsBuilder<NeaslatorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new NeaslatorDbContext(options);

        IServiceProvider sp = Substitute.For<IServiceProvider>();
        sp.GetService(typeof(ITranslationCache)).Returns(_cache);
        IServiceScope scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(sp);
        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);
        // CreateAsyncScope() is an extension over CreateScope(); the substitute above serves it.

        _pipeline = new TranslationPipeline(_db, _cache, _router, scopeFactory, Substitute.For<ILogger<TranslationPipeline>>());
    }

    public void Dispose() => _db.Dispose();

    private async Task SeedLanguages(params string[] codes)
    {
        foreach (string code in codes)
            _db.SupportedLanguages.Add(new SupportedLanguage { Code = code, EnglishName = code, NativeName = code, IsActive = true });
        await _db.SaveChangesAsync();
    }

    private void AllMisses()
    {
        _cache.LookupAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                IReadOnlyList<string> targets = ci.ArgAt<IReadOnlyList<string>>(3);
                return (IReadOnlyList<CacheLookupResult>)targets
                    .Select(t => new CacheLookupResult(t, null, CacheSource.Miss)).ToList();
            });
    }

    private void RouterSucceedsEchoing(TranslationProviderTier tier = TranslationProviderTier.Primary, string provider = "deepseek")
    {
        _router.TranslateAsync(Arg.Any<TranslationBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                TranslationBatchRequest req = ci.ArgAt<TranslationBatchRequest>(0);
                return new TranslationBatchResult
                {
                    IsSuccess = true,
                    Translations = req.Items.Select(i => new TranslatedUnit { SourceHash = i.SourceHash, TranslatedName = $"t-{i.SourceHash}" }).ToList(),
                    TokenUsage = new TokenUsage(10, 10, 0),
                    ProviderName = provider,
                    ProviderTier = tier
                };
            });
    }

    private static long HashOf(string text) => TranslationHasher.ComputeHash(TextNormalizer.Normalize(text.AsSpan()));

    // Section with DoNotTranslateName so ONLY item-name units are produced (deterministic counts).
    private static MenuSnapshot MenuWithItems(int count, string prefix = "Item")
    {
        var items = Enumerable.Range(0, count).Select(i => new ItemSnapshot
        {
            Id = Ulid.NewUlid(),
            Name = $"{prefix} {i}"
        }).ToList();

        return new MenuSnapshot
        {
            Sections = [new SectionSnapshot { Id = Ulid.NewUlid(), Name = "Menu", DoNotTranslateName = true, Items = items }]
        };
    }

    [Fact]
    public async Task MoreThanBatchSize_SplitsIntoMultipleBatches()
    {
        await SeedLanguages("fr");
        AllMisses();
        var batchSizes = new ConcurrentBag<int>();
        _router.TranslateAsync(Arg.Any<TranslationBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                TranslationBatchRequest req = ci.ArgAt<TranslationBatchRequest>(0);
                batchSizes.Add(req.Items.Count);
                return new TranslationBatchResult
                {
                    IsSuccess = true,
                    Translations = req.Items.Select(i => new TranslatedUnit { SourceHash = i.SourceHash, TranslatedName = "x" }).ToList(),
                    TokenUsage = new TokenUsage(1, 1, 0),
                    ProviderName = "deepseek",
                    ProviderTier = TranslationProviderTier.Primary
                };
            });

        // 25 item-name units for one language -> chunks of 20 and 5.
        TranslationPipelineResult result = await _pipeline.ExecuteAsync(
            MenuWithItems(25), null, "en", "Restaurant", "Italian", CancellationToken.None);

        await _router.Received(2).TranslateAsync(Arg.Any<TranslationBatchRequest>(), Arg.Any<CancellationToken>());
        batchSizes.OrderByDescending(x => x).Should().ContainInOrder(20, 5);
        result.CompletedLanguages.Should().Be(1);
    }

    [Fact]
    public async Task ExactlyBatchSize_SingleBatch()
    {
        await SeedLanguages("fr");
        AllMisses();
        RouterSucceedsEchoing();

        await _pipeline.ExecuteAsync(MenuWithItems(20), null, "en", "Restaurant", "Italian", CancellationToken.None);

        await _router.Received(1).TranslateAsync(Arg.Any<TranslationBatchRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FirstChunkFails_RemainingChunksSkipped_LanguageFailed()
    {
        await SeedLanguages("fr");
        AllMisses();
        _router.TranslateAsync(Arg.Any<TranslationBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TranslationBatchResult
            {
                IsSuccess = false,
                Translations = [],
                TokenUsage = new TokenUsage(0, 0, 0),
                ErrorMessage = "provider down"
            });

        // 25 units -> 2 chunks, but the first failing chunk short-circuits the language.
        TranslationPipelineResult result = await _pipeline.ExecuteAsync(
            MenuWithItems(25), null, "en", "Restaurant", "Italian", CancellationToken.None);

        await _router.Received(1).TranslateAsync(Arg.Any<TranslationBatchRequest>(), Arg.Any<CancellationToken>());
        result.FailedLanguages.Should().Be(1);
        result.Results.Should().ContainSingle(r => r.TargetLanguageCode == "fr" && !r.IsSuccess);
    }

    [Fact]
    public async Task ProviderTierAndName_PropagateIntoCacheStore()
    {
        await SeedLanguages("fr");
        AllMisses();
        RouterSucceedsEchoing(TranslationProviderTier.Secondary, "openai");

        long hash = HashOf("Item 0");

        await _pipeline.ExecuteAsync(MenuWithItems(1), null, "en", "Restaurant", "Italian", CancellationToken.None);

        await _cache.Received(1).StoreAsync(
            hash, "Item 0", "en", "fr", $"t-{hash}",
            TranslationProviderTier.Secondary, "openai", Arg.Any<float>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProviderReturnsUnknownHash_NotStored_ButLanguageStillSucceeds()
    {
        await SeedLanguages("fr");
        AllMisses();
        _router.TranslateAsync(Arg.Any<TranslationBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TranslationBatchResult
            {
                IsSuccess = true,
                Translations = [new TranslatedUnit { SourceHash = 424242L, TranslatedName = "ghost" }],
                TokenUsage = new TokenUsage(1, 1, 0),
                ProviderName = "deepseek",
                ProviderTier = TranslationProviderTier.Primary
            });

        TranslationPipelineResult result = await _pipeline.ExecuteAsync(
            MenuWithItems(1), null, "en", "Restaurant", "Italian", CancellationToken.None);

        await _cache.DidNotReceive().StoreAsync(
            Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<TranslationProviderTier>(), Arg.Any<string>(),
            Arg.Any<float>(), Arg.Any<CancellationToken>());
        result.CompletedLanguages.Should().Be(1);
    }

    [Fact]
    public async Task PartialCachePerLanguage_OnlyMissedUnitsSentToProvider()
    {
        await SeedLanguages("fr");

        long cachedHash = HashOf("Item 0");
        long missedHash = HashOf("Item 1");

        _cache.LookupAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                long h = ci.ArgAt<long>(0);
                IReadOnlyList<string> targets = ci.ArgAt<IReadOnlyList<string>>(3);
                return (IReadOnlyList<CacheLookupResult>)targets.Select(t =>
                    h == cachedHash
                        ? new CacheLookupResult(t, new CachedTranslation("cached", TranslationProviderTier.Primary, 1f, "Item 0"), CacheSource.L1Garnet)
                        : new CacheLookupResult(t, null, CacheSource.Miss)).ToList();
            });

        var sentHashes = new ConcurrentBag<long>();
        _router.TranslateAsync(Arg.Any<TranslationBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                TranslationBatchRequest req = ci.ArgAt<TranslationBatchRequest>(0);
                foreach (var item in req.Items) sentHashes.Add(item.SourceHash);
                return new TranslationBatchResult
                {
                    IsSuccess = true,
                    Translations = req.Items.Select(i => new TranslatedUnit { SourceHash = i.SourceHash, TranslatedName = "x" }).ToList(),
                    TokenUsage = new TokenUsage(1, 1, 0),
                    ProviderName = "deepseek",
                    ProviderTier = TranslationProviderTier.Primary
                };
            });

        TranslationPipelineResult result = await _pipeline.ExecuteAsync(
            MenuWithItems(2), null, "en", "Restaurant", "Italian", CancellationToken.None);

        sentHashes.Should().ContainSingle().And.Contain(missedHash);
        sentHashes.Should().NotContain(cachedHash);
        result.CompletedLanguages.Should().Be(1);
    }

    [Fact]
    public async Task MixedLanguages_OneFullyCachedOneMissed_OnlyMissedHitsProvider()
    {
        await SeedLanguages("fr", "de");

        // fr fully cached, de all misses.
        _cache.LookupAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                IReadOnlyList<string> targets = ci.ArgAt<IReadOnlyList<string>>(3);
                return (IReadOnlyList<CacheLookupResult>)targets.Select(t =>
                    t == "fr"
                        ? new CacheLookupResult(t, new CachedTranslation("c", TranslationProviderTier.Primary, 1f, "Item 0"), CacheSource.L2PostgreSql)
                        : new CacheLookupResult(t, null, CacheSource.Miss)).ToList();
            });

        var sentTargets = new ConcurrentBag<string>();
        _router.TranslateAsync(Arg.Any<TranslationBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                TranslationBatchRequest req = ci.ArgAt<TranslationBatchRequest>(0);
                sentTargets.Add(req.TargetLanguageCode);
                return new TranslationBatchResult
                {
                    IsSuccess = true,
                    Translations = req.Items.Select(i => new TranslatedUnit { SourceHash = i.SourceHash, TranslatedName = "x" }).ToList(),
                    TokenUsage = new TokenUsage(1, 1, 0),
                    ProviderName = "deepseek",
                    ProviderTier = TranslationProviderTier.Primary
                };
            });

        TranslationPipelineResult result = await _pipeline.ExecuteAsync(
            MenuWithItems(1), null, "en", "Restaurant", "Italian", CancellationToken.None);

        sentTargets.Should().OnlyContain(t => t == "de");
        result.TotalLanguages.Should().Be(2);
        result.CompletedLanguages.Should().Be(2);
        result.Results.Should().Contain(r => r.TargetLanguageCode == "fr" && r.IsSuccess);
    }
}
