using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Neaslator.Domain.Entities;
using Neaslator.Domain.Enums;
using Neaslator.Infrastructure.Cache;
using Neaslator.Persistence;
using NSubstitute;
using StackExchange.Redis;

namespace Neaslator.Tests.Cache;

/// <summary>
/// Data-integrity guarantees for the two-tier cache when the L1 (Garnet) tier returns garbage.
/// A single poisoned/corrupted L1 value must never break a lookup — Postgres (L2) is the
/// source of truth, so a bad L1 entry must degrade to an L1 miss and fall through, not throw.
/// </summary>
public sealed class TranslationCacheResilienceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDatabase _redis;
    private readonly NeaslatorDbContext _db;
    private readonly TranslationCache _sut;

    public TranslationCacheResilienceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        IConnectionMultiplexer garnet = Substitute.For<IConnectionMultiplexer>();
        _redis = Substitute.For<IDatabase>();
        garnet.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(_redis);

        DbContextOptions<NeaslatorDbContext> options = new DbContextOptionsBuilder<NeaslatorDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new NeaslatorDbContext(options);
        _db.Database.EnsureCreated();

        _sut = new TranslationCache(garnet, _db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task SeedL2(long hash, string normalized, string sourceLang, string targetLang, string translated)
    {
        _db.TranslationMemory.Add(new TranslationMemoryEntry
        {
            SourceHash = hash,
            NormalizedSourceText = normalized,
            SourceLanguageCode = sourceLang,
            TargetLanguageCode = targetLang,
            TranslatedText = translated,
            ProviderTier = TranslationProviderTier.Primary,
            ProviderName = "deepseek",
            ConfidenceScore = 1f,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    [Theory]
    [InlineData("{ this is not valid json")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]            // valid json, wrong shape
    [InlineData("\"a bare string\"")]  // valid json, wrong shape
    public async Task CorruptedL1Value_FallsThroughToL2_NeverThrows(string garbage)
    {
        const long hash = 700L;
        await SeedL2(hash, "Espresso", "en", "it", "Espresso");
        _redis.StringGetAsync(Arg.Any<RedisKey[]>()).Returns(new RedisValue[] { garbage });

        IReadOnlyList<CacheLookupResult> results = await _sut.LookupAsync(
            hash, "Espresso", "en", new[] { "it" }, CancellationToken.None);

        results.Should().ContainSingle();
        results[0].Source.Should().Be(CacheSource.L2PostgreSql,
            "a corrupt L1 value must degrade to a miss and be served by the authoritative L2 tier");
        results[0].Translation!.TranslatedText.Should().Be("Espresso");
    }

    [Fact]
    public async Task CorruptedL1Value_NoL2Entry_ReturnsMiss_NotThrow()
    {
        const long hash = 701L;
        _redis.StringGetAsync(Arg.Any<RedisKey[]>()).Returns(new RedisValue[] { "totally broken" });

        IReadOnlyList<CacheLookupResult> results = await _sut.LookupAsync(
            hash, "Ristretto", "en", new[] { "it" }, CancellationToken.None);

        results.Should().ContainSingle();
        results[0].Source.Should().Be(CacheSource.Miss);
        results[0].Translation.Should().BeNull();
    }

    [Fact]
    public async Task OneCorruptOneValidL1_AcrossLanguages_EachHandledIndependently()
    {
        const long hash = 702L;
        var validIt = new CachedTranslation("Gelato", TranslationProviderTier.Primary, 1f, "Gelato");
        await SeedL2(hash, "Gelato", "en", "fr", "Glace");

        _redis.StringGetAsync(Arg.Any<RedisKey[]>()).Returns(new RedisValue[]
        {
            JsonSerializer.Serialize(validIt),   // it -> valid L1 hit
            "corrupt-value"                      // fr -> poisoned, must fall through to L2
        });

        IReadOnlyList<CacheLookupResult> results = await _sut.LookupAsync(
            hash, "Gelato", "en", new[] { "it", "fr" }, CancellationToken.None);

        results.Should().ContainSingle(r => r.TargetLanguageCode == "it" && r.Source == CacheSource.L1Garnet);
        results.Should().ContainSingle(r => r.TargetLanguageCode == "fr" && r.Source == CacheSource.L2PostgreSql
                                            && r.Translation!.TranslatedText == "Glace");
    }
}
