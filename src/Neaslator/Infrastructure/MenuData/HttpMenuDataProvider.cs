using Neaslator.Infrastructure.Diff;

namespace Neaslator.Infrastructure.MenuData;

public sealed class HttpMenuDataProvider : IMenuDataProvider
{
    private readonly HttpClient _http;
    private readonly ILogger<HttpMenuDataProvider> _logger;

    public HttpMenuDataProvider(HttpClient httpClient, ILogger<HttpMenuDataProvider> logger)
    {
        _http = httpClient;
        _logger = logger;
    }

    public async Task<MenuSnapshot?> GetMenuSnapshotAsync(
        Ulid menuId, Ulid ownerId, Ulid? tenantId, CancellationToken ct)
    {
        // The EDITOR endpoint, not the public one.
        //
        // The public projection is deliberately minimal for the 14.3 KB browser budget and
        // does not carry doNotTranslateName/doNotTranslateDescription. Reading it here made
        // every flag deserialize as false, so text the author had explicitly excluded would
        // have been translated anyway. It also only serves *published* menus, while a
        // translation request is most often made against a draft.
        // The tenant travels per request, not on the shared HttpClient: one neaslator instance
        // translates menus for many venues concurrently, so a default header would be a race.
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/editor/smartmenu/{menuId}");
        // X-Venue-Id is the owner: the venue or event the menu hangs off.
        request.Headers.Add("X-Venue-Id", ownerId.ToString());

        // X-Tenant-Id is the ORGANISATION, which is a different thing and used to be sent as the
        // owner. That was correct only while every menu in the estate had owner_id == tenant_id —
        // all 467 of them did. The first menu that genuinely belonged to a venue inside an
        // organisation made this header name a tenant that owns nothing: menu-service's query filter
        // matched no rows, the fetch answered 404, and translation stopped with "Failed to retrieve
        // menu snapshot" long after the caller had been told 202.
        //
        // Falls back to the owner when the publisher has not been redeployed and omits the tenant,
        // which is exactly the previous behaviour rather than a hard failure.
        request.Headers.Add("X-Tenant-Id", (tenantId ?? ownerId).ToString());

        HttpResponseMessage response = await _http.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Failed to fetch menu {MenuId} from menu service: {Status}", menuId, response.StatusCode);
            return null;
        }

        EditorMenuEnvelope? envelope = await response.Content.ReadFromJsonAsync<EditorMenuEnvelope>(ct);
        MenuServiceResponse? menuData = envelope?.SmartMenuDto;
        if (menuData is null)
            return null;

        return new MenuSnapshot
        {
            // The menu's own title and description. Everything below this was already carried; these
            // four lines are the whole reason a menu's title was identical in all twenty-nine
            // languages while its sections were correctly translated.
            Name = menuData.Name,
            Description = menuData.Description,
            DoNotTranslateName = menuData.DoNotTranslateName,
            DoNotTranslateDescription = menuData.DoNotTranslateDescription,

            // Collections are null-coalesced: System.Text.Json overwrites the record's
            // default initializer when the JSON contains an explicit null, so an upstream
            // "sections": null (or null items/subItems) must not crash the consumer.
            Sections = (menuData.Sections ?? []).Select(s => new SectionSnapshot
            {
                Id = s.Id,
                Name = s.Name,
                DoNotTranslateName = s.DoNotTranslateName,
                DoNotTranslateDescription = s.DoNotTranslateDescription,
                Items = (s.Items ?? []).Select(i => new ItemSnapshot
                {
                    Id = i.Id,
                    Name = i.Name,
                    Description = i.Description,
                    DoNotTranslateName = i.DoNotTranslateName,
                    DoNotTranslateDescription = i.DoNotTranslateDescription,
                    SubItems = (i.SubItems ?? []).Select(si => new SubItemSnapshot
                    {
                        Id = si.Id,
                        Name = si.Name,
                        Description = si.Description,
                        DoNotTranslateName = si.DoNotTranslateName,
                        DoNotTranslateDescription = si.DoNotTranslateDescription
                    }).ToList()
                }).ToList()
            }).ToList()
        };
    }
}
