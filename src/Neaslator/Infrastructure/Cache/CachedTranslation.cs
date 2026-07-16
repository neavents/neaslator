using Neaslator.Domain.Enums;

namespace Neaslator.Infrastructure.Cache;

public sealed record CachedTranslation(
    string TranslatedText,
    TranslationProviderTier ProviderTier,
    float ConfidenceScore,
    string NormalizedSourceText);
