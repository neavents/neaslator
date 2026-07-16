using Neaslator.Domain.Enums;

namespace Neaslator.Infrastructure.Cache;

public interface ITranslationCache
{
    Task<IReadOnlyList<CacheLookupResult>> LookupAsync(
        long sourceHash,
        string normalizedSourceText,
        string sourceLanguageCode,
        IReadOnlyList<string> targetLanguageCodes,
        CancellationToken cancellationToken);

    Task StoreAsync(
        long sourceHash,
        string normalizedSourceText,
        string sourceLanguageCode,
        string targetLanguageCode,
        string translatedText,
        TranslationProviderTier providerTier,
        string providerName,
        float confidenceScore,
        CancellationToken cancellationToken);

    Task InvalidateAsync(long sourceHash, string targetLanguageCode);
}
