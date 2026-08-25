namespace Neaslator.Persistence;

using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;

/// <summary>
/// Reads served by a PostgreSQL standby instead of the primary, for the queries that can tolerate
/// being a few milliseconds behind.
/// </summary>
/// <remarks>
/// <para>
/// <b>The mechanism is the easy half.</b> A second connection string is three lines. The part that
/// decides whether this is an improvement or an outage is knowing which reads may go here, and that
/// question has one wrong answer that looks right:
/// </para>
/// <para>
/// <b>A cache in front of a read does NOT make it safe to serve from a replica.</b> The opposite,
/// usually. A cache that is <i>invalidated</i> on write exists precisely so the read immediately
/// after a change is fresh. Route that read to a standby and the invalidation still fires, the
/// cache still misses, and the miss is then filled from a server that has not yet applied the
/// write. The cache made the stale read MORE likely, not less, because it removed the only copy
/// that was correct.
/// </para>
/// <para>
/// So the rule is about the read's contract, not its volume:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Safe:</b> a read whose caller has not just written, and whose answer nobody is waiting to see
/// change — list and search endpoints, admin views, exports, and the sweeps, which re-read on their
/// own schedule and repair anything they missed.
/// </description></item>
/// <item><description>
/// <b>Never:</b> anything in the request path that follows a write by the same caller; anything
/// inside a transaction; anything a cache invalidation exists to make fresh; the outbox; migrations;
/// advisory locks. In this service that rules out the two paths that matter most. The translation
/// memory is written and then read back within a run, so a standby read would re-translate text it
/// had just paid a provider to translate. And <c>MenuPublishSnapshots</c> is written by the publish
/// consumer and read by the work that follows it — a lagging read there processes the previous
/// version of a menu and reports success.
/// </description></item>
/// </list>
/// <para>
/// <b>Why it is worth having at all,</b> given the primary is nowhere near its limits: during a
/// failover the primary is gone for about twenty seconds while a standby is promoted. Reads pointed
/// at <c>-ro</c> keep being served by whichever replicas remain, so the estate degrades to
/// read-only instead of going dark. That is an availability argument, not a throughput one, and it
/// is the reason to wire it before the load arrives rather than after.
/// </para>
/// <para>
/// <b>What is actually routed here</b> is the pair that has neither problem: the supported-language
/// list, which changes when somebody adds a language and is read on essentially every request, and
/// the translation-memory statistics, which are aggregate counts nobody is waiting to see move.
/// The translation status and retry endpoints are deliberately left on the primary — both are read
/// straight after the write whose outcome they report.
/// </para>
/// <para>
/// <b>Unset falls back to the primary,</b> which is correct wherever there are no replicas — the
/// compose estate has one PostgreSQL, and a read that lands on it is simply a read. The fallback is
/// never silently wrong; it is only ever less fast.
/// </para>
/// </remarks>
public sealed class ReadReplica
{
    public const string ConnectionStringKey = "ConnectionStrings:PostgresRead";

    /// <summary>
    /// Past this, reads go to the primary. <b>100ms — measured, not chosen for comfort.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// This was one second. Across 450 trials — insert, commit, then poll a standby until the row
    /// appears — the estate replicates at a median of <b>0.17ms</b>, p99 under 0.75ms, worst
    /// observed 1.06ms. A one-second gate therefore allowed a staleness window some three thousand
    /// times worse than anything ever seen here, which makes it not a guard: any lag bad enough to
    /// matter would have passed it.
    /// </para>
    /// <para>
    /// 100ms is still ~140x the measured p99, so ordinary jitter cannot trip it, while a standby
    /// that has genuinely fallen behind leaves rotation long before anyone notices by hand.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMilliseconds(100);

    private readonly DbContextOptions<NeaslatorDbContext> _options;
    private readonly DbContextOptions<NeaslatorDbContext> _primaryOptions;
    private readonly string _connectionString;

    /// <summary>
    /// The last lag reading, refreshed on a timer rather than measured per request. Volatile
    /// because it is written by the sampler and read by every request thread; a torn read of a
    /// double would be a real hazard, so it is stored as ticks in a long.
    /// </summary>
    private long _lastLagTicks = -1;

    /// <summary>When the lag was last sampled, so the sampling is self-driven.</summary>
    private long _lastSampleAtTicks;
    private int _sampling;

    /// <summary>
    /// How stale a lag reading may be before another is taken. Never in the path of a read: the
    /// sample runs in the background and the current read uses the previous value.
    /// </summary>
    private static readonly TimeSpan SampleEvery = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How old a lag READING may be before it stops counting as evidence.
    /// </summary>
    /// <remarks>
    /// <see cref="StaleAfter"/> bounds how far behind the standby was AT THE LAST SAMPLE. It says
    /// nothing about now. Samples are taken every <see cref="SampleEvery"/> by a fire-and-forget
    /// task, off the request path — and a task that stops (a swallowed exception, a wedged
    /// connection, a sampler that never reschedules) leaves the last good reading in place
    /// indefinitely. Reads would then keep choosing the replica on the strength of a measurement
    /// taken minutes ago, which IS the unknown-lag case this guard exists to refuse.
    /// <para>
    /// Three sample intervals: long enough that one slow or missed sample does not flap the estate
    /// onto the primary, short enough that a sampler which has genuinely stopped is caught in
    /// about a minute and a half.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan ReadingExpiresAfter = TimeSpan.FromTicks(SampleEvery.Ticks * 3);

    private ReadReplica(
        DbContextOptions<NeaslatorDbContext> options,
        DbContextOptions<NeaslatorDbContext> primaryOptions,
        string connectionString,
        bool isSeparate)
    {
        _options = options;
        _primaryOptions = primaryOptions;
        _connectionString = connectionString;
        IsSeparateFromPrimary = isSeparate;
    }

    /// <summary>
    /// Records how far behind the replica was when last sampled, so <see cref="Open"/> can route
    /// around a replica that has fallen behind without paying a round trip per request.
    /// </summary>
    /// <remarks>
    /// Null means "could not measure" — an unreachable replica, or one that turned out not to be a
    /// standby. Both route to the primary, because an unmeasurable replica is exactly the one that
    /// should not be trusted with a read.
    /// </remarks>
    public void RecordLag(double? milliseconds) =>
        Interlocked.Exchange(
            ref _lastLagTicks,
            milliseconds is { } ms && ms >= 0 ? (long)(ms * TimeSpan.TicksPerMillisecond) : -1);

    /// <summary>
    /// False when <c>PostgresRead</c> is unset or names the same host as the primary, so reads are
    /// really being served by the primary. Not a fault — but the difference matters to anything
    /// reporting on replication, which would otherwise measure a standby that is not there.
    /// </summary>
    public bool IsSeparateFromPrimary { get; }

    public static ReadReplica Create(string? primary, string? read)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(primary);

        var effective = string.IsNullOrWhiteSpace(read) ? primary : read;

        // Compare hosts rather than whole strings: the read string legitimately differs in pool size
        // and application name, and comparing verbatim would report "separate" for a string that
        // points at the same server.
        var separate = !string.Equals(
            new NpgsqlConnectionStringBuilder(effective).Host,
            new NpgsqlConnectionStringBuilder(primary).Host,
            StringComparison.OrdinalIgnoreCase);

        static DbContextOptions<NeaslatorDbContext> Build(string connectionString) =>
            new DbContextOptionsBuilder<NeaslatorDbContext>()
                // No MigrationsHistoryTable override: neaslator owns a whole DATABASE rather than a
                // schema inside the shared one, so the default public.__EFMigrationsHistory is
                // correct. Carrying identity's "identity" schema across would have pointed this at
                // a table that does not exist in this database.
                .UseNpgsql(connectionString)
                // Nothing read here is ever saved, and tracking a graph nobody will mutate costs
                // memory and time on exactly the large list queries this exists for.
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
                .Options;

        return new ReadReplica(Build(effective), Build(primary), effective, separate);
    }

    /// <summary>
    /// A read-only context: the replica when it is keeping up, the primary when it is not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The fallback is what makes moving a read here a safe decision rather than a bet.</b>
    /// Every judgement about whether a query tolerates a replica rests on the lag being about a
    /// millisecond — which it is, until something breaks. A replica that falls behind, or one that
    /// cannot be reached at all, then quietly turns a sound decision into stale answers with no
    /// error anywhere. Routing back to the primary costs a little throughput in exactly the
    /// situation where throughput is not the problem.
    /// </para>
    /// <para>
    /// The reading comes from a timer, not from this call. Measuring per request would put a round
    /// trip in front of every query to save a round trip, and a lag that changes between two
    /// samples thirty seconds apart is not a lag anyone is racing.
    /// </para>
    /// <para>
    /// Disposed with the query, deliberately not scoped to the request, so nothing that intends to
    /// write can pick it up by accident.
    /// </para>
    /// </remarks>
    public NeaslatorDbContext Open()
    {
        if (!IsSeparateFromPrimary)
        {
            return new NeaslatorDbContext(_primaryOptions);
        }

        MaybeSample();

        var ticks = Interlocked.Read(ref _lastLagTicks);

        // -1 is "not measured yet or unmeasurable". The first requests after a pod starts therefore
        // go to the primary until the first sample lands, which is the right way round: an
        // unmeasured replica is indistinguishable from a broken one.
        return ticks >= 0 && ticks <= StaleAfter.Ticks
            && DateTimeOffset.UtcNow.UtcTicks - Interlocked.Read(ref _lastSampleAtTicks) <= ReadingExpiresAfter.Ticks
            ? new NeaslatorDbContext(_options)
            : new NeaslatorDbContext(_primaryOptions);
    }

    /// <summary>Where <see cref="Open"/> would send a query right now, for logging and tests.</summary>
    public bool WouldUseReplica()
    {
        if (!IsSeparateFromPrimary) return false;
        var ticks = Interlocked.Read(ref _lastLagTicks);
        return ticks >= 0 && ticks <= StaleAfter.Ticks
            && DateTimeOffset.UtcNow.UtcTicks - Interlocked.Read(ref _lastSampleAtTicks) <= ReadingExpiresAfter.Ticks;
    }

    /// <summary>
    /// Refreshes the lag reading in the background when it has gone stale.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The seam feeds itself, and it did not always.</b> The reading used to arrive only from
    /// <c>ReplicaLagHealthCheck</c>, driven by a hosted service — so a service that copied this
    /// class without also copying the sampler had <c>_lastLagTicks</c> stuck at -1 forever and sent
    /// every read to the primary. That is exactly what happened to subscription-service: the seam
    /// was wired, the reads were moved, the startup log said "reads go to standby", and the
    /// measured traffic to the replicas was ZERO.
    /// </para>
    /// <para>
    /// It failed SAFE, which is why nothing broke and why it would have gone unnoticed
    /// indefinitely. A component whose correct operation depends on someone remembering to wire a
    /// second component will eventually be wired without it.
    /// </para>
    /// <para>
    /// Fire-and-forget, at most one in flight, and never awaited by the caller: the read in
    /// progress uses the previous value, and a lag that changed between two samples thirty seconds
    /// apart is not one anybody is racing. A health check may still call
    /// <see cref="RecordLag"/> on top of this — the two are idempotent.
    /// </para>
    /// </remarks>
    private void MaybeSample()
    {
        var now = DateTimeOffset.UtcNow.UtcTicks;

        if (now - Interlocked.Read(ref _lastSampleAtTicks) <= SampleEvery.Ticks)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _sampling, 1, 0) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _lastSampleAtTicks, now);

        _ = Task.Run(async () =>
        {
            try
            {
                RecordLag(await ReplayLagMillisecondsAsync().ConfigureAwait(false));
            }
            catch (Exception)
            {
                // Unmeasurable is unusable: RecordLag(null) sends reads to the primary until a
                // sample succeeds.
                RecordLag(null);
            }
            finally
            {
                Interlocked.Exchange(ref _sampling, 0);
            }
        });
    }

    /// <summary>
    /// How far behind the primary this replica is, in milliseconds, or null when it cannot be
    /// measured — including when the connection turns out to be the primary itself.
    /// </summary>
    /// <remarks>
    /// <c>pg_last_xact_replay_timestamp()</c> is the last transaction the standby has APPLIED, which
    /// is what a reader actually sees; <c>pg_stat_replication</c> on the primary reports what it has
    /// SENT, which can be ahead of what any reader can observe. Asking the standby is the honest
    /// measurement, and it is also the only one available if the primary is unreachable — which is
    /// precisely when someone wants to know whether reads are still trustworthy.
    /// </remarks>
    public async Task<double?> ReplayLagMillisecondsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CASE
                WHEN NOT pg_is_in_recovery() THEN NULL
                WHEN pg_last_wal_receive_lsn() = pg_last_wal_replay_lsn() THEN 0
                ELSE EXTRACT(EPOCH FROM (now() - pg_last_xact_replay_timestamp())) * 1000
            END
            """;

        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? null : Convert.ToDouble(value);
    }

    /// <summary>
    /// Logs what the read connection actually reaches, once, at startup.
    /// </summary>
    /// <remarks>
    /// It does not refuse to start. A read string pointing at the primary is the correct
    /// configuration on any deployment without replicas, and the estate has one of those. What is
    /// worth refusing is silence: "reads go to a replica" is otherwise a belief held from YAML, and
    /// this is the estate that has been caught by that more than once.
    /// </remarks>
    public async Task ReportAsync(ILogger logger, CancellationToken cancellationToken = default)
    {
        using var activity = Observability.NeaslatorActivitySources.Cache.StartActivity("ReadReplicaReport");

        try
        {
            var lag = await ReplayLagMillisecondsAsync(cancellationToken).ConfigureAwait(false);

            // Seed the reading here too, so the FIRST request already knows where to go rather than
            // spending itself on the primary while the first background sample runs.
            RecordLag(lag);
            Interlocked.Exchange(ref _lastSampleAtTicks, DateTimeOffset.UtcNow.UtcTicks);

            var host = new NpgsqlConnectionStringBuilder(_connectionString).Host;

            if (lag is null)
            {
                logger.LogInformation(
                    "Read queries go to {Host}, which is NOT a standby — it is serving them from a primary. "
                    + "Correct where there are no replicas; set {Setting} to a -ro endpoint to change it.",
                    host, ConnectionStringKey);
            }
            else
            {
                logger.LogInformation(
                    "Read queries go to standby {Host}, {Lag:F1}ms behind the primary.", host, lag.Value);
            }

            activity?.SetTag("read.host", host);
            activity?.SetTag("read.is_standby", lag is not null);
        }
        catch (Exception ex)
        {
            // Never fatal. Reads fall back to the primary connection at worst, and a service that
            // will not start because a REPLICA is unreachable has turned an optimisation into an
            // availability regression.
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            logger.LogWarning(ex, "Could not determine what {Setting} reaches.", ConnectionStringKey);
        }
    }
}
