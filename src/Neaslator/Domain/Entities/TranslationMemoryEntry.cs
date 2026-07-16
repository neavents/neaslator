using Neaslator.Domain.Enums;

namespace Neaslator.Domain.Entities;

public sealed class TranslationMemoryEntry
{
    public long Id { get; set; }
    public long SourceHash { get; set; }
    public string NormalizedSourceText { get; set; } = default!;
    public string SourceLanguageCode { get; set; } = default!;
    public string TargetLanguageCode { get; set; } = default!;
    public string TranslatedText { get; set; } = default!;
    public TranslationProviderTier ProviderTier { get; set; }
    public string ProviderName { get; set; } = default!;
    public float ConfidenceScore { get; set; } = 1.0f;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long HitCount { get; set; }
}
