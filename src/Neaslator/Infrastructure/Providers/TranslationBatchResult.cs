using Neaslator.Domain.Enums;

namespace Neaslator.Infrastructure.Providers;

public sealed record TranslationBatchResult
{
    public required bool IsSuccess { get; init; }
    public required IReadOnlyList<TranslatedUnit> Translations { get; init; }
    public required TokenUsage TokenUsage { get; init; }
    public string? ErrorMessage { get; init; }
    public TimeSpan Latency { get; init; }
    public string ProviderName { get; init; } = "";
    public TranslationProviderTier ProviderTier { get; init; }
}

public sealed record TranslatedUnit
{
    public required long SourceHash { get; init; }
    public required string TranslatedName { get; init; }
    public string? TranslatedDescription { get; init; }
    public float ConfidenceScore { get; init; } = 1.0f;
}

public sealed record TokenUsage(int InputTokens, int OutputTokens, int CachedTokens);
