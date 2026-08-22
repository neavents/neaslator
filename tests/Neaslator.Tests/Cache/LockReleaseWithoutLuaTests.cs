using FluentAssertions;
using Neaslator.Infrastructure.Cache;
using Neaslator.Tests.Shared;
using NSubstitute;
using StackExchange.Redis;

namespace Neaslator.Tests.Cache;

/// <summary>
/// Releasing a lease on a Garnet built without Lua must still respect who owns it.
/// </summary>
/// <remarks>
/// <para>
/// The normal release is a Lua compare-and-delete: drop the key only if it still holds OUR token.
/// Garnet can be built without Lua and this estate runs Garnet, so there is a fallback — and the
/// fallback used to be an unconditional delete.
/// </para>
/// <para>
/// That is not merely a narrower guarantee, because <c>AcquireAsync</c> has a ForcedAcquisition
/// path. "Another holder owns this key" is a designed state here, not an expiry edge case, so a
/// blind delete hands a second worker the same menu — and translation is the paid path, against a
/// provider that already exhausts partway through a large run.
/// </para>
/// <para>
/// Read, compare, delete on a match. Not atomic — the holder can change between the read and the
/// delete — but microseconds wide instead of always, and the TTL is still the dead-man switch.
/// </para>
/// </remarks>
public sealed class LockReleaseWithoutLuaTests : UnitTestBase
{
    private readonly IDatabase _redisDb = Substitute.For<IDatabase>();
    private readonly DistributedTranslationLock _sut;

    public LockReleaseWithoutLuaTests()
    {
        IConnectionMultiplexer garnet = Substitute.For<IConnectionMultiplexer>();
        garnet.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(_redisDb);

        _sut = new DistributedTranslationLock(garnet);

        // What a Garnet built without Lua answers.
        _redisDb.ScriptEvaluateAsync(
                Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns<Task<RedisResult>>(_ => throw new RedisServerException("ERR Lua scripting is not enabled"));
    }

    [Fact]
    public async Task Our_own_lease_is_released()
    {
        _redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(new RedisValue("mine"));

        await _sut.ReleaseAsync("lock:menu", "mine");

        await _redisDb.Received(1).KeyDeleteAsync(
            Arg.Is<RedisKey>(k => k.ToString() == "lock:menu"), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task Someone_else_s_lease_is_left_alone()
    {
        // The regression. A forced acquisition means this is reachable by design, and deleting
        // here starts a second paid translation of the same menu.
        _redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(new RedisValue("someone-else"));

        await _sut.ReleaseAsync("lock:menu", "mine");

        await _redisDb.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task An_expired_lease_is_not_deleted_again()
    {
        _redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(RedisValue.Null);

        await _sut.ReleaseAsync("lock:menu", "mine");

        await _redisDb.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task Release_never_throws_when_the_script_call_times_out()
    {
        // The narrower guard this replaces caught RedisException — and RedisTimeoutException
        // derives from TimeoutException, not from RedisException, so a timeout escaped the very
        // "never throws" guarantee the handler documents. A timeout talking to Garnet is precisely
        // when a release happens and precisely when it must not throw.
        IDatabase timingOut = Substitute.For<IDatabase>();
        IConnectionMultiplexer garnet = Substitute.For<IConnectionMultiplexer>();
        garnet.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(timingOut);

        timingOut.ScriptEvaluateAsync(
                Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns<Task<RedisResult>>(_ => throw new RedisTimeoutException("gone", CommandStatus.Unknown));

        DistributedTranslationLock sut = new(garnet);

        Func<Task> act = () => sut.ReleaseAsync("lock:menu", "mine");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Release_never_throws_even_when_the_read_fails()
    {
        // Called from a finally. An exception here would replace the caller's real result — a
        // successful translation reported as a failure — and the TTL reclaims the lease anyway.
        _redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns<Task<RedisValue>>(_ => throw new RedisTimeoutException("gone", CommandStatus.Unknown));

        Func<Task> act = () => _sut.ReleaseAsync("lock:menu", "mine");

        await act.Should().NotThrowAsync();
    }
}
