namespace Neaslator.Infrastructure.LanguagePolicy;

/// <summary>The languages one venue's menus may be translated into; null means every active language.</summary>
public sealed record TranslationLanguagePolicy(IReadOnlyCollection<string>? Allowed)
{
    public static readonly TranslationLanguagePolicy Unrestricted = new((IReadOnlyCollection<string>?)null);

    public bool Permits(string languageCode)
        => Allowed is null || Allowed.Contains(languageCode, StringComparer.OrdinalIgnoreCase);
}

public interface ITranslationLanguagePolicySource
{
    Task<TranslationLanguagePolicy> GetAsync(Ulid ownerId, CancellationToken cancellationToken);

    Task ForgetOrganisationAsync(Ulid orgId, CancellationToken cancellationToken);
}
