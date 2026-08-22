using Npgsql;

namespace Neaslator.Persistence;

/// <summary>
/// The connection that session-scoped advisory locks are taken on, and the startup check that
/// proves it can actually hold one.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this exists for.</b> Every singleton in this service — the startup migration, and any
/// background sweep that must run on one replica rather than all of them — is guarded by a
/// PostgreSQL <em>session</em> advisory lock. A session lock belongs to a server connection and
/// lives exactly as long as that connection does. That is fine today, when the service talks
/// straight to PostgreSQL. It stops being fine the moment a transaction-pooling proxy is put in
/// front of it.
/// </para>
/// <para>
/// <b>Why a pooler breaks it.</b> PgBouncer in transaction mode binds a client to a server
/// connection only for the length of a transaction. <c>SELECT pg_try_advisory_lock(k)</c> in
/// autocommit is its own transaction, so the lock is taken and the server connection is handed
/// straight back to the pool with the lock still on it. The work then runs on some other server
/// connection, and the matching <c>pg_advisory_unlock</c> lands on a third — returns false, releases
/// nothing, and leaks the lock onto a pooled connection where it blocks every replica until that
/// connection is recycled. The failure is worse than the race the lock was added to prevent, and
/// nothing in the code looks wrong.
/// </para>
/// <para>
/// <b>The shape of the fix.</b> Two connection strings, not one. The pooled one carries all the
/// ordinary request traffic and is what a pooler should sit in front of. This one goes straight at
/// PostgreSQL and is used by exactly two things: the startup migration lock, and the background job
/// locks. Both are few and long-lived, so they gain nothing from pooling in the first place.
/// </para>
/// <para>
/// <b>Absence is allowed; being wrong is not.</b> When <c>ConnectionStrings:PostgresDirect</c> is
/// not set this falls back to the pooled string, which is correct as long as no pooler is deployed
/// — and that is the state of the estate today. What it will not do is fall back <em>silently past
/// a pooler</em>: <see cref="ValidateAsync"/> runs a live probe at startup and refuses to boot if
/// the connection cannot hold a session lock. So the day a proxy is introduced without anyone
/// setting the new string, this service stops with a message naming the setting, instead of
/// running with every singleton quietly broken.
/// </para>
/// <para>
/// <b>Why the two strings must reach the same database.</b> Advisory locks are scoped to a
/// <em>database</em>, not to the cluster — measured against this estate's own PostgreSQL: two
/// sessions on the same database contend for key 987654321, two sessions on different databases in
/// the same cluster both take it, and <c>pg_locks.database</c> carries the database oid.
/// </para>
/// <para>
/// Replicas that all share one wrong database would still exclude each other, so this is not about
/// a single misconfigured value — it is about a <em>rollout</em>, where two configurations are live
/// at once by design. A pod that has not been given <c>PostgresDirect</c> locks in the database the
/// pooled string names; a new pod whose <c>PostgresDirect</c> names a different one locks
/// somewhere else. Neither sees the other, both migrate, and the DDL interleaves — the four
/// incidents this lock was added to prevent, reintroduced by the setting meant to protect it, with
/// no error anywhere. Requiring the two strings to agree is what makes the mixed state during a
/// rollout safe, so <see cref="ValidateAsync"/> proves it rather than trusting it.
/// </para>
/// <para>
/// <b>This file is a copy.</b> The estate keeps no shared infrastructure package, so the same class
/// lives in identity, menu, media, messaging, subscription, neaslator and dracula. A fix here
/// reaches none of the others — change them together. neadocs needs none of it: its migrator uses
/// <c>pg_advisory_xact_lock</c>, which is transaction-scoped and pool-safe.
/// </para>
/// </remarks>
public sealed class PostgresDirect
{
    /// <summary>
    /// The connection-string name, identical in every service in the estate so that one deployment
    /// change reaches all of them.
    /// </summary>
    public const string SettingName = "ConnectionStrings:PostgresDirect";

    /// <summary>The key under <c>ConnectionStrings</c>, for callers that read that section.</summary>
    public const string ConnectionStringKey = "PostgresDirect";

    private readonly string _pooled;
    private readonly string _pooledSettingName;

    private PostgresDirect(string pooled, string direct, string pooledSettingName, bool isExplicit)
    {
        _pooled = pooled;
        _pooledSettingName = pooledSettingName;
        ConnectionString = direct;
        IsExplicitlyConfigured = isExplicit;
    }

    /// <summary>The string to open session-lock connections on.</summary>
    public string ConnectionString { get; }

    /// <summary>
    /// False when <see cref="SettingName"/> was absent and the pooled string is being reused. Safe
    /// today, and <see cref="ValidateAsync"/> is what keeps it safe later; worth logging once at
    /// startup so the fallback is visible in a log rather than only in this code.
    /// </summary>
    public bool IsExplicitlyConfigured { get; }

    /// <summary>
    /// Builds the source from the two configured strings.
    /// </summary>
    /// <param name="pooled">
    /// The ordinary connection string — the one the DbContext is configured with. Required: a
    /// service that cannot reach its database has nothing to validate and must not start.
    /// </param>
    /// <param name="direct">
    /// <see cref="SettingName"/>, or null/blank to fall back to <paramref name="pooled"/>.
    /// </param>
    /// <param name="pooledSettingName">
    /// What the pooled string is called in this service's configuration. Every service names it
    /// differently, and an error message that guesses the name is an error message that sends the
    /// reader to the wrong file.
    /// </param>
    public static PostgresDirect Create(string? pooled, string? direct, string pooledSettingName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pooledSettingName);

        if (string.IsNullOrWhiteSpace(pooled))
        {
            throw new PostgresDirectMisconfiguredException(
                $"'{pooledSettingName}' is not configured. The service cannot start without a database.");
        }

        return string.IsNullOrWhiteSpace(direct)
            ? new PostgresDirect(pooled, pooled, pooledSettingName, isExplicit: false)
            : new PostgresDirect(pooled, Capped(direct), pooledSettingName, isExplicit: true);
    }

    /// <summary>
    /// Gives the direct string a modest pool ceiling if it does not name one itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The moment this string differs from the pooled one it gets its own Npgsql pool, and Npgsql
    /// defaults to 100 connections per pool per process. That default has already cost this estate
    /// once: every pooled string here carries an explicit "Maximum Pool Size=20" precisely because
    /// one pod otherwise sizes itself to exhaust a PostgreSQL that allows 100 in total. A direct
    /// string added later would have quietly reintroduced the same ceiling next to it.
    /// </para>
    /// <para>
    /// Sixteen, because the only things that use this connection are the startup migration and the
    /// singleton job locks, and the busiest service in the estate has ten such jobs. A ceiling is
    /// not a reservation — Npgsql opens nothing up front — so the headroom costs nothing and the
    /// cap bounds the damage if something ever holds leases it should not.
    /// </para>
    /// <para>
    /// Only a default. An operator who writes "Maximum Pool Size" into the string keeps their own
    /// value, and the fallback case is untouched because the pooled string already carries one.
    /// </para>
    /// </remarks>
    private static string Capped(string direct)
    {
        var builder = new NpgsqlConnectionStringBuilder(direct);

        // ShouldSerialize, not ContainsKey. Npgsql overrides ContainsKey to mean "is this a
        // recognised keyword", so it answers true for every option whether or not the string set
        // one — measured: "Host=h" reports ContainsKey("Maximum Pool Size") = true with
        // MaxPoolSize sitting at its default of 100. A guard written that way never fires, which
        // is the failure this whole class exists to argue against. ShouldSerialize answers the
        // question actually being asked, and is true for every spelling Npgsql accepts
        // ("Maximum Pool Size", "MaxPoolSize", lower case).
        if (!builder.ShouldSerialize("Maximum Pool Size"))
        {
            builder.MaxPoolSize = 16;
        }

        return builder.ConnectionString;
    }

    /// <summary>
    /// Opens a connection for a session-scoped advisory lock. The caller owns it and must dispose
    /// it — disposing is what ultimately releases the lock, whatever else happens.
    /// </summary>
    public async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new NpgsqlConnection(ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Proves, against the live server, that this connection can hold a session advisory lock and
    /// that it takes those locks in the same namespace as the pooled connection. Throws
    /// <see cref="PostgresDirectMisconfiguredException"/> if either is not true.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Call this once at startup, before migrating. It costs two connections and a handful of round
    /// trips, and it converts two silent, deferred failures into a refusal to boot.
    /// </para>
    /// <para>
    /// <b>Three checks, and why it takes three.</b> A transaction pooler can be configured two ways
    /// and each hides from the other's test.
    /// </para>
    /// <list type="number">
    /// <item>
    /// <b>Does a lock outlive the client that took it?</b> Take a lock, close the socket, then look
    /// for the lock from somewhere else. A real backend dies with its client and takes its locks
    /// with it, so the lock is gone. PgBouncer in transaction mode with the default
    /// <c>server_reset_query_always = 0</c> runs no reset in that mode, so the server connection
    /// goes back to the pool still holding it — measured here: a disconnected client left
    /// <c>advisory 777000111</c> held by an idle backend, unreachable by anyone. This check is
    /// deterministic; it observes server state rather than guessing from which backend answered.
    /// </item>
    /// <item>
    /// <b>Does a lock survive to the next statement?</b> The opposite configuration,
    /// <c>server_reset_query_always = 1</c>, discards the lock the moment its transaction ends — so
    /// nothing leaks and check 1 sees a clean server, but session locks do not work at all. Reading
    /// <c>pg_locks</c> for this session on the following statement catches that, along with a
    /// backend pid that moves and a session setting that does not stick.
    /// </item>
    /// <item>
    /// <b>Do the two connection strings contend?</b> Positive proof that a lock taken on the direct
    /// string is visible to the pooled one — the check that catches a direct string aimed at the
    /// wrong database, where every lock is granted and nothing looks broken.
    /// </item>
    /// </list>
    /// <para>
    /// The probe key is random per process, so a leaked probe lock blocks nothing real and two
    /// replicas validating at the same instant never mistake each other for a misconfiguration.
    /// </para>
    /// </remarks>
    public async Task<PostgresDirectDiagnostics> ValidateAsync(CancellationToken cancellationToken = default)
    {
        // Random per process, so two replicas validating at the same instant never collide on the
        // probe key and mistake each other for a misconfiguration. Interpolated into the SQL rather
        // than parameterised: it is a long produced by Random, so there is nothing to inject, and
        // every other advisory-lock call site in the estate reads the same way.
        long probe = Random.Shared.NextInt64();
        string token = $"neavents-direct-probe-{probe:x}";

        await ServerConnectionOutlivesClientAsync(probe, cancellationToken).ConfigureAwait(false);

        await using NpgsqlConnection direct = await OpenAsync(cancellationToken).ConfigureAwait(false);

        int pid = await ScalarAsync<int>(direct, "SELECT pg_backend_pid()", cancellationToken)
            .ConfigureAwait(false);
        string database = await ScalarAsync<string>(direct, "SELECT current_database()", cancellationToken)
            .ConfigureAwait(false);

        await SessionAffinityAsync(direct, probe, token, pid, database, cancellationToken).ConfigureAwait(false);
        await SharedLockNamespaceAsync(direct, probe, database, cancellationToken).ConfigureAwait(false);

        return new PostgresDirectDiagnostics(database, pid, IsExplicitlyConfigured);
    }

    /// <summary>
    /// Takes a lock, hangs up, and looks for the lock afterwards. A PostgreSQL backend dies with its
    /// client, so the lock must be gone; if it is still held, something between here and the server
    /// kept the connection alive and this string cannot own a session lock.
    /// </summary>
    /// <remarks>
    /// Both connections disable Npgsql's own pooling. That is the whole mechanism: with pooling on,
    /// disposing hands the connection back to Npgsql instead of closing the socket, and the backend
    /// this check is trying to outlive is still there — the probe would fail against a perfectly
    /// direct connection. Npgsql's pool is not the hazard, since it resets a connection on return
    /// and that releases advisory locks; it is simply in the way of the measurement.
    /// </remarks>
    private async Task ServerConnectionOutlivesClientAsync(long probe, CancellationToken cancellationToken)
    {
        string unpooled = Unpooled(ConnectionString);

        await using (var taker = new NpgsqlConnection(unpooled))
        {
            await taker.OpenAsync(cancellationToken).ConfigureAwait(false);

            bool taken = await ScalarAsync<bool>(
                taker, $"SELECT pg_try_advisory_lock({probe})", cancellationToken).ConfigureAwait(false);

            if (!taken)
            {
                throw Misconfigured(
                    await ScalarAsync<string>(taker, "SELECT current_database()", cancellationToken)
                        .ConfigureAwait(false),
                    $"the probe advisory lock {probe} could not be taken on a fresh connection.");
            }

            // Deliberately no unlock. Disposing is the event under test.
        }

        await using var observer = new NpgsqlConnection(unpooled);
        await observer.OpenAsync(cancellationToken).ConfigureAwait(false);

        // A closed socket does not retire the backend synchronously, so give it a moment. The
        // passing path exits on the first or second look; only a genuine leak waits out the budget.
        for (int attempt = 0; attempt < 40; attempt++)
        {
            if (!await ProbeLockHeldAsync(observer, probe, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        string database = await ScalarAsync<string>(observer, "SELECT current_database()", cancellationToken)
            .ConfigureAwait(false);

        throw Misconfigured(
            database,
            $"advisory lock {probe} was still held two seconds after the connection that took it was " +
            "closed, so the server connection outlived its client and the lock leaked onto it. A lock " +
            "leaked this way is held until the proxy recycles that connection, and for as long as it " +
            "lasts every replica is locked out of the work it guards.");
    }

    /// <summary>
    /// Whether the probe lock is held by anyone. A one-argument bigint key appears in
    /// <c>pg_locks</c> split across <c>classid</c> (high 32 bits) and <c>objid</c> (low 32), with
    /// <c>objsubid = 1</c> — verified against this estate's PostgreSQL, where key
    /// 1234605616436508552 came back as classid 287454020, objid 1432778632.
    /// </summary>
    private static Task<bool> ProbeLockHeldAsync(
        NpgsqlConnection observer, long probe, CancellationToken cancellationToken) =>
        ScalarAsync<bool>(
            observer,
            "SELECT EXISTS (SELECT 1 FROM pg_locks WHERE locktype = 'advisory' AND objsubid = 1 " +
            $"AND ((classid::bigint << 32) | objid::bigint) = {probe})",
            cancellationToken);

    /// <summary>
    /// The same connection string with Npgsql's client-side pooling turned off, so that closing a
    /// connection closes the socket.
    /// </summary>
    private static string Unpooled(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;

    /// <summary>
    /// Four independent proofs that statements on this connection reach one unchanging backend.
    /// </summary>
    private async Task SessionAffinityAsync(
        NpgsqlConnection direct,
        long probe,
        string token,
        int pid,
        string database,
        CancellationToken cancellationToken)
    {
        // set_config rather than SET: the extended query protocol Npgsql uses cannot parameterise a
        // SET, and the third argument is what makes this session-level rather than transaction-local
        // — which is precisely the property being tested.
        string applied = await SetApplicationNameAsync(direct, token, cancellationToken).ConfigureAwait(false);

        if (!string.Equals(applied, token, StringComparison.Ordinal))
        {
            throw Misconfigured(database, $"set_config returned '{applied}' rather than '{token}'.");
        }

        string echoed = await ScalarAsync<string>(direct, "SHOW application_name", cancellationToken)
            .ConfigureAwait(false);

        if (!string.Equals(echoed, token, StringComparison.Ordinal))
        {
            throw Misconfigured(
                database,
                $"a session-level setting did not survive to the next statement " +
                $"(set '{token}', read back '{echoed}').");
        }

        bool taken = await ScalarAsync<bool>(
            direct, $"SELECT pg_try_advisory_lock({probe})", cancellationToken).ConfigureAwait(false);

        if (!taken)
        {
            // The key is random per process, so nothing else can be holding it.
            throw Misconfigured(database, $"the probe advisory lock {probe} could not be taken.");
        }

        bool checksPassed = false;
        bool released;

        try
        {
            int pidAgain = await ScalarAsync<int>(direct, "SELECT pg_backend_pid()", cancellationToken)
                .ConfigureAwait(false);

            if (pidAgain != pid)
            {
                throw Misconfigured(
                    database,
                    $"consecutive statements reached different backends (pid {pid}, then {pidAgain}).");
            }

            long visible = await ScalarAsync<long>(
                direct,
                "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND pid = pg_backend_pid()",
                cancellationToken).ConfigureAwait(false);

            if (visible == 0)
            {
                throw Misconfigured(
                    database,
                    "an advisory lock taken by this session was not visible to it on the next statement.");
            }

            checksPassed = true;
        }
        finally
        {
            // Released whatever happened above, and never by throwing: an exception raised from a
            // finally block replaces the precise diagnosis with a vague one. The result is inspected
            // afterwards instead, on the path where there is still something to say about it.
            released = await TryReleaseProbeAsync(direct, probe).ConfigureAwait(false);
        }

        if (checksPassed && !released)
        {
            throw Misconfigured(
                database,
                $"pg_advisory_unlock({probe}) released nothing, so the lock was taken on a different " +
                "server connection from the one that tried to release it.");
        }
    }

    /// <summary>
    /// Proves the direct and pooled strings contend with each other. This is the check that catches
    /// a direct string aimed at the wrong database — where every lock is granted and nothing ever
    /// looks broken.
    /// </summary>
    private async Task SharedLockNamespaceAsync(
        NpgsqlConnection direct,
        long probe,
        string database,
        CancellationToken cancellationToken)
    {
        bool held = await ScalarAsync<bool>(
            direct, $"SELECT pg_try_advisory_lock({probe})", cancellationToken).ConfigureAwait(false);

        if (!held)
        {
            throw Misconfigured(database, $"the probe advisory lock {probe} could not be re-taken.");
        }

        try
        {
            await using var pooled = new NpgsqlConnection(_pooled);
            await pooled.OpenAsync(cancellationToken).ConfigureAwait(false);

            string pooledDatabase = await ScalarAsync<string>(
                pooled, "SELECT current_database()", cancellationToken).ConfigureAwait(false);

            bool grantedTwice = await ScalarAsync<bool>(
                pooled, $"SELECT pg_try_advisory_lock({probe})", cancellationToken).ConfigureAwait(false);

            if (grantedTwice)
            {
                // Nothing is protected by this lock, so releasing it is only tidiness — but leaving
                // it on a connection that goes back into the pool would be a second fault on top of
                // the one being reported.
                await TryReleaseProbeAsync(pooled, probe).ConfigureAwait(false);

                throw new PostgresDirectMisconfiguredException(
                    $"'{SettingName}' and '{_pooledSettingName}' do not share an advisory-lock namespace: " +
                    $"both connections were granted lock {probe} at the same time. Advisory locks are scoped " +
                    $"to a database, so a process locking through one of these strings excludes nothing that " +
                    $"locks through the other. During a rolling update both are live at once — a pod without " +
                    $"'{SettingName}' locks where '{_pooledSettingName}' points — so migrations and singleton " +
                    $"jobs would run on two replicas simultaneously, with no error anywhere. " +
                    $"'{SettingName}' reached database '{database}'; '{_pooledSettingName}' reached " +
                    $"'{pooledDatabase}'. They must be the same database in the same cluster; only the route " +
                    $"to it may differ.");
            }
        }
        finally
        {
            await TryReleaseProbeAsync(direct, probe).ConfigureAwait(false);
        }
    }

    private PostgresDirectMisconfiguredException Misconfigured(string database, string symptom) =>
        new(IsExplicitlyConfigured
            ? $"'{SettingName}' cannot hold a session advisory lock: {symptom} It is reaching database " +
              $"'{database}' through something that does not keep a client on one server connection — a " +
              $"transaction-pooling proxy such as PgBouncer. Point '{SettingName}' straight at PostgreSQL, " +
              $"or at a pooler configured for session pooling."
            : $"'{_pooledSettingName}' cannot hold a session advisory lock: {symptom} It is reaching database " +
              $"'{database}' through something that does not keep a client on one server connection — a " +
              $"transaction-pooling proxy such as PgBouncer. Startup migrations and the background job locks " +
              $"need a connection that does. Set '{SettingName}' to a string that goes straight at PostgreSQL; " +
              $"'{_pooledSettingName}' can keep going through the pooler.");

    /// <summary>
    /// Releases a probe lock without ever throwing. Disposing the connection ends the session and
    /// releases it regardless, so a failure here is not worth masking a real diagnosis for.
    /// </summary>
    private static async Task<bool> TryReleaseProbeAsync(NpgsqlConnection connection, long probe)
    {
        try
        {
            await using NpgsqlCommand release = connection.CreateCommand();
            release.CommandText = $"SELECT pg_advisory_unlock({probe})";

            // CancellationToken.None: a cancelled probe is exactly when the lock most needs
            // releasing, and a cancelled unlock would leave it sitting on a live connection.
            return await release.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false) is true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> SetApplicationNameAsync(
        NpgsqlConnection connection, string token, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT set_config('application_name', @token, false)";
        command.Parameters.AddWithValue("token", token);

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value as string ?? string.Empty;
    }

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = sql;

        // ExecuteScalar, because for every query here the answer IS the returned value.
        // ExecuteNonQuery would report rows affected — minus one for a SELECT — which is the read
        // that made ten "singleton" jobs run on every replica for months.
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is T typed
            ? typed
            : throw new PostgresDirectMisconfiguredException(
                $"'{sql}' returned {(value is null ? "null" : value.GetType().Name)} rather than " +
                $"{typeof(T).Name}. This connection is not talking to PostgreSQL.");
    }
}

/// <summary>What the startup probe learned, for the one log line that records it.</summary>
/// <param name="Database">The database the direct connection reached.</param>
/// <param name="BackendProcessId">The backend it reached, which held still across the probe.</param>
/// <param name="IsExplicitlyConfigured">
/// False when <see cref="PostgresDirect.SettingName"/> was absent and the pooled string is in use.
/// </param>
public sealed record PostgresDirectDiagnostics(string Database, int BackendProcessId, bool IsExplicitlyConfigured);

/// <summary>
/// The direct connection cannot do the one thing it exists to do. Thrown during startup, before
/// migrations, so the process dies with a message instead of running with broken mutual exclusion.
/// </summary>
public sealed class PostgresDirectMisconfiguredException(string message) : InvalidOperationException(message);
