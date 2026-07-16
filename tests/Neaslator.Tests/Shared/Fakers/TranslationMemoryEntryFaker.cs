using Bogus;
using Neaslator.Domain.Entities;
using Neaslator.Domain.Enums;

namespace Neaslator.Tests.Shared.Fakers;

public sealed class TranslationMemoryEntryFaker : Faker<TranslationMemoryEntry>
{
    public TranslationMemoryEntryFaker()
    {
        RuleFor(e => e.Id, f => f.Random.Long(1, long.MaxValue));
        RuleFor(e => e.SourceHash, f => f.Random.Long(1, long.MaxValue));
        RuleFor(e => e.NormalizedSourceText, f => f.Lorem.Sentence(3));
        RuleFor(e => e.SourceLanguageCode, f => f.PickRandom("en", "tr", "de", "fr", "es", "ja", "ar"));
        RuleFor(e => e.TargetLanguageCode, f => f.PickRandom("en", "tr", "de", "fr", "es", "ja", "ar"));
        RuleFor(e => e.TranslatedText, f => f.Lorem.Sentence(4));
        RuleFor(e => e.ProviderTier, f => f.PickRandom<TranslationProviderTier>());
        RuleFor(e => e.ProviderName, f => f.PickRandom("deepseek", "openai", "anthropic", "google"));
        RuleFor(e => e.ConfidenceScore, f => MathF.Round(f.Random.Float(0.5f, 1.0f), 4));
        RuleFor(e => e.CreatedAt, f => f.Date.RecentOffset(30));
        RuleFor(e => e.UpdatedAt, (f, e) => e.CreatedAt.AddHours(f.Random.Double(0, 24)));
        RuleFor(e => e.HitCount, f => f.Random.Long(0, 10000));

        // Ensure source/target are different languages
        FinishWith((f, e) =>
        {
            if (e.SourceLanguageCode == e.TargetLanguageCode)
            {
                e.TargetLanguageCode = f.PickRandom(new[] { "en", "tr", "de", "fr", "es", "ja", "ar" }
                    .Where(c => c != e.SourceLanguageCode).ToArray());
            }
        });
    }

    public TranslationMemoryEntryFaker WithProviderTier(TranslationProviderTier tier)
    {
        RuleFor(e => e.ProviderTier, tier);
        return this;
    }

    public TranslationMemoryEntryFaker WithLanguages(string source, string target)
    {
        RuleFor(e => e.SourceLanguageCode, source);
        RuleFor(e => e.TargetLanguageCode, target);
        return this;
    }
}
