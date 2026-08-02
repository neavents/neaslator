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

    /// <summary>
    /// Whether asking the same provider again is worth doing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// True for everything caused by the model's output rather than by the request: a response in an
    /// envelope we could not read, the wrong number of items, a hash that was not echoed back, an
    /// empty name. None of those are deterministic — measured live, the same call succeeded about
    /// 92% of the time — so a single failure was never evidence that the text could not be
    /// translated, only that this particular generation came out wrong.
    /// </para>
    /// <para>
    /// It matters because there is one provider. With no second provider to fall through to, a
    /// non-retried failure is final for that text in that language: it fails identically on every
    /// subsequent run, and the router reports it as the whole chain being exhausted.
    /// </para>
    /// <para>
    /// Defaults to false so a new failure path has to opt in. A retry loop on something a retry
    /// cannot fix costs money and hides the real error.
    /// </para>
    /// </remarks>
    public bool IsRetryable { get; init; }
}

public sealed record TranslatedUnit
{
    public required long SourceHash { get; init; }
    public required string TranslatedName { get; init; }
    public string? TranslatedDescription { get; init; }
    public float ConfidenceScore { get; init; } = 1.0f;
}

public sealed record TokenUsage(int InputTokens, int OutputTokens, int CachedTokens);
