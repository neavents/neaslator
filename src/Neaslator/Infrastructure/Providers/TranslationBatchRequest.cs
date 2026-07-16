namespace Neaslator.Infrastructure.Providers;

public sealed record TranslationBatchRequest
{
    public required string SourceLanguageCode { get; init; }
    public required string TargetLanguageCode { get; init; }
    public required string VenueType { get; init; }
    public required string CuisineType { get; init; }
    public required string SectionName { get; init; }
    public required IReadOnlyList<TranslationBatchItem> Items { get; init; }
    public bool IsVanguardRequest { get; init; }
}

public sealed record TranslationBatchItem
{
    public required long SourceHash { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
}
