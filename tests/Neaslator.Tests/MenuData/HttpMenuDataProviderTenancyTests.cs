using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Neaslator.Infrastructure.MenuData;
using NSubstitute;

namespace Neaslator.Tests.MenuData;

/// <summary>
/// Pins how the tenant reaches menu-service, and which endpoint is read.
/// </summary>
/// <remarks>
/// <para>
/// The <c>ownerId</c> parameter is not decoration — menu-service scopes every read by the tenant
/// headers, so dropping them is not "a missing header", it is a cross-tenant read or an empty
/// result depending on which side fails first. And it travels <b>per request</b> rather than on
/// the <see cref="HttpClient"/>'s default headers on purpose: one neaslator instance translates
/// menus for many venues concurrently, so a default header set at construction is a race that
/// hands one venue's menu to another venue's translation job.
/// </para>
/// <para>
/// The endpoint choice is equally load-bearing. The public projection is trimmed for the browser
/// budget and omits <c>doNotTranslateName</c>/<c>doNotTranslateDescription</c>, so reading it made
/// both flags deserialize as <c>false</c> and text an author had explicitly excluded got
/// translated anyway. It also only serves published menus, while a translation is usually
/// requested against a draft.
/// </para>
/// </remarks>
public sealed class HttpMenuDataProviderTenancyTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"smartMenuDto":{"sections":[]}}"""),
            };
            response.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            return Task.FromResult(response);
        }
    }

    private static (HttpMenuDataProvider Provider, CapturingHandler Handler) Build()
    {
        var handler = new CapturingHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://menu.test.local") };
        return (new HttpMenuDataProvider(http, Substitute.For<ILogger<HttpMenuDataProvider>>()), handler);
    }

    private static string? Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    [Fact]
    public async Task The_owner_is_sent_as_both_tenant_headers()
    {
        var (provider, handler) = Build();
        var ownerId = Ulid.NewUlid();

        await provider.GetMenuSnapshotAsync(Ulid.NewUlid(), ownerId, CancellationToken.None);

        Header(handler.Last!, "X-Venue-Id").Should().Be(ownerId.ToString());
        Header(handler.Last!, "X-Tenant-Id").Should().Be(ownerId.ToString());
    }

    [Fact]
    public async Task The_tenant_headers_are_per_request_not_client_defaults()
    {
        // Two menus for two different venues through the same provider instance. If the tenant
        // were set on the client, the second call would inherit or collide with the first — and
        // the failure would be a venue receiving another venue's menu, not an error.
        var (provider, handler) = Build();

        var first = Ulid.NewUlid();
        await provider.GetMenuSnapshotAsync(Ulid.NewUlid(), first, CancellationToken.None);
        Header(handler.Last!, "X-Tenant-Id").Should().Be(first.ToString());

        var second = Ulid.NewUlid();
        await provider.GetMenuSnapshotAsync(Ulid.NewUlid(), second, CancellationToken.None);
        Header(handler.Last!, "X-Tenant-Id").Should().Be(second.ToString(),
            "the second request must carry its own tenant, not the first one's");
    }

    [Fact]
    public async Task The_editor_endpoint_is_read_not_the_public_projection()
    {
        // Reading the public route would silently lose the do-not-translate flags and would only
        // ever see published menus. Both failures are invisible at the call site.
        var (provider, handler) = Build();
        var menuId = Ulid.NewUlid();

        await provider.GetMenuSnapshotAsync(menuId, Ulid.NewUlid(), CancellationToken.None);

        handler.Last!.RequestUri!.AbsolutePath
            .Should().Be($"/api/v1/editor/smartmenu/{menuId}");
    }

    [Fact]
    public async Task The_menu_id_addresses_the_resource_and_the_owner_never_does()
    {
        // If the owner ever leaked into the path, a caller could read across tenants by editing a
        // URL — the headers are the boundary, and the path must not be a second, weaker one.
        var (provider, handler) = Build();
        var ownerId = Ulid.NewUlid();

        await provider.GetMenuSnapshotAsync(Ulid.NewUlid(), ownerId, CancellationToken.None);

        handler.Last!.RequestUri!.ToString().Should().NotContain(ownerId.ToString());
    }
}
