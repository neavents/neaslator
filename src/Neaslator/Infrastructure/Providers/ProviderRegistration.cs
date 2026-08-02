using Polly;

namespace Neaslator.Infrastructure.Providers;

public sealed class ProviderRegistration
{
    public required ITranslationProvider Provider { get; init; }

    /// <summary>
    /// The resilience pipeline wrapping this provider.
    /// </summary>
    /// <remarks>
    /// <b>Typed on purpose.</b> It was a bare <see cref="ResiliencePipeline"/>, whose retry only sees
    /// thrown exceptions — and <c>TranslateBatchAsync</c> never throws for the failure that actually
    /// happens. A malformed model response is <i>returned</i> as <c>IsSuccess = false</c>, so the
    /// retry sat there looking configured and covering nothing. Typing the pipeline on
    /// <see cref="TranslationBatchResult"/> is what lets <c>ShouldHandle</c> inspect the outcome and
    /// retry a response that a second attempt would very likely get right.
    /// </remarks>
    public required ResiliencePipeline<TranslationBatchResult> Pipeline { get; init; }

    public bool IsAvailable { get; set; } = true;
}
