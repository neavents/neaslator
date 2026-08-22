using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using FluentAssertions;
using Neaslator.Persistence;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Neaslator.Tests.Persistence;

/// <summary>
/// What <see cref="PostgresDirect.Create"/> accepts, with no database involved.
/// </summary>
public sealed class PostgresDirectCreationTests
{
    private const string Pooled = "Host=pooler;Database=neaslator;Username=u;Password=p";
    private const string Direct = "Host=postgres;Database=neaslator;Username=u;Password=p";

    [Fact]
    public void An_absent_direct_string_falls_back_to_the_pooled_one()
    {
        // The state of the estate today, and it is correct while nothing is pooling. What makes it
        // safe later is not this decision but ValidateAsync, which refuses to boot on a connection
        // that cannot hold a session lock.
        PostgresDirect direct = PostgresDirect.Create(Pooled, null, "ConnectionStrings:NeaslatorDb");

        direct.ConnectionString.Should().Be(Pooled);
        direct.IsExplicitlyConfigured.Should().BeFalse(
            "the fallback has to be visible, or a log cannot tell the two configurations apart");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_direct_string_is_the_same_as_an_absent_one(string blank)
    {
        // An env var set to empty is how this arrives in a container, not as a missing key.
        PostgresDirect.Create(Pooled, blank, "ConnectionStrings:NeaslatorDb")
            .IsExplicitlyConfigured.Should().BeFalse();
    }

    [Fact]
    public void A_configured_direct_string_is_used_and_reported_as_configured()
    {
        PostgresDirect direct = PostgresDirect.Create(Pooled, Direct, "ConnectionStrings:NeaslatorDb");

        // Compared field by field rather than as a string: Create adds a pool ceiling when the
        // string does not name one, so an exact match would only be pinning the formatting.
        var actual = new NpgsqlConnectionStringBuilder(direct.ConnectionString);
        var expected = new NpgsqlConnectionStringBuilder(Direct);

        actual.Host.Should().Be(expected.Host, "the route must be the one that was configured");
        actual.Database.Should().Be(expected.Database);
        actual.Username.Should().Be(expected.Username);
        direct.IsExplicitlyConfigured.Should().BeTrue();
    }

    [Fact]
    public void A_missing_pooled_string_is_refused_by_name()
    {
        Action create = () => PostgresDirect.Create(null, Direct, "ConnectionStrings:NeaslatorDb");

        create.Should().Throw<PostgresDirectMisconfiguredException>()
            .WithMessage("*ConnectionStrings:NeaslatorDb*",
                "the one job of this error is to tell an operator which setting to add");
    }

    [Fact]
    public void A_configured_direct_string_gets_a_pool_ceiling_it_did_not_ask_for()
    {
        // The moment the direct string differs from the pooled one it gets its own Npgsql pool,
        // and Npgsql defaults to 100 per pool per process. Every pooled string in this estate
        // carries an explicit cap for exactly that reason; a direct string added later must not
        // quietly reintroduce the default beside it.
        PostgresDirect direct = PostgresDirect.Create(Pooled, Direct, "ConnectionStrings:NeaslatorDb");

        new NpgsqlConnectionStringBuilder(direct.ConnectionString).MaxPoolSize.Should().Be(16);
    }

    [Theory]
    [InlineData("Maximum Pool Size=40")]
    [InlineData("MaxPoolSize=40")]
    [InlineData("maximum pool size=40")]
    public void An_explicit_pool_size_is_left_alone_however_it_is_spelled(string setting)
    {
        // The reason this is a Theory: the obvious way to write the guard is ContainsKey, and
        // Npgsql overrides ContainsKey to answer "is this a recognised keyword" — true for every
        // option, set or not. A guard written that way silently never fires, and nothing about it
        // looks wrong.
        PostgresDirect direct = PostgresDirect.Create(
            Pooled, $"{Direct};{setting}", "ConnectionStrings:NeaslatorDb");

        new NpgsqlConnectionStringBuilder(direct.ConnectionString).MaxPoolSize.Should().Be(40);
    }

    [Fact]
    public void The_fallback_keeps_the_pooled_string_exactly_as_written()
    {
        // Untouched, deliberately: the pooled string already carries the service's own cap, and
        // rewriting it here would silently change what request traffic runs on.
        PostgresDirect.Create(Pooled, null, "ConnectionStrings:NeaslatorDb")
            .ConnectionString.Should().Be(Pooled);
    }

    [Fact]
    public void The_setting_is_spelled_the_same_in_every_service()
    {
        // Every service names its pooled connection string differently — Postgres,
        // PostgreSqlDefaultConnection, PostgreSQL, NeaslatorDb, DefaultConnection. The direct one
        // is deliberately uniform, so one deployment change reaches all seven.
        PostgresDirect.SettingName.Should().Be("ConnectionStrings:PostgresDirect");
        PostgresDirect.ConnectionStringKey.Should().Be("PostgresDirect");
    }
}

/// <summary>
/// The startup probe, against a real PostgreSQL and a real PgBouncer.
/// </summary>
/// <remarks>
/// <para>
/// These assert the two failures the class exists to prevent, and both are silent without it.
/// </para>
/// <para>
/// <b>A session lock behind a transaction pooler leaks.</b> PgBouncer in transaction mode binds a
/// client to a server connection only for the length of a transaction, so
/// <c>pg_try_advisory_lock</c> in autocommit takes the lock and hands the server connection back to
/// the pool still holding it. Measured against a live PgBouncer 1.25: a client took advisory lock
/// 777000111, disconnected, and the lock was still held by an idle backend afterwards — unreachable
/// by anyone until that connection is recycled. The sweep it guards then never runs on any replica.
/// </para>
/// <para>
/// <b>A direct string aimed at the wrong database excludes nothing.</b> Advisory locks are scoped
/// to a database: two sessions on the same database contend for a key, two sessions on different
/// databases in one cluster are both granted it. That only bites during a rollout, where both
/// configurations are live at once — a pod without the setting locks where the pooled string
/// points, a pod with a wrong one locks elsewhere, neither sees the other, and both migrate.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class PostgresDirectProbeTests : IAsyncLifetime
{
    private const string User = "neavents";
    private const string Password = "neavents_test";
    private const string Database = "neaslator";
    private const string OtherDatabase = "somewhere_else";

    private readonly INetwork _network = new NetworkBuilder().Build();
    private readonly PostgreSqlContainer _postgres;
    private readonly IContainer _pooler;
    private readonly IContainer _discardingPooler;

    public PostgresDirectProbeTests()
    {
        _postgres = new PostgreSqlBuilder("postgres:17-alpine")
            .WithNetwork(_network)
            .WithNetworkAliases("postgres")
            .WithUsername(User)
            .WithPassword(Password)
            .WithDatabase(Database)
            .Build();

        // Transaction pooling with PgBouncer's defaults, which is the configuration a team reaches
        // for when connection count becomes the problem — and the one that breaks session locks.
        _pooler = new ContainerBuilder("edoburu/pgbouncer:latest")
            .WithNetwork(_network)
            .WithEnvironment("DB_HOST", "postgres")
            .WithEnvironment("DB_PORT", "5432")
            .WithEnvironment("DB_USER", User)
            .WithEnvironment("DB_PASSWORD", Password)
            .WithEnvironment("POOL_MODE", "transaction")
            .WithEnvironment("AUTH_TYPE", "plain")
            .WithEnvironment("MAX_CLIENT_CONN", "50")
            .WithEnvironment("DEFAULT_POOL_SIZE", "5")
            .WithEnvironment("LISTEN_PORT", "6432")
            .WithPortBinding(6432, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("process up"))
            .Build();

        // The other transaction-pooling configuration, and it hides from the leak check. With
        // server_reset_query_always on, PgBouncer runs DISCARD ALL at the end of every transaction,
        // which releases advisory locks immediately — so nothing leaks onto a server connection and
        // a probe that only looks for leaks sees a clean server. Session locks are nonetheless
        // completely broken: the lock is gone before the next statement runs.
        _discardingPooler = new ContainerBuilder("edoburu/pgbouncer:latest")
            .WithNetwork(_network)
            .WithEnvironment("DB_HOST", "postgres")
            .WithEnvironment("DB_PORT", "5432")
            .WithEnvironment("DB_USER", User)
            .WithEnvironment("DB_PASSWORD", Password)
            .WithEnvironment("POOL_MODE", "transaction")
            .WithEnvironment("AUTH_TYPE", "plain")
            .WithEnvironment("MAX_CLIENT_CONN", "50")
            .WithEnvironment("DEFAULT_POOL_SIZE", "5")
            .WithEnvironment("SERVER_RESET_QUERY", "DISCARD ALL")
            .WithEnvironment("SERVER_RESET_QUERY_ALWAYS", "1")
            .WithEnvironment("LISTEN_PORT", "6432")
            .WithPortBinding(6432, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("process up"))
            .Build();
    }

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        // A second database in the SAME cluster. Everything about the route is identical — host,
        // port, credentials — so the only thing the namespace check can be reacting to is the
        // database, which is exactly the property under test.
        await using (var admin = new NpgsqlConnection(_postgres.GetConnectionString()))
        {
            await admin.OpenAsync();
            await using NpgsqlCommand create = admin.CreateCommand();
            create.CommandText = $"CREATE DATABASE {OtherDatabase}";
            await create.ExecuteNonQueryAsync();
        }

        await _pooler.StartAsync();
        await _discardingPooler.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _discardingPooler.DisposeAsync();
        await _pooler.DisposeAsync();
        await _postgres.DisposeAsync();
        await _network.DisposeAsync();
    }

    private string DirectConnectionString => _postgres.GetConnectionString();

    private string OtherDatabaseConnectionString =>
        new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = OtherDatabase }
            .ConnectionString;

    private string PooledThroughPgBouncer => Through(_pooler);

    private string PooledThroughDiscardingPgBouncer => Through(_discardingPooler);

    private static string Through(IContainer pooler) =>
        new NpgsqlConnectionStringBuilder
        {
            Host = pooler.Hostname,
            Port = pooler.GetMappedPublicPort(6432),
            Database = Database,
            Username = User,
            Password = Password,
        }.ConnectionString;

    [Fact]
    public async Task A_direct_connection_passes_and_reports_what_it_reached()
    {
        PostgresDirect direct =
            PostgresDirect.Create(DirectConnectionString, null, "ConnectionStrings:NeaslatorDb");

        PostgresDirectDiagnostics diagnostics = await direct.ValidateAsync();

        diagnostics.Database.Should().Be(Database);
        diagnostics.BackendProcessId.Should().BeGreaterThan(0);
        diagnostics.IsExplicitlyConfigured.Should().BeFalse();
    }

    [Fact]
    public async Task A_direct_string_reaching_a_different_database_is_refused()
    {
        // Same cluster, same credentials, one letter different in the database. Without this check
        // every lock is granted immediately and there is no error anywhere.
        PostgresDirect direct = PostgresDirect.Create(
            DirectConnectionString, OtherDatabaseConnectionString, "ConnectionStrings:NeaslatorDb");

        Func<Task> validate = () => direct.ValidateAsync();

        (await validate.Should().ThrowAsync<PostgresDirectMisconfiguredException>())
            .Which.Message.Should()
                .Contain(OtherDatabase, "the message must name where the direct string actually landed")
                .And.Contain(Database, "and where the pooled one did, so the difference is visible")
                .And.Contain("rolling update", "which is the circumstance that makes this dangerous");
    }

    [Fact]
    public async Task A_transaction_pooler_is_refused_as_the_direct_string()
    {
        PostgresDirect direct = PostgresDirect.Create(
            DirectConnectionString, PooledThroughPgBouncer, "ConnectionStrings:NeaslatorDb");

        Func<Task> validate = () => direct.ValidateAsync();

        (await validate.Should().ThrowAsync<PostgresDirectMisconfiguredException>())
            .Which.Message.Should().Contain(PostgresDirect.SettingName);
    }

    [Fact]
    public async Task A_pooler_that_discards_after_every_transaction_is_refused_too()
    {
        // Nothing leaks here, so the leak check passes and something else has to catch it: the lock
        // is simply not there on the following statement. Without this second signal, the safest-
        // looking PgBouncer configuration would be the one that got through.
        PostgresDirect direct = PostgresDirect.Create(
            DirectConnectionString, PooledThroughDiscardingPgBouncer, "ConnectionStrings:NeaslatorDb");

        Func<Task> validate = () => direct.ValidateAsync();

        (await validate.Should().ThrowAsync<PostgresDirectMisconfiguredException>())
            .Which.Message.Should().Contain("session advisory lock");
    }

    [Fact]
    public async Task A_pooled_string_with_no_direct_string_is_refused_and_names_the_setting_to_add()
    {
        // The accident this is really guarding against: someone puts PgBouncer in front of the
        // existing connection string and changes nothing else. Every singleton in the service
        // silently stops working. Instead, the service does not start, and says why.
        PostgresDirect direct =
            PostgresDirect.Create(PooledThroughPgBouncer, null, "ConnectionStrings:NeaslatorDb");

        Func<Task> validate = () => direct.ValidateAsync();

        (await validate.Should().ThrowAsync<PostgresDirectMisconfiguredException>())
            .Which.Message.Should()
                .Contain("ConnectionStrings:NeaslatorDb", "the setting that is currently wrong")
                .And.Contain(PostgresDirect.SettingName, "and the one to add");
    }

    [Fact]
    public async Task A_pooled_string_behind_the_pooler_is_fine_once_the_direct_string_bypasses_it()
    {
        // The fix, end to end: request traffic through PgBouncer, session locks straight at
        // PostgreSQL. This is the configuration the estate moves to, and it must pass.
        PostgresDirect direct = PostgresDirect.Create(
            PooledThroughPgBouncer, DirectConnectionString, "ConnectionStrings:NeaslatorDb");

        PostgresDirectDiagnostics diagnostics = await direct.ValidateAsync();

        diagnostics.Database.Should().Be(Database);
        diagnostics.IsExplicitlyConfigured.Should().BeTrue();
    }

    [Fact]
    public async Task Two_replicas_validating_at_once_do_not_refuse_each_other()
    {
        // The probe takes real advisory locks. A fixed probe key would make the second replica in a
        // rollout fail startup because the first was validating at that moment, which would be a
        // self-inflicted outage on every deploy. The key is random per call for that reason.
        PostgresDirect direct =
            PostgresDirect.Create(DirectConnectionString, null, "ConnectionStrings:NeaslatorDb");

        PostgresDirectDiagnostics[] all = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(_ => direct.ValidateAsync()));

        all.Should().AllSatisfy(d => d.Database.Should().Be(Database));
    }
}
