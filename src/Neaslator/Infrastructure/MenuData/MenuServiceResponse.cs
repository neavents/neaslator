using System.Text.Json.Serialization;

namespace Neaslator.Infrastructure.MenuData;

/// <summary>
/// The editor endpoint wraps the menu in { smartMenuDto, detailsDto }. Only the first is
/// needed here; detailsDto is audit/hyper metadata.
/// </summary>
internal sealed record EditorMenuEnvelope
{
    [JsonPropertyName("smartMenuDto")]
    public MenuServiceResponse? SmartMenuDto { get; init; }
}

internal sealed record MenuServiceResponse
{
    // The editor DTO names this smartMenuId; sections/items/sub-items use plain "id".
    [JsonPropertyName("smartMenuId")]
    public Ulid Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("sections")]
    public IReadOnlyList<MenuSectionResponse> Sections { get; init; } = [];
}

internal sealed record MenuSectionResponse
{
    [JsonPropertyName("id")]
    public Ulid Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("doNotTranslateName")]
    public bool DoNotTranslateName { get; init; }

    [JsonPropertyName("doNotTranslateDescription")]
    public bool DoNotTranslateDescription { get; init; }

    [JsonPropertyName("items")]
    public IReadOnlyList<MenuItemResponse> Items { get; init; } = [];
}

internal sealed record MenuItemResponse
{
    [JsonPropertyName("id")]
    public Ulid Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("doNotTranslateName")]
    public bool DoNotTranslateName { get; init; }

    [JsonPropertyName("doNotTranslateDescription")]
    public bool DoNotTranslateDescription { get; init; }

    [JsonPropertyName("subItems")]
    public IReadOnlyList<MenuSubItemResponse> SubItems { get; init; } = [];
}

internal sealed record MenuSubItemResponse
{
    [JsonPropertyName("id")]
    public Ulid Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("doNotTranslateName")]
    public bool DoNotTranslateName { get; init; }

    [JsonPropertyName("doNotTranslateDescription")]
    public bool DoNotTranslateDescription { get; init; }
}
