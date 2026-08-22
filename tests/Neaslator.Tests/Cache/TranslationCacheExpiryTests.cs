using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Neaslator.Domain.Enums;
using Neaslator.Infrastructure.Cache;
using Neaslator.Persistence;
using Neaslator.Tests.Shared;
using NSubstitute;
using StackExchange.Redis;

namespace Neaslator.Tests.Cache;

/// <summary>
/// Every L1 write carries an expiry.
/// </summary>
/// <remarks>
/// <para>
/// L1 entries were written with no expiry at all. Keys are content-addressed
/// (<c>sourceHash:targetLanguage</c>) so nothing went stale — but nothing was ever released
/// either, and Garnet accumulated every translation this service had ever looked up. The estate
/// is heading for roughly seventy-five languages across every string on every menu.
/// </para>
/// <para>
/// The blast radius is not confined to translation. Garnet is shared with identity, menu, media,
/// subscription and messaging, so under memory pressure an unbounded translation cache evicts the
/// permission and entitlement entries those services depend on — and the symptom appears somewhere
/// with no visible connection to this service.
/// </para>
/// <para>
/// Bounding it is safe because L1 is only an accelerator over <c>TranslationMemory</c> in Postgres,
/// which the lookup already falls back to. An expired key costs one indexed read.
/// </para>
/// </remarks>
public sealed class TranslationCacheExpiryTests : UnitTestBase, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDatabase _redisDb;
    private readonly IBatch _batch;
    private readonly NeaslatorDbContext _db;
    private readonly TranslationCache _sut;

    public TranslationCacheExpiryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var garnet = Substitute.For<IConnectionMultiplexer>();
        _redisDb = Substitute.For<IDatabase>();
        _batch = Substitute.For<IBatch>();
        _redisDb.CreateBatch(Arg.Any<object>()).Returns(_batch);
        garnet.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(_redisDb);

        _db = new NeaslatorDbContext(new DbContextOptionsBuilder<NeaslatorDbContext>()
            .UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        _sut = new TranslationCache(garnet, _db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    /// <summary>
    /// The expiry each recorded write carried, as the driver rendered it — e.g. <c>"EX 2592000"</c>.
    /// </summary>
    /// <remarks>
    /// Found by argument TYPE, not position. StackExchange.Redis wraps the expiry in an
    /// <c>Expiration</c> struct and has several StringSetAsync overloads, so an index-based
    /// assertion tests which overload the compiler picked rather than whether a TTL was set.
    /// A write with no expiry records an empty <c>Expiration</c>, which is exactly the defect.
    /// </remarks>
    private static List<string> ExpiriesOf(IEnumerable<NSubstitute.Core.ICall> calls, string methodName) =>
        calls
            .Where(c => c.GetMethodInfo().Name == methodName)
            .Select(c => c.GetArguments()
                .FirstOrDefault(a => a?.GetType().Name == "Expiration")?.ToString() ?? string.Empty)
            .ToList();

    [Fact]
    public async Task Storing_a_translation_sets_an_expiry()
    {
        await _sut.StoreAsync(4242L, "Karışık Pizza", "tr", "de", "Gemischte Pizza",
            TranslationProviderTier.Primary, "deepseek", 0.98f, CancellationToken.None);

        var expiries = ExpiriesOf(_redisDb.ReceivedCalls(), nameof(IDatabase.StringSetAsync));

        expiries.Should().NotBeEmpty("the L1 write must reach Garnet");
        expiries.Should().OnlyContain(e => e.StartsWith("EX ", StringComparison.Ordinal),
            "a key written without an expiry is never released, and Garnet is shared with five other services");
    }

    [Fact]
    public async Task The_expiry_is_long_enough_to_be_worth_caching()
    {
        // The TTL bounds MEMORY, not freshness — content-addressed keys never go stale. Too short
        // and every lookup pays a Postgres read; the cache stops being one.
        await _sut.StoreAsync(4243L, "Su", "tr", "en", "Water",
            TranslationProviderTier.Primary, "deepseek", 0.99f, CancellationToken.None);

        var expiry = ExpiriesOf(_redisDb.ReceivedCalls(), nameof(IDatabase.StringSetAsync))
            .First(e => e.StartsWith("EX ", StringComparison.Ordinal));

        int seconds = int.Parse(expiry["EX ".Length..], System.Globalization.CultureInfo.InvariantCulture);
        TimeSpan.FromSeconds(seconds).Should().BeGreaterThan(TimeSpan.FromDays(1));
    }

    [Fact]
    public async Task The_L2_backfill_also_sets_an_expiry()
    {
        // The backfill path used the multi-key StringSetAsync overload, which maps to MSET — and
        // MSET cannot carry an expiry, so every key it wrote lived forever. This is the path that
        // runs most often, because it fires on every L1 miss that L2 can answer.
        _db.TranslationMemory.Add(new Domain.Entities.TranslationMemoryEntry
        {
            SourceHash = 5150L,
            NormalizedSourceText = "Ayran",
            SourceLanguageCode = "tr",
            TargetLanguageCode = "en",
            TranslatedText = "Ayran",
            ProviderTier = TranslationProviderTier.Primary,
            ProviderName = "deepseek",
            ConfidenceScore = 0.97f,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync();

        _redisDb.StringGetAsync(Arg.Any<RedisKey[]>()).Returns([RedisValue.Null]);

        await _sut.LookupAsync(5150L, "Ayran", "tr", ["en"], CancellationToken.None);

        var batchExpiries = ExpiriesOf(_batch.ReceivedCalls(), nameof(IBatch.StringSetAsync));

        batchExpiries.Should().NotBeEmpty("an L2 hit must backfill L1");
        batchExpiries.Should().OnlyContain(e => e.StartsWith("EX ", StringComparison.Ordinal));
    }
}
