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

    public async Task<MenuSnapshot?> GetMenuSnapshotAsync(Ulid menuId, CancellationToken ct)
    {
        HttpResponseMessage response = await _http.GetAsync($"/api/v1/smartmenu/{menuId}", ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Failed to fetch menu {MenuId} from menu service: {Status}", menuId, response.StatusCode);
            return null;
        }

        MenuServiceResponse? menuData = await response.Content.ReadFromJsonAsync<MenuServiceResponse>(ct);
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
