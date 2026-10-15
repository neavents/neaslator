using Neaslator.Infrastructure.LanguagePolicy;

namespace Neaslator.Tests.Shared;

public sealed class FixedLanguagePolicy(TranslationLanguagePolicy policy) : ITranslationLanguagePolicySource
{
    public static FixedLanguagePolicy Unrestricted { get; } = new(TranslationLanguagePolicy.Unrestricted);

    public Task<TranslationLanguagePolicy> GetAsync(Ulid ownerId, CancellationToken cancellationToken) => Task.FromResult(policy);

    public Task ForgetOrganisationAsync(Ulid orgId, CancellationToken cancellationToken) => Task.CompletedTask;
}
