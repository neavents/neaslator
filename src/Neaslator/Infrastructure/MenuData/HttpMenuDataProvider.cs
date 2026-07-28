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

    public async Task<MenuSnapshot?> GetMenuSnapshotAsync(Ulid menuId, Ulid ownerId, CancellationToken ct)
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
        request.Headers.Add("X-Venue-Id", ownerId.ToString());
        request.Headers.Add("X-Tenant-Id", ownerId.ToString());

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
