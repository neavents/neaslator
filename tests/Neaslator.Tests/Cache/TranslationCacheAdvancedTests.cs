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
/// Behavioural coverage the base cache suite leaves as "integration-only": the L2
/// hit-count bump and the exact value written back into L1 on a backfill. Uses a real
/// in-memory SQLite database so the EF <c>ExecuteUpdate</c> path actually runs.
/// </summary>
public sealed class TranslationCacheAdvancedTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDatabase _redisDb;
    private readonly NeaslatorDbContext _db;
    private readonly TranslationCache _sut;

    public TranslationCacheAdvancedTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        IConnectionMultiplexer garnet = Substitute.For<IConnectionMultiplexer>();
        _redisDb = Substitute.For<IDatabase>();
        garnet.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(_redisDb);

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

    private KeyValuePair<RedisKey, RedisValue>[] CapturedBackfill()
    {
        var call = _redisDb.ReceivedCalls().FirstOrDefault(c =>
            c.GetMethodInfo().Name == "StringSetAsync" &&
            c.GetArguments().Length > 0 &&
            c.GetArguments()[0] is KeyValuePair<RedisKey, RedisValue>[]);
        call.Should().NotBeNull("a backfill StringSetAsync(KeyValuePair[]) call should have been made");
        return (KeyValuePair<RedisKey, RedisValue>[])call!.GetArguments()[0]!;
    }

    private NeaslatorDbContext FreshContext()
    {
        DbContextOptions<NeaslatorDbContext> options = new DbContextOptionsBuilder<NeaslatorDbContext>()
            .UseSqlite(_connection)
            .Options;
        return new NeaslatorDbContext(options);
    }

    private async Task<long> SeedEntry(long sourceHash, string normalized, string sourceLang, string targetLang, string translated, long hitCount = 0)
    {
        var entry = new TranslationMemoryEntry
        {
            SourceHash = sourceHash,
            NormalizedSourceText = normalized,
            SourceLanguageCode = sourceLang,
            TargetLanguageCode = targetLang,
            TranslatedText = translated,
            ProviderTier = TranslationProviderTier.Primary,
            ProviderName = "deepseek",
            ConfidenceScore = 0.9f,
            HitCount = hitCount,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        _db.TranslationMemory.Add(entry);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return entry.Id;
    }

    [Fact]
    public async Task L2Hit_IncrementsHitCountInDatabase()
    {
        long id = await SeedEntry(1000L, "Tiramisu", "en", "fr", "Tiramisu", hitCount: 7);
        _redisDb.StringGetAsync(Arg.Any<RedisKey[]>()).Returns(new RedisValue[] { RedisValue.Null });

        await _sut.LookupAsync(1000L, "Tiramisu", "en", new[] { "fr" }, CancellationToken.None);

        await using NeaslatorDbContext verify = FreshContext();
        TranslationMemoryEntry entry = await verify.TranslationMemory.AsNoTracking().FirstAsync(e => e.Id == id);
        entry.HitCount.Should().Be(8);
    }

    [Fact]
    public async Task L1Hit_DoesNotIncrementHitCount()
    {
        long id = await SeedEntry(1001L, "Gelato", "en", "it", "Gelato", hitCount: 3);
        var cached = new CachedTranslation("Gelato", TranslationProviderTier.Primary, 1.0f, "Gelato");
        _redisDb.StringGetAsync(Arg.Any<RedisKey[]>())
            .Returns(new RedisValue[] { JsonSerializer.Serialize(cached) });

        var results = await _sut.LookupAsync(1001L, "Gelato", "en", new[] { "it" }, CancellationToken.None);

        results[0].Source.Should().Be(CacheSource.L1Garnet);
        await using NeaslatorDbContext verify = FreshContext();
        TranslationMemoryEntry entry = await verify.TranslationMemory.AsNoTracking().FirstAsync(e => e.Id == id);
        entry.HitCount.Should().Be(3);
    }

    [Fact]
    public async Task L2Collision_DoesNotIncrementHitCount()
    {
        // Same hash, different source text -> collision, must be skipped, no bump.
        long id = await SeedEntry(1002L, "Sorbet", "en", "it", "Sorbetto", hitCount: 4);
        _redisDb.StringGetAsync(Arg.Any<RedisKey[]>()).Returns(new RedisValue[] { RedisValue.Null });

        var results = await _sut.LookupAsync(1002L, "Gelato", "en", new[] { "it" }, CancellationToken.None);

        results[0].Source.Should().Be(CacheSource.Miss);
        await using NeaslatorDbContext verify = FreshContext();
        TranslationMemoryEntry entry = await verify.TranslationMemory.AsNoTracking().FirstAsync(e => e.Id == id);
        entry.HitCount.Should().Be(4);
    }

    [Fact]
    public async Task L2Hit_BackfillsL1WithSerializedTranslation()
    {
        await SeedEntry(1003L, "Espresso", "en", "it", "Espresso", hitCount: 0);
        _redisDb.StringGetAsync(Arg.Any<RedisKey[]>()).Returns(new RedisValue[] { RedisValue.Null });

        await _sut.LookupAsync(1003L, "Espresso", "en", new[] { "it" }, CancellationToken.None);

        KeyValuePair<RedisKey, RedisValue>[] backfill = CapturedBackfill();
        backfill.Should().ContainSingle();
        backfill[0].Key.ToString().Should().Be("neaslator:t:1003:it");

        CachedTranslation? written = JsonSerializer.Deserialize<CachedTranslation>((string)backfill[0].Value!);
        written.Should().NotBeNull();
        written!.TranslatedText.Should().Be("Espresso");
        written.NormalizedSourceText.Should().Be("Espresso");
        written.ProviderTier.Should().Be(TranslationProviderTier.Primary);
    }

    [Fact]
    public async Task MultipleL2Hits_AllBackfilledAndAllHitCountsIncremented()
    {
        long idFr = await SeedEntry(1004L, "Croissant", "en", "fr", "Croissant", hitCount: 1);
        long idDe = await SeedEntry(1004L, "Croissant", "en", "de", "Hörnchen", hitCount: 2);
        _redisDb.StringGetAsync(Arg.Any<RedisKey[]>())
            .Returns(new RedisValue[] { RedisValue.Null, RedisValue.Null });

        var results = await _sut.LookupAsync(1004L, "Croissant", "en", new[] { "fr", "de" }, CancellationToken.None);

        results.Should().OnlyContain(r => r.Source == CacheSource.L2PostgreSql);
        CapturedBackfill().Should().HaveCount(2);

        await using NeaslatorDbContext verify = FreshContext();
        (await verify.TranslationMemory.AsNoTracking().FirstAsync(e => e.Id == idFr)).HitCount.Should().Be(2);
        (await verify.TranslationMemory.AsNoTracking().FirstAsync(e => e.Id == idDe)).HitCount.Should().Be(3);
    }

    [Fact]
    public async Task PartialL2Hit_OneHitOneMiss_OnlyHitIsIncrementedAndBackfilled()
    {
        long idFr = await SeedEntry(1005L, "Baguette", "en", "fr", "Baguette", hitCount: 0);
        _redisDb.StringGetAsync(Arg.Any<RedisKey[]>())
            .Returns(new RedisValue[] { RedisValue.Null, RedisValue.Null });

        var results = await _sut.LookupAsync(1005L, "Baguette", "en", new[] { "fr", "de" }, CancellationToken.None);

        results.Should().ContainSingle(r => r.TargetLanguageCode == "fr" && r.Source == CacheSource.L2PostgreSql);
        results.Should().ContainSingle(r => r.TargetLanguageCode == "de" && r.Source == CacheSource.Miss);

        KeyValuePair<RedisKey, RedisValue>[] backfill = CapturedBackfill();
        backfill.Should().ContainSingle();
        backfill[0].Key.ToString().Should().Be("neaslator:t:1005:fr");

        await using NeaslatorDbContext verify = FreshContext();
        (await verify.TranslationMemory.AsNoTracking().FirstAsync(e => e.Id == idFr)).HitCount.Should().Be(1);
    }

    [Fact]
    public async Task L2Lookup_IsScopedToSourceLanguage()
    {
        // Entry stored for source "es" must not satisfy a lookup with source "en".
        await SeedEntry(1006L, "Paella", "es", "fr", "Paella", hitCount: 0);
        _redisDb.StringGetAsync(Arg.Any<RedisKey[]>()).Returns(new RedisValue[] { RedisValue.Null });

        var results = await _sut.LookupAsync(1006L, "Paella", "en", new[] { "fr" }, CancellationToken.None);

        results[0].Source.Should().Be(CacheSource.Miss);
    }
}
