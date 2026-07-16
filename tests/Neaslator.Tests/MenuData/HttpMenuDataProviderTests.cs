using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Neaslator.Infrastructure.MenuData;
using NSubstitute;

namespace Neaslator.Tests.MenuData;

public sealed class HttpMenuDataProviderTests
{
    private static ILogger<HttpMenuDataProvider> CreateLogger()
    {
        return Substitute.For<ILogger<HttpMenuDataProvider>>();
    }

    private static HttpClient CreateHttpClient(HttpStatusCode statusCode, string? responseBody = null)
    {
        var handler = new FakeHttpMessageHandler(statusCode, responseBody);
        return new HttpClient(handler) { BaseAddress = new Uri("http://test.local") };
    }

    [Fact]
    public async Task GetMenuSnapshotAsync_SuccessResponse_MapsToMenuSnapshot()
    {
        var menuId = Ulid.NewUlid();
        var sectionId = Ulid.NewUlid();
        var itemId = Ulid.NewUlid();
        var subItemId = Ulid.NewUlid();

        var responseJson = JsonSerializer.Serialize(new
        {
            id = menuId.ToString(),
            name = "Test Menu",
            sections = new[]
            {
                new
                {
                    id = sectionId.ToString(),
                    name = "Appetizers",
                    doNotTranslateName = false,
                    doNotTranslateDescription = false,
                    items = new[]
                    {
                        new
                        {
                            id = itemId.ToString(),
                            name = "Bruschetta",
                            description = "Tomato and basil",
                            doNotTranslateName = false,
                            doNotTranslateDescription = false,
                            subItems = new[]
                            {
                                new
                                {
                                    id = subItemId.ToString(),
                                    name = "Extra cheese",
                                    description = (string?)null,
                                    doNotTranslateName = false,
                                    doNotTranslateDescription = true
                                }
                            }
                        }
                    }
                }
            }
        });

        var http = CreateHttpClient(HttpStatusCode.OK, responseJson);
        var provider = new HttpMenuDataProvider(http, CreateLogger());

        var result = await provider.GetMenuSnapshotAsync(menuId, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Sections.Should().ContainSingle();
        result.Sections[0].Id.Should().Be(sectionId);
        result.Sections[0].Name.Should().Be("Appetizers");
        result.Sections[0].Items.Should().ContainSingle();
        result.Sections[0].Items[0].Name.Should().Be("Bruschetta");
        result.Sections[0].Items[0].Description.Should().Be("Tomato and basil");
        result.Sections[0].Items[0].SubItems.Should().ContainSingle();
        result.Sections[0].Items[0].SubItems[0].Name.Should().Be("Extra cheese");
        result.Sections[0].Items[0].SubItems[0].DoNotTranslateDescription.Should().BeTrue();
    }

    [Fact]
    public async Task GetMenuSnapshotAsync_NonSuccessStatusCode_ReturnsNull()
    {
        var menuId = Ulid.NewUlid();
        var http = CreateHttpClient(HttpStatusCode.NotFound);
        var provider = new HttpMenuDataProvider(http, CreateLogger());

        var result = await provider.GetMenuSnapshotAsync(menuId, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetMenuSnapshotAsync_ServerError_ReturnsNull()
    {
        var menuId = Ulid.NewUlid();
        var http = CreateHttpClient(HttpStatusCode.InternalServerError);
        var provider = new HttpMenuDataProvider(http, CreateLogger());

        var result = await provider.GetMenuSnapshotAsync(menuId, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetMenuSnapshotAsync_PreservesDoNotTranslateFlags()
    {
        var menuId = Ulid.NewUlid();
        var sectionId = Ulid.NewUlid();
        var itemId = Ulid.NewUlid();

        var responseJson = JsonSerializer.Serialize(new
        {
            id = menuId.ToString(),
            name = "Menu",
            sections = new[]
            {
                new
                {
                    id = sectionId.ToString(),
                    name = "Section",
                    doNotTranslateName = true,
                    doNotTranslateDescription = true,
                    items = new[]
                    {
                        new
                        {
                            id = itemId.ToString(),
                            name = "Item",
                            description = "Desc",
                            doNotTranslateName = true,
                            doNotTranslateDescription = false,
                            subItems = System.Array.Empty<object>()
                        }
                    }
                }
            }
        });

        var http = CreateHttpClient(HttpStatusCode.OK, responseJson);
        var provider = new HttpMenuDataProvider(http, CreateLogger());

        var result = await provider.GetMenuSnapshotAsync(menuId, CancellationToken.None);

        result!.Sections[0].DoNotTranslateName.Should().BeTrue();
        result.Sections[0].DoNotTranslateDescription.Should().BeTrue();
        result.Sections[0].Items[0].DoNotTranslateName.Should().BeTrue();
        result.Sections[0].Items[0].DoNotTranslateDescription.Should().BeFalse();
    }

    [Fact]
    public async Task GetMenuSnapshotAsync_MultipleSections_MapsAll()
    {
        var menuId = Ulid.NewUlid();
        var responseJson = JsonSerializer.Serialize(new
        {
            id = menuId.ToString(),
            name = "Menu",
            sections = new[]
            {
                new { id = Ulid.NewUlid().ToString(), name = "Section1", doNotTranslateName = false, doNotTranslateDescription = false, items = System.Array.Empty<object>() },
                new { id = Ulid.NewUlid().ToString(), name = "Section2", doNotTranslateName = false, doNotTranslateDescription = false, items = System.Array.Empty<object>() },
                new { id = Ulid.NewUlid().ToString(), name = "Section3", doNotTranslateName = false, doNotTranslateDescription = false, items = System.Array.Empty<object>() }
            }
        });

        var http = CreateHttpClient(HttpStatusCode.OK, responseJson);
        var provider = new HttpMenuDataProvider(http, CreateLogger());

        var result = await provider.GetMenuSnapshotAsync(menuId, CancellationToken.None);

        result!.Sections.Should().HaveCount(3);
        result.Sections.Select(s => s.Name).Should().ContainInOrder("Section1", "Section2", "Section3");
    }

    [Fact]
    public async Task GetMenuSnapshotAsync_MalformedJson_ReturnsNull()
    {
        var menuId = Ulid.NewUlid();
        var http = CreateHttpClient(HttpStatusCode.OK, "{invalid}");
        var provider = new HttpMenuDataProvider(http, CreateLogger());

        var act = () => provider.GetMenuSnapshotAsync(menuId, CancellationToken.None);
        await act.Should().ThrowAsync<JsonException>();
    }

    [Fact]
    public async Task GetMenuSnapshotAsync_EmptyBody_ThrowsJsonException()
    {
        var menuId = Ulid.NewUlid();
        var http = CreateHttpClient(HttpStatusCode.OK, "");
        var provider = new HttpMenuDataProvider(http, CreateLogger());

        var act = () => provider.GetMenuSnapshotAsync(menuId, CancellationToken.None);
        await act.Should().ThrowAsync<JsonException>();
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string? _responseBody;

        public FakeHttpMessageHandler(HttpStatusCode statusCode, string? responseBody)
        {
            _statusCode = statusCode;
            _responseBody = responseBody;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_statusCode);
            if (_responseBody is not null)
            {
                response.Content = new StringContent(_responseBody);
                response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            }
            return Task.FromResult(response);
        }
    }
}
