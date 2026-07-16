using Neaslator.Domain.Enums;

namespace Neaslator.Infrastructure.Providers;

public interface ITranslationProvider
{
    string ProviderName { get; }
    TranslationProviderTier Tier { get; }
    bool SupportsPrefixCaching { get; }
    int MaxBatchSize { get; }
    int MaxConcurrentRequests { get; }
    Task<TranslationBatchResult> TranslateBatchAsync(TranslationBatchRequest request, CancellationToken cancellationToken);
    Task<bool> IsHealthyAsync(CancellationToken cancellationToken);
}
