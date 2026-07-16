using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Neaslator.Domain.Entities;
using Neaslator.Domain.Enums;
using Neaslator.Features.TranslateMenu;
using Neaslator.Infrastructure.Cache;
using Neaslator.Infrastructure.Diff;
using Neaslator.Infrastructure.Providers;
using Neaslator.Persistence;
using NSubstitute;
using Polly;
using StackExchange.Redis;

namespace Neaslator.Tests.Pipeline;

public sealed class TranslationPipelineTests : IDisposable
{
    private readonly NeaslatorDbContext _db;
    private readonly ITranslationProvider _mockProvider;
    private readonly IDatabase _mockRedisDb;
    private readonly TranslationPipeline _pipeline;

    public TranslationPipelineTests()
    {
        DbContextOptions<NeaslatorDbContext> options = new DbContextOptionsBuilder<NeaslatorDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new NeaslatorDbContext(options);

        IConnectionMultiplexer mockMultiplexer = Substitute.For<IConnectionMultiplexer>();
        _mockRedisDb = Substitute.For<IDatabase>();
        mockMultiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(_mockRedisDb);
        _mockRedisDb.StringGetAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>())
            .Returns(callInfo =>
            {
                RedisKey[] keys = callInfo.ArgAt<RedisKey[]>(0);
                return new RedisValue[keys.Length];
            });
        _mockRedisDb.StringSetAsync(Arg.Any<KeyValuePair<RedisKey, RedisValue>[]>(), Arg.Any<When>(), Arg.Any<CommandFlags>())
            .Returns(true);
        _mockRedisDb.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<bool>(), Arg.Any<When>(), Arg.Any<CommandFlags>())
            .Returns(true);

        TranslationCache cache = new(mockMultiplexer, _db);

        _mockProvider = Substitute.For<ITranslationProvider>();
        _mockProvider.ProviderName.Returns("test-provider");
        _mockProvider.Tier.Returns(TranslationProviderTier.Primary);
        _mockProvider.MaxBatchSize.Returns(20);

        ProviderRegistration[] registrations =
        [
            new() { Provider = _mockProvider, Pipeline = ResiliencePipeline.Empty }
        ];
        TranslationRouter router = new(registrations, Substitute.For<ILogger<TranslationRouter>>());

        IServiceScope scope = Substitute.For<IServiceScope>();
        IServiceProvider sp = Substitute.For<IServiceProvider>();
        sp.GetService(typeof(ITranslationCache)).Returns(cache);
        scope.ServiceProvider.Returns(sp);
        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        _pipeline = new TranslationPipeline(_db, cache, router, scopeFactory, Substitute.For<ILogger<TranslationPipeline>>());
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task EmptyDiff_ReturnsZeroLanguagesResult()
    {
        MenuSnapshot identical = new()
        {
            Sections =
            [
                new SectionSnapshot
                {
                    Id = Ulid.NewUlid(), Name = "Starters",
                    Items = [new ItemSnapshot { Id = Ulid.NewUlid(), Name = "Soup" }]
                }
            ]
        };

        TranslationPipelineResult result = await _pipeline.ExecuteAsync(
            identical, identical, "en", "Restaurant", "Italian", CancellationToken.None);

        result.TotalLanguages.Should().Be(0);
        result.Results.Should().BeEmpty();
    }

    [Fact]
    public async Task NoSupportedLanguages_ReturnsZeroLanguages()
    {
        MenuSnapshot current = MakeSingleItemSnapshot("Soup");

        TranslationPipelineResult result = await _pipeline.ExecuteAsync(
            current, null, "en", "Restaurant", "Italian", CancellationToken.None);

        result.TotalLanguages.Should().Be(0);
        result.Results.Should().BeEmpty();
    }

    [Fact]
    public async Task AllCacheHits_NoProviderCalls()
    {
        await SeedLanguages("fr", "de");

        string sectionNorm = Neaslator.Infrastructure.Normalization.TextNormalizer.Normalize("Starters".AsSpan());
        long sectionHash = Neaslator.Infrastructure.Hashing.TranslationHasher.ComputeHash(sectionNorm.AsSpan());
        string itemNorm = Neaslator.Infrastructure.Normalization.TextNormalizer.Normalize("Soup".AsSpan());
        long itemHash = Neaslator.Infrastructure.Hashing.TranslationHasher.ComputeHash(itemNorm.AsSpan());

        _mockRedisDb.StringGetAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>())
            .Returns(callInfo =>
            {
                RedisKey[] keys = callInfo.ArgAt<RedisKey[]>(0);
                return keys.Select(k =>
                {
                    string keyStr = (string)k!;
                    string normalizedText = keyStr.Contains(sectionHash.ToString()) ? sectionNorm : itemNorm;
                    return (RedisValue)System.Text.Json.JsonSerializer.Serialize(
                        new CachedTranslation("translated", TranslationProviderTier.Primary, 1.0f, normalizedText));
                }).ToArray();
            });

        MenuSnapshot current = MakeSingleItemSnapshot("Soup");

        TranslationPipelineResult result = await _pipeline.ExecuteAsync(
            current, null, "en", "Restaurant", "Italian", CancellationToken.None);

        result.TotalLanguages.Should().Be(2);
        result.CompletedLanguages.Should().Be(2);
        result.FailedLanguages.Should().Be(0);
        await _mockProvider.DidNotReceive().TranslateBatchAsync(Arg.Any<TranslationBatchRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CacheMiss_ProviderCalledWithCorrectRequest()
    {
        await SeedLanguages("fr");

        _mockProvider.TranslateBatchAsync(Arg.Any<TranslationBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                TranslationBatchRequest req = callInfo.ArgAt<TranslationBatchRequest>(0);
                return new TranslationBatchResult
                {
                    IsSuccess = true,
                    Translations = req.Items.Select(i => new TranslatedUnit
                    {
                        SourceHash = i.SourceHash,
                        TranslatedName = "Soupe"
                    }).ToList(),
                    TokenUsage = new TokenUsage(100, 50, 0),
                    ProviderName = "test-provider",
                    ProviderTier = TranslationProviderTier.Primary
                };
            });

        MenuSnapshot current = MakeSingleItemSnapshot("Soup");

        TranslationPipelineResult result = await _pipeline.ExecuteAsync(
            current, null, "en", "Restaurant", "Italian", CancellationToken.None);

        result.Results.Should().Contain(r => r.TargetLanguageCode == "fr" && r.IsSuccess);
        await _mockProvider.Received().TranslateBatchAsync(
            Arg.Is<TranslationBatchRequest>(r =>
                r.SourceLanguageCode == "en" && r.TargetLanguageCode == "fr"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SourceLanguageExcludedFromTargets()
    {
        _db.SupportedLanguages.AddRange(
            new SupportedLanguage { Code = "en", EnglishName = "English", NativeName = "English", IsActive = true },
            new SupportedLanguage { Code = "fr", EnglishName = "French", NativeName = "Francais", IsActive = true }
        );
        await _db.SaveChangesAsync();

        _mockRedisDb.StringGetAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>())
            .Returns(callInfo =>
            {
                RedisKey[] keys = callInfo.ArgAt<RedisKey[]>(0);
                keys.Should().AllSatisfy(k => ((string)k!).Should().NotContain(":en"));
                return keys.Select(_ =>
                    (RedisValue)System.Text.Json.JsonSerializer.Serialize(
                        new CachedTranslation("translated", TranslationProviderTier.Primary, 1.0f, "Soup"))
                ).ToArray();
            });

        MenuSnapshot current = MakeSingleItemSnapshot("Soup");

        TranslationPipelineResult result = await _pipeline.ExecuteAsync(
            current, null, "en", "Restaurant", "Italian", CancellationToken.None);

        result.TotalLanguages.Should().Be(1);
    }

    [Fact]
    public async Task ProviderFails_LanguageMarkedAsFailed()
    {
        await SeedLanguages("fr");

        _mockProvider.TranslateBatchAsync(Arg.Any<TranslationBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TranslationBatchResult
            {
                IsSuccess = false,
                Translations = [],
                TokenUsage = new TokenUsage(0, 0, 0),
                ErrorMessage = "Provider unavailable"
            });

        MenuSnapshot current = MakeSingleItemSnapshot("Soup");

        TranslationPipelineResult result = await _pipeline.ExecuteAsync(
            current, null, "en", "Restaurant", "Italian", CancellationToken.None);

        result.FailedLanguages.Should().BeGreaterThan(0);
        result.Results.Should().Contain(r => r.TargetLanguageCode == "fr" && !r.IsSuccess);
    }

    [Fact]
    public async Task ProviderThrowsException_LanguageMarkedAsFailed()
    {
        await SeedLanguages("fr");

        _mockProvider.TranslateBatchAsync(Arg.Any<TranslationBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns<TranslationBatchResult>(_ => throw new HttpRequestException("Network error"));

        MenuSnapshot current = MakeSingleItemSnapshot("Soup");

        TranslationPipelineResult result = await _pipeline.ExecuteAsync(
            current, null, "en", "Restaurant", "Italian", CancellationToken.None);

        result.FailedLanguages.Should().BeGreaterThan(0);
        result.Results.Should().Contain(r => !r.IsSuccess);
    }

    [Fact]
    public async Task InactiveLanguage_Excluded()
    {
        _db.SupportedLanguages.AddRange(
            new SupportedLanguage { Code = "fr", EnglishName = "French", NativeName = "Francais", IsActive = true },
            new SupportedLanguage { Code = "de", EnglishName = "German", NativeName = "Deutsch", IsActive = false }
        );
        await _db.SaveChangesAsync();

        _mockRedisDb.StringGetAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>())
            .Returns(callInfo =>
            {
                RedisKey[] keys = callInfo.ArgAt<RedisKey[]>(0);
                keys.Should().AllSatisfy(k => ((string)k!).Should().NotContain(":de"));
                return keys.Select(_ =>
                    (RedisValue)System.Text.Json.JsonSerializer.Serialize(
                        new CachedTranslation("translated", TranslationProviderTier.Primary, 1.0f, "Soup"))
                ).ToArray();
            });

        MenuSnapshot current = MakeSingleItemSnapshot("Soup");

        TranslationPipelineResult result = await _pipeline.ExecuteAsync(
            current, null, "en", "Restaurant", "Italian", CancellationToken.None);

        result.TotalLanguages.Should().Be(1);
    }

    [Fact]
    public async Task MultipleLanguages_FanOutCorrectly()
    {
        await SeedLanguages("fr", "de", "es");

        _mockProvider.TranslateBatchAsync(Arg.Any<TranslationBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                TranslationBatchRequest req = callInfo.ArgAt<TranslationBatchRequest>(0);
                return new TranslationBatchResult
                {
                    IsSuccess = true,
                    Translations = req.Items.Select(i => new TranslatedUnit
                    {
                        SourceHash = i.SourceHash,
                        TranslatedName = $"translated-{req.TargetLanguageCode}"
                    }).ToList(),
                    TokenUsage = new TokenUsage(100, 50, 0),
                    ProviderName = "test-provider",
                    ProviderTier = TranslationProviderTier.Primary
                };
            });

        MenuSnapshot current = MakeSingleItemSnapshot("Soup");

        TranslationPipelineResult result = await _pipeline.ExecuteAsync(
            current, null, "en", "Restaurant", "Italian", CancellationToken.None);

        result.TotalLanguages.Should().Be(3);
        result.Results.Should().HaveCount(3);
        result.Results.Should().OnlyContain(r => r.IsSuccess);
    }

    private static MenuSnapshot MakeSingleItemSnapshot(string itemName)
    {
        return new MenuSnapshot
        {
            Sections =
            [
                new SectionSnapshot
                {
                    Id = Ulid.NewUlid(), Name = "Starters",
                    Items = [new ItemSnapshot { Id = Ulid.NewUlid(), Name = itemName }]
                }
            ]
        };
    }

    private async Task SeedLanguages(params string[] codes)
    {
        foreach (string code in codes)
        {
            _db.SupportedLanguages.Add(new SupportedLanguage
            {
                Code = code, EnglishName = code, NativeName = code, IsActive = true
            });
        }
        await _db.SaveChangesAsync();
    }
}
