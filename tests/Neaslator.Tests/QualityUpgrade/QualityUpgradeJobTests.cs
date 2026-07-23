using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Neaslator.Domain.Entities;
using Neaslator.Domain.Enums;
using Neaslator.Features.QualityUpgrade;
using Neaslator.Infrastructure.Cache;
using Neaslator.Infrastructure.Providers;
using Neaslator.Persistence;
using NSubstitute;

namespace Neaslator.Tests.QualityUpgrade;

/// <summary>
/// Behavioural coverage for the degraded-translation re-upgrade job: which entries it
/// scans, how it groups them per language pair, and that a failure in one group never
/// aborts the others. Uses a real in-memory SQLite database with mocked router/cache.
/// </summary>
public sealed class QualityUpgradeJobTests : IDisposable
{
    private readonly NeaslatorDbContext _db;
    private readonly ITranslationRouter _router = Substitute.For<ITranslationRouter>();
    private readonly ITranslationCache _cache = Substitute.For<ITranslationCache>();
    private readonly QualityUpgradeJob _sut;

    public QualityUpgradeJobTests()
    {
        // InMemory (not SQLite) because the job orders by DateTimeOffset, which SQLite
        // cannot translate but PostgreSQL (production) and the InMemory provider both can.
        DbContextOptions<NeaslatorDbContext> options = new DbContextOptionsBuilder<NeaslatorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new NeaslatorDbContext(options);

        IServiceProvider sp = Substitute.For<IServiceProvider>();
        sp.GetService(typeof(NeaslatorDbContext)).Returns(_db);
        sp.GetService(typeof(ITranslationRouter)).Returns(_router);
        sp.GetService(typeof(ITranslationCache)).Returns(_cache);

        IServiceScope scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(sp);
        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        _sut = new QualityUpgradeJob(scopeFactory, Substitute.For<ILogger<QualityUpgradeJob>>());
    }

    public void Dispose() => _db.Dispose();

    private async Task Seed(long sourceHash, string normalized, string sourceLang, string targetLang, TranslationProviderTier tier)
    {
        _db.TranslationMemory.Add(new TranslationMemoryEntry
        {
            SourceHash = sourceHash,
            NormalizedSourceText = normalized,
            SourceLanguageCode = sourceLang,
            TargetLanguageCode = targetLang,
            TranslatedText = "old-translation",
            ProviderTier = tier,
            ProviderName = "openai",
            ConfidenceScore = 0.7f,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    private void RouterReturnsSuccessEchoingHashes()
    {
        _router.TranslateAsync(Arg.Any<TranslationBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                TranslationBatchRequest req = callInfo.ArgAt<TranslationBatchRequest>(0);
                return new TranslationBatchResult
                {
                    IsSuccess = true,
                    Translations = req.Items.Select(i => new TranslatedUnit
                    {
                        SourceHash = i.SourceHash,
                        TranslatedName = $"upgraded-{i.SourceHash}"
                    }).ToList(),
                    TokenUsage = new TokenUsage(10, 10, 0),
                    ProviderName = "deepseek",
                    ProviderTier = TranslationProviderTier.Primary
                };
            });
    }

    [Fact]
    public async Task NoDegradedEntries_DoesNotCallRouter()
    {
        await Seed(1L, "Soup", "en", "fr", TranslationProviderTier.Primary);

        await _sut.UpgradeDegradedEntries(CancellationToken.None);

        await _router.DidNotReceive().TranslateAsync(Arg.Any<TranslationBatchRequest>(), Arg.Any<CancellationToken>());
        await _cache.DidNotReceive().StoreAsync(
            Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<TranslationProviderTier>(), Arg.Any<string>(),
            Arg.Any<float>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DegradedEntries_UpgradedAndStoredAsPrimary()
    {
        await Seed(10L, "Bruschetta", "en", "fr", TranslationProviderTier.Secondary);
        RouterReturnsSuccessEchoingHashes();

        await _sut.UpgradeDegradedEntries(CancellationToken.None);

        await _router.Received(1).TranslateAsync(Arg.Any<TranslationBatchRequest>(), Arg.Any<CancellationToken>());
        await _cache.Received(1).StoreAsync(
            10L, "Bruschetta", "en", "fr", "upgraded-10",
            TranslationProviderTier.Primary, "quality_upgrade", Arg.Any<float>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DegradedTier_BothSecondaryAndDegraded_AreScanned()
    {
        await Seed(20L, "A", "en", "fr", TranslationProviderTier.Secondary);
        await Seed(21L, "B", "en", "fr", TranslationProviderTier.Degraded);
        RouterReturnsSuccessEchoingHashes();

        await _sut.UpgradeDegradedEntries(CancellationToken.None);

        // Same language pair -> one grouped request carrying both items.
        await _router.Received(1).TranslateAsync(
            Arg.Is<TranslationBatchRequest>(r => r.Items.Count == 2), Arg.Any<CancellationToken>());
        await _cache.Received(2).StoreAsync(
            Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), TranslationProviderTier.Primary, "quality_upgrade",
            Arg.Any<float>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DifferentLanguagePairs_AreGroupedSeparately()
    {
        await Seed(30L, "A", "en", "fr", TranslationProviderTier.Secondary);
        await Seed(31L, "B", "en", "de", TranslationProviderTier.Secondary);
        await Seed(32L, "C", "es", "fr", TranslationProviderTier.Secondary);
        RouterReturnsSuccessEchoingHashes();

        await _sut.UpgradeDegradedEntries(CancellationToken.None);

        // 3 distinct (source, target) pairs -> 3 router calls.
        await _router.Received(3).TranslateAsync(Arg.Any<TranslationBatchRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GroupTranslationFails_OtherGroupsStillProcessed()
    {
        await Seed(40L, "A", "en", "fr", TranslationProviderTier.Secondary);
        await Seed(41L, "B", "en", "de", TranslationProviderTier.Secondary);

        _router.TranslateAsync(Arg.Any<TranslationBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                TranslationBatchRequest req = callInfo.ArgAt<TranslationBatchRequest>(0);
                if (req.TargetLanguageCode == "fr")
                    return new TranslationBatchResult
                    {
                        IsSuccess = false,
                        Translations = [],
                        TokenUsage = new TokenUsage(0, 0, 0),
                        ErrorMessage = "provider down"
                    };
                return new TranslationBatchResult
                {
                    IsSuccess = true,
                    Translations = req.Items.Select(i => new TranslatedUnit { SourceHash = i.SourceHash, TranslatedName = "ok" }).ToList(),
                    TokenUsage = new TokenUsage(10, 10, 0),
                    ProviderName = "deepseek",
                    ProviderTier = TranslationProviderTier.Primary
                };
            });

        await _sut.UpgradeDegradedEntries(CancellationToken.None);

        // fr failed -> no store; de succeeded -> exactly one store.
        await _cache.Received(1).StoreAsync(
            41L, "B", "en", "de", "ok",
            TranslationProviderTier.Primary, "quality_upgrade", Arg.Any<float>(), Arg.Any<CancellationToken>());
        await _cache.DidNotReceive().StoreAsync(
            40L, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<TranslationProviderTier>(), Arg.Any<string>(),
            Arg.Any<float>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GroupTranslationThrows_IsIsolated_OtherGroupsStillProcessed()
    {
        await Seed(50L, "A", "en", "fr", TranslationProviderTier.Secondary);
        await Seed(51L, "B", "en", "de", TranslationProviderTier.Secondary);

        _router.TranslateAsync(Arg.Any<TranslationBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                TranslationBatchRequest req = callInfo.ArgAt<TranslationBatchRequest>(0);
                if (req.TargetLanguageCode == "fr")
                    throw new HttpRequestException("boom");
                return new TranslationBatchResult
                {
                    IsSuccess = true,
                    Translations = req.Items.Select(i => new TranslatedUnit { SourceHash = i.SourceHash, TranslatedName = "ok" }).ToList(),
                    TokenUsage = new TokenUsage(10, 10, 0),
                    ProviderName = "deepseek",
                    ProviderTier = TranslationProviderTier.Primary
                };
            });

        Func<Task> act = () => _sut.UpgradeDegradedEntries(CancellationToken.None);

        await act.Should().NotThrowAsync();
        await _cache.Received(1).StoreAsync(
            51L, "B", "en", "de", "ok",
            TranslationProviderTier.Primary, "quality_upgrade", Arg.Any<float>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TranslationForUnknownHash_IsSkipped()
    {
        await Seed(60L, "A", "en", "fr", TranslationProviderTier.Secondary);

        _router.TranslateAsync(Arg.Any<TranslationBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TranslationBatchResult
            {
                IsSuccess = true,
                Translations = [new TranslatedUnit { SourceHash = 99999L, TranslatedName = "ghost" }],
                TokenUsage = new TokenUsage(10, 10, 0),
                ProviderName = "deepseek",
                ProviderTier = TranslationProviderTier.Primary
            });

        await _sut.UpgradeDegradedEntries(CancellationToken.None);

        await _cache.DidNotReceive().StoreAsync(
            Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<TranslationProviderTier>(), Arg.Any<string>(),
            Arg.Any<float>(), Arg.Any<CancellationToken>());
    }
}
