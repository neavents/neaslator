using FluentAssertions;
using Neaslator.Infrastructure.Cache;
using Neaslator.Tests.Shared;
using NSubstitute;
using StackExchange.Redis;

namespace Neaslator.Tests.Cache;

public sealed class DistributedTranslationLockTests : UnitTestBase
{
    private readonly IConnectionMultiplexer _garnet;
    private readonly IDatabase _redisDb;
    private readonly DistributedTranslationLock _sut;

    public DistributedTranslationLockTests()
    {
        _garnet = Substitute.For<IConnectionMultiplexer>();
        _redisDb = Substitute.For<IDatabase>();
        _garnet.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(_redisDb);
        _sut = new DistributedTranslationLock(_garnet);
    }

    [Fact]
    public async Task TryAcquireAsync_FirstAttempt_ReturnsAcquired()
    {
        _redisDb.StringSetAsync(
            Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan>(), When.NotExists)
            .Returns(true);

        var result = await _sut.TryAcquireAsync(12345L, "tr", CancellationToken.None);

        result.Outcome.Should().Be(LockOutcome.Acquired);
        result.LockKey.Should().NotBeNull();
        result.LockValue.Should().NotBeNull();
        result.LockKey!.Should().Contain("12345:tr");
    }

    [Fact]
    public async Task TryAcquireAsync_LockContended_PeerResolves_ReturnsResolvedByPeer()
    {
        // First attempt fails
        _redisDb.StringSetAsync(
            Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan>(), When.NotExists)
            .Returns(false);

        // Poll succeeds on first try
        _redisDb.StringGetAsync(Arg.Is<RedisKey>(k => k.ToString().Contains("neaslator:t:12345:tr")))
            .Returns("{\"TranslatedText\":\"merhaba\",\"ProviderTier\":0,\"ConfidenceScore\":0.99,\"NormalizedSourceText\":\"hello\"}");

        var result = await _sut.TryAcquireAsync(12345L, "tr", CancellationToken.None);

        result.Outcome.Should().Be(LockOutcome.ResolvedByPeer);
        result.CachedValue.Should().NotBeNull();
    }

    [Fact]
    public async Task TryAcquireAsync_LockContended_Timeout_ReturnsForcedAcquisition()
    {
        // First attempt fails
        _redisDb.StringSetAsync(
            Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan>(), When.NotExists)
            .Returns(false);

        // Poll never finds cached value
        _redisDb.StringGetAsync(Arg.Any<RedisKey>()).Returns(RedisValue.Null);

        // Forced acquisition succeeds
        _redisDb.StringSetAsync(
            Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan>(), When.Always)
            .Returns(true);

        var result = await _sut.TryAcquireAsync(12345L, "tr", CancellationToken.None);

        result.Outcome.Should().Be(LockOutcome.ForcedAcquisition);
        result.LockKey.Should().NotBeNull();
    }

    [Fact]
    public async Task TryAcquireAsync_WithCancellation_ThrowsOperationCancelledException()
    {
        _redisDb.StringSetAsync(
            Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan>(), When.NotExists)
            .Returns(false);

        _redisDb.StringGetAsync(Arg.Any<RedisKey>()).Returns(RedisValue.Null);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => _sut.TryAcquireAsync(12345L, "tr", cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ReleaseAsync_ValidLock_ExecutesScript()
    {
        _redisDb.ScriptEvaluateAsync(
            Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>())
            .Returns(RedisResult.Create(1));

        await _sut.ReleaseAsync("lock:key", "lock-value");

        await _redisDb.Received(1).ScriptEvaluateAsync(
            Arg.Any<string>(),
            Arg.Is<RedisKey[]>(k => k[0].ToString() == "lock:key"),
            Arg.Is<RedisValue[]>(v => v[0].ToString() == "lock-value"));
    }

    [Fact]
    public async Task ReleaseAsync_StolenLock_DoesNotThrow()
    {
        _redisDb.ScriptEvaluateAsync(
            Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>())
            .Returns(RedisResult.Create(0));

        var act = () => _sut.ReleaseAsync("lock:key", "stolen-value");
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void LockResult_Acquired_HasCorrectProperties()
    {
        var result = LockResult.Acquired("key1", "val1");
        result.Outcome.Should().Be(LockOutcome.Acquired);
        result.LockKey.Should().Be("key1");
        result.LockValue.Should().Be("val1");
        result.CachedValue.Should().BeNull();
    }

    [Fact]
    public void LockResult_ResolvedByPeer_HasCorrectProperties()
    {
        var result = LockResult.ResolvedByPeer("cached-data");
        result.Outcome.Should().Be(LockOutcome.ResolvedByPeer);
        result.CachedValue.Should().Be("cached-data");
        result.LockKey.Should().BeNull();
    }

    [Fact]
    public void LockResult_ForcedAcquisition_HasCorrectProperties()
    {
        var result = LockResult.ForcedAcquisition("key2", "val2");
        result.Outcome.Should().Be(LockOutcome.ForcedAcquisition);
        result.LockKey.Should().Be("key2");
        result.LockValue.Should().Be("val2");
    }
}
