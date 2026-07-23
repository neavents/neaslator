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
    private static HttpMenuDataProvider Provider(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var http = new HttpClient(new FakeHandler(status, body)) { BaseAddress = new Uri("http://menu.test") };
        return new HttpMenuDataProvider(http, Substitute.For<ILogger<HttpMenuDataProvider>>());
    }

    [Fact]
    public async Task NullSectionsArray_DoesNotThrow_TreatedAsEmpty()
    {
        string body = """{"id":"01F8MECHZX3TBDSZ7XRADM79XV","name":"M","sections":null}""";
        var provider = Provider(body);

        var result = await provider.GetMenuSnapshotAsync(Ulid.NewUlid(), CancellationToken.None);

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

        var result = await provider.GetMenuSnapshotAsync(Ulid.NewUlid(), CancellationToken.None);

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

        var result = await provider.GetMenuSnapshotAsync(Ulid.NewUlid(), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Sections[0].Items[0].SubItems.Should().BeEmpty();
    }

    [Fact]
    public async Task EmptySectionsArray_ReturnsEmptySnapshot()
    {
        string body = """{"id":"01F8MECHZX3TBDSZ7XRADM79XV","name":"M","sections":[]}""";
        var provider = Provider(body);

        var result = await provider.GetMenuSnapshotAsync(Ulid.NewUlid(), CancellationToken.None);

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
