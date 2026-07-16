using System.Text.Json.Serialization;

namespace Neaslator.Infrastructure.MenuData;

internal sealed record MenuServiceResponse
{
    [JsonPropertyName("id")]
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
