using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Neaslator.Infrastructure.MenuData;
using NSubstitute;

namespace Neaslator.Tests.MenuData;

/// <summary>
/// Robustness of menu deserialization against explicit JSON nulls. System.Text.Json
/// overwrites a property initializer when the JSON contains an explicit null, so a
/// menu service that sends "sections": null (or null items/subItems) must not crash
/// the translation consumer with a NullReferenceException.
/// </summary>
public sealed class HttpMenuDataProviderRobustnessTests
{
    /// <summary>
    /// Wraps a menu body in the editor endpoint's envelope.
    /// </summary>
    /// <remarks>
    /// The editor route answers <c>{"smartMenuDto": { ... }}</c>, not a bare menu. It is read
    /// instead of the public projection because the public one is trimmed for the browser budget
    /// and drops the do-not-translate flags — which would silently translate text an author had
    /// excluded — and because it only serves published menus, while translations are usually
    /// requested against a draft.
    /// </remarks>
    private static string Envelope(string menuJson) => $$"""{"smartMenuDto":{{menuJson}}}""";

    private static HttpMenuDataProvider Provider(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var http = new HttpClient(new FakeHandler(status, Envelope(body)))
        {
            BaseAddress = new Uri("http://menu.test"),
        };
        return new HttpMenuDataProvider(http, Substitute.For<ILogger<HttpMenuDataProvider>>());
    }

    /// <summary>Builds a provider over a body that is used verbatim, envelope and all.</summary>
    private static HttpMenuDataProvider RawProvider(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var http = new HttpClient(new FakeHandler(status, body)) { BaseAddress = new Uri("http://menu.test") };
        return new HttpMenuDataProvider(http, Substitute.For<ILogger<HttpMenuDataProvider>>());
    }

    [Fact]
    public async Task A_body_without_the_editor_envelope_yields_nothing_rather_than_a_thin_snapshot()
    {
        // The shape the public projection returns: a bare menu with no `smartMenuDto` wrapper.
        // Returning null here is the point. A partially-populated snapshot would translate a menu
        // whose do-not-translate flags had all defaulted to false — the original bug, and one that
        // surfaces as a customer's brand names being rewritten, not as an error.
        var provider = RawProvider("""{"id":"01F8MECHZX3TBDSZ7XRADM79XV","name":"M","sections":[]}""");

        var result = await provider.GetMenuSnapshotAsync(Ulid.NewUlid(), Ulid.NewUlid(), null, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task An_envelope_with_a_null_menu_yields_nothing()
    {
        var provider = RawProvider("""{"smartMenuDto":null}""");

        var result = await provider.GetMenuSnapshotAsync(Ulid.NewUlid(), Ulid.NewUlid(), null, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task NullSectionsArray_DoesNotThrow_TreatedAsEmpty()
    {
        string body = """{"id":"01F8MECHZX3TBDSZ7XRADM79XV","name":"M","sections":null}""";
        var provider = Provider(body);

        var result = await provider.GetMenuSnapshotAsync(Ulid.NewUlid(), Ulid.NewUlid(), null, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Sections.Should().BeEmpty();
    }

    [Fact]
    public async Task NullItemsArray_DoesNotThrow_TreatedAsEmpty()
    {
        string body = """
        {"id":"01F8MECHZX3TBDSZ7XRADM79XV","name":"M","sections":[
            {"id":"01F8MECHZX3TBDSZ7XRADM79XW","name":"S","doNotTranslateName":false,"doNotTranslateDescription":false,"items":null}
        ]}
        """;
        var provider = Provider(body);

        var result = await provider.GetMenuSnapshotAsync(Ulid.NewUlid(), Ulid.NewUlid(), null, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Sections.Should().ContainSingle();
        result.Sections[0].Items.Should().BeEmpty();
    }

    [Fact]
    public async Task NullSubItemsArray_DoesNotThrow_TreatedAsEmpty()
    {
        string body = """
        {"id":"01F8MECHZX3TBDSZ7XRADM79XV","name":"M","sections":[
            {"id":"01F8MECHZX3TBDSZ7XRADM79XW","name":"S","doNotTranslateName":false,"doNotTranslateDescription":false,"items":[
                {"id":"01F8MECHZX3TBDSZ7XRADM79XX","name":"I","description":null,"doNotTranslateName":false,"doNotTranslateDescription":false,"subItems":null}
            ]}
        ]}
        """;
        var provider = Provider(body);

        var result = await provider.GetMenuSnapshotAsync(Ulid.NewUlid(), Ulid.NewUlid(), null, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Sections[0].Items[0].SubItems.Should().BeEmpty();
    }

    [Fact]
    public async Task EmptySectionsArray_ReturnsEmptySnapshot()
    {
        string body = """{"id":"01F8MECHZX3TBDSZ7XRADM79XV","name":"M","sections":[]}""";
        var provider = Provider(body);

        var result = await provider.GetMenuSnapshotAsync(Ulid.NewUlid(), Ulid.NewUlid(), null, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Sections.Should().BeEmpty();
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public FakeHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body)
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            return Task.FromResult(response);
        }
    }
}
