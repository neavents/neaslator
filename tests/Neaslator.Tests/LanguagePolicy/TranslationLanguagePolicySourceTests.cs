using System.Net;
using System.Text;
using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using Neavents.Messaging.Contracts.Settings;
using Neaslator.Infrastructure.LanguagePolicy;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using StackExchange.Redis;

namespace Neaslator.Tests.LanguagePolicy;

public sealed class TranslationLanguagePolicySourceTests
{
    private static readonly Ulid Owner = Ulid.NewUlid();
    private static readonly Ulid Org = Ulid.NewUlid();

    private sealed class Identity(HttpStatusCode status, string? body = null) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body ?? "", Encoding.UTF8, "application/json"),
            });
        }
    }

    private static (IdentityTranslationLanguagePolicySource Source, IDatabase Cache) Build(Identity identity, bool withBase = true, bool cacheDown = false)
    {
        IDatabase cache = Substitute.For<IDatabase>();
        if (cacheDown)
        {
            cache.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
                .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"));
            cache.SetAddAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
                .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"));
        }
        else
        {
            cache.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(RedisValue.Null);
        }

        IConnectionMultiplexer garnet = Substitute.For<IConnectionMultiplexer>();
        garnet.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(cache);
        HttpClient http = new(identity) { BaseAddress = withBase ? new Uri("http://identity.local") : null };
        return (new IdentityTranslationLanguagePolicySource(http, garnet, NullLogger<IdentityTranslationLanguagePolicySource>.Instance), cache);
    }

    private static string Defaults(string? languages) =>
        $$"""{"orgId":"{{Org}}","currency":"TRY","language":"tr","timezone":null,"translationLanguages":{{languages ?? "null"}}}""";

    [Fact]
    public void A_policy_without_a_list_permits_every_language_and_a_list_only_its_own()
    {
        TranslationLanguagePolicy.Unrestricted.Permits("fr").Should().BeTrue();
        TranslationLanguagePolicy restricted = new(["de", "en"]);
        restricted.Permits("DE").Should().BeTrue();
        restricted.Permits("fr").Should().BeFalse();
        new TranslationLanguagePolicy([]).Permits("de").Should().BeFalse();
    }

    [Fact]
    public async Task Without_an_identity_address_nothing_is_restricted_and_nothing_is_asked()
    {
        Identity identity = new(HttpStatusCode.OK, Defaults("[\"tr\"]"));
        (IdentityTranslationLanguagePolicySource source, _) = Build(identity, withBase: false);

        (await source.GetAsync(Owner, default)).Should().Be(TranslationLanguagePolicy.Unrestricted);
        identity.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_listed_answer_is_returned_and_remembered_for_the_organisation()
    {
        (IdentityTranslationLanguagePolicySource source, IDatabase cache) = Build(new Identity(HttpStatusCode.OK, Defaults("[\"de\",\"en\"]")));

        (await source.GetAsync(Owner, default)).Allowed.Should().Equal("de", "en");

        await cache.Received().SetAddAsync(IdentityTranslationLanguagePolicySource.OrganisationKey(Org),
            IdentityTranslationLanguagePolicySource.OwnerKey(Owner), Arg.Any<CommandFlags>());
        bool stored = cache.ReceivedCalls().Any(call => call.GetMethodInfo().Name == nameof(IDatabase.StringSetAsync)
            && call.GetArguments()[0]!.ToString() == IdentityTranslationLanguagePolicySource.OwnerKey(Owner)
            && call.GetArguments()[1]!.ToString()!.Contains("\"de\"", StringComparison.Ordinal));
        stored.Should().BeTrue("the answer is kept in Garnet for every replica");
    }

    [Fact]
    public async Task A_cached_answer_costs_no_identity_call()
    {
        Identity identity = new(HttpStatusCode.OK, Defaults("null"));
        (IdentityTranslationLanguagePolicySource source, IDatabase cache) = Build(identity);
        cache.StringGetAsync(IdentityTranslationLanguagePolicySource.OwnerKey(Owner), Arg.Any<CommandFlags>())
            .Returns(new RedisValue("{\"languages\":[\"tr\"]}"));

        (await source.GetAsync(Owner, default)).Allowed.Should().Equal("tr");
        identity.Calls.Should().Be(0);
    }

    [Fact]
    public async Task An_unknown_owner_is_unrestricted()
    {
        (IdentityTranslationLanguagePolicySource source, _) = Build(new Identity(HttpStatusCode.NotFound));

        (await source.GetAsync(Owner, default)).Should().Be(TranslationLanguagePolicy.Unrestricted);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task An_identity_that_refuses_fails_the_lookup_rather_than_permitting_everything(HttpStatusCode status)
    {
        (IdentityTranslationLanguagePolicySource source, _) = Build(new Identity(status));

        await FluentActions.Invoking(() => source.GetAsync(Owner, default)).Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task A_cache_that_is_down_falls_through_to_identity()
    {
        Identity identity = new(HttpStatusCode.OK, Defaults("[]"));
        (IdentityTranslationLanguagePolicySource source, _) = Build(identity, cacheDown: true);

        (await source.GetAsync(Owner, default)).Allowed.Should().BeEmpty();
        identity.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Forgetting_an_organisation_drops_every_venue_it_cached()
    {
        (IdentityTranslationLanguagePolicySource source, IDatabase cache) = Build(new Identity(HttpStatusCode.OK));
        string venue = IdentityTranslationLanguagePolicySource.OwnerKey(Owner);
        cache.SetMembersAsync(IdentityTranslationLanguagePolicySource.OrganisationKey(Org), Arg.Any<CommandFlags>())
            .Returns([new RedisValue(venue)]);

        await source.ForgetOrganisationAsync(Org, default);

        await cache.Received().KeyDeleteAsync(
            Arg.Is<RedisKey[]>(keys => keys.Select(k => k.ToString()).SequenceEqual(new[] { venue, IdentityTranslationLanguagePolicySource.OrganisationKey(Org) })),
            Arg.Any<CommandFlags>());
    }

    [Theory]
    [InlineData("identity", TranslationLanguageSettingsChangedConsumer.SettingKey, true)]
    [InlineData("identity", TranslationLanguageSettingsChangedConsumer.ManageTogetherKey, true)]
    [InlineData("identity", "locale.currency", false)]
    [InlineData("inventory", TranslationLanguageSettingsChangedConsumer.SettingKey, false)]
    public async Task Only_identitys_change_to_the_languages_or_to_managing_venues_together_drops_the_cache(string service, string key, bool drops)
    {
        ITranslationLanguagePolicySource policies = Substitute.For<ITranslationLanguagePolicySource>();
        ConsumeContext<SettingsChanged> context = Substitute.For<ConsumeContext<SettingsChanged>>();
        context.Message.Returns(new SettingsChanged
        {
            OrgId = Org, OwningService = service, Keys = [key], Level = "org", ScopeId = Org,
            Change = SettingsChanged.ValueChanged, Timestamp = DateTimeOffset.UnixEpoch,
        });

        await new TranslationLanguageSettingsChangedConsumer(policies).Consume(context);

        await policies.Received(drops ? 1 : 0).ForgetOrganisationAsync(Org, Arg.Any<CancellationToken>());
    }
}
