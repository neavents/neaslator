using FluentAssertions;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Neaslator.Domain.Entities;
using Neaslator.Domain.Enums;
using Neaslator.Features.ProviderHealth;
using Neaslator.Features.RetryFailedTranslations;
using Neaslator.Features.TranslateMenu;
using Neaslator.Features.TranslationStatus;
using Neaslator.Infrastructure.Providers;
using Neaslator.Persistence;
using NSubstitute;

namespace Neaslator.Tests.Endpoints;

/// <summary>
/// Handler-level coverage for the status, retry, and provider-health endpoints. Each
/// returned <see cref="IResult"/> is executed against a <see cref="DefaultHttpContext"/>
/// to assert real status codes; DB-backed handlers use an InMemory context.
/// </summary>
public sealed class StatusRetryHealthEndpointTests : IDisposable
{
    private readonly NeaslatorDbContext _db;
    private static readonly IServiceProvider Services = new ServiceCollection().AddLogging().BuildServiceProvider();

    public StatusRetryHealthEndpointTests()
    {
        DbContextOptions<NeaslatorDbContext> options = new DbContextOptionsBuilder<NeaslatorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new NeaslatorDbContext(options);
    }

    public void Dispose() => _db.Dispose();

    private static async Task<int> StatusOf(IResult result)
    {
        var ctx = new DefaultHttpContext { RequestServices = Services };
        using var stream = new MemoryStream();
        ctx.Response.Body = stream;
        await result.ExecuteAsync(ctx);
        return ctx.Response.StatusCode;
    }

    private async Task SeedSnapshot(Ulid menuId, Ulid ownerId)
    {
        _db.MenuPublishSnapshots.Add(new MenuPublishSnapshot
        {
            MenuId = menuId,
            OwnerId = ownerId,
            SnapshotJson = "{}",
            PublishedAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync();
    }

    // ───── Status ─────

    [Fact]
    public async Task Status_InvalidUlid_Returns400()
    {
        IResult r = await TranslationStatusEndpoint.HandleAsync("not-a-ulid", _db, CancellationToken.None);
        (await StatusOf(r)).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Status_UnknownMenu_Returns404()
    {
        IResult r = await TranslationStatusEndpoint.HandleAsync(Ulid.NewUlid().ToString(), _db, CancellationToken.None);
        (await StatusOf(r)).Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task Status_KnownMenu_Returns200()
    {
        Ulid menuId = Ulid.NewUlid();
        await SeedSnapshot(menuId, Ulid.NewUlid());

        IResult r = await TranslationStatusEndpoint.HandleAsync(menuId.ToString(), _db, CancellationToken.None);
        (await StatusOf(r)).Should().Be(StatusCodes.Status200OK);
    }

    // ───── Retry ─────

    private static RetryTranslationRequest ValidRetry() => new("en", "Restaurant", "Italian");

    [Fact]
    public async Task Retry_InvalidUlid_Returns400()
    {
        IPublishEndpoint pub = Substitute.For<IPublishEndpoint>();
        IResult r = await RetryEndpoint.HandleAsync("bad", ValidRetry(), _db, pub, CancellationToken.None);
        (await StatusOf(r)).Should().Be(StatusCodes.Status400BadRequest);
        await pub.DidNotReceive().Publish(Arg.Any<StartTranslationCommand>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("", "Restaurant", "Italian")]
    [InlineData("en", "", "Italian")]
    [InlineData("en", "Restaurant", "")]
    public async Task Retry_MissingRequiredField_Returns400(string src, string venue, string cuisine)
    {
        IPublishEndpoint pub = Substitute.For<IPublishEndpoint>();
        IResult r = await RetryEndpoint.HandleAsync(Ulid.NewUlid().ToString(), new RetryTranslationRequest(src, venue, cuisine), _db, pub, CancellationToken.None);
        (await StatusOf(r)).Should().Be(StatusCodes.Status400BadRequest);
        await pub.DidNotReceive().Publish(Arg.Any<StartTranslationCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Retry_UnknownMenu_Returns404_NoPublish()
    {
        IPublishEndpoint pub = Substitute.For<IPublishEndpoint>();
        IResult r = await RetryEndpoint.HandleAsync(Ulid.NewUlid().ToString(), ValidRetry(), _db, pub, CancellationToken.None);
        (await StatusOf(r)).Should().Be(StatusCodes.Status404NotFound);
        await pub.DidNotReceive().Publish(Arg.Any<StartTranslationCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Retry_KnownMenu_Returns202_AndPublishesCommandWithOwnerFromSnapshot()
    {
        Ulid menuId = Ulid.NewUlid();
        Ulid ownerId = Ulid.NewUlid();
        await SeedSnapshot(menuId, ownerId);
        IPublishEndpoint pub = Substitute.For<IPublishEndpoint>();

        IResult r = await RetryEndpoint.HandleAsync(menuId.ToString(), ValidRetry(), _db, pub, CancellationToken.None);

        (await StatusOf(r)).Should().Be(StatusCodes.Status202Accepted);
        await pub.Received(1).Publish(
            Arg.Is<StartTranslationCommand>(c =>
                c.MenuId == menuId &&
                c.OwnerId == ownerId &&
                c.SourceLanguageCode == "en" &&
                c.VenueType == "Restaurant" &&
                c.CuisineType == "Italian"),
            Arg.Any<CancellationToken>());
    }

    // ───── Provider health ─────

    [Fact]
    public async Task ProviderHealth_Healthy_Returns200()
    {
        ITranslationProvider provider = Substitute.For<ITranslationProvider>();
        provider.ProviderName.Returns("deepseek");
        provider.Tier.Returns(TranslationProviderTier.Primary);
        provider.IsHealthyAsync(Arg.Any<CancellationToken>()).Returns(true);

        IResult r = await ProviderHealthEndpoint.HandleAsync(provider, CancellationToken.None);
        (await StatusOf(r)).Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task ProviderHealth_Unhealthy_StillReturns200_WithFlag()
    {
        ITranslationProvider provider = Substitute.For<ITranslationProvider>();
        provider.ProviderName.Returns("deepseek");
        provider.Tier.Returns(TranslationProviderTier.Primary);
        provider.IsHealthyAsync(Arg.Any<CancellationToken>()).Returns(false);

        IResult r = await ProviderHealthEndpoint.HandleAsync(provider, CancellationToken.None);

        var ctx = new DefaultHttpContext { RequestServices = Services };
        using var stream = new MemoryStream();
        ctx.Response.Body = stream;
        await r.ExecuteAsync(ctx);
        stream.Position = 0;
        string body = await new StreamReader(stream).ReadToEndAsync();

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        body.Should().Contain("false");
    }
}
