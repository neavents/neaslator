using Bogus;
using Neaslator.Domain.Entities;

namespace Neaslator.Tests.Shared.Fakers;

public sealed class SupportedLanguageFaker : Faker<SupportedLanguage>
{
    private static readonly (string Code, string EnglishName, string NativeName)[] LanguageData =
    [
        ("en", "English", "English"),
        ("tr", "Turkish", "Türkçe"),
        ("de", "German", "Deutsch"),
        ("fr", "French", "Français"),
        ("es", "Spanish", "Español"),
        ("ja", "Japanese", "日本語"),
        ("ar", "Arabic", "العربية"),
        ("zh", "Chinese", "中文"),
        ("ru", "Russian", "Русский"),
        ("it", "Italian", "Italiano"),
    ];

    private int _index = 0;

    public SupportedLanguageFaker()
    {
        RuleFor(l => l.Code, _ =>
        {
            var (code, _, _) = LanguageData[_index % LanguageData.Length];
            _index++;
            return code;
        });
        RuleFor(l => l.EnglishName, _ =>
        {
            var (_, english, _) = LanguageData[(_index - 1) % LanguageData.Length];
            return english;
        });
        RuleFor(l => l.NativeName, _ =>
        {
            var (_, _, native) = LanguageData[(_index - 1) % LanguageData.Length];
            return native;
        });
        RuleFor(l => l.IsActive, true);
        RuleFor(l => l.SortOrder, f => (short)((_index - 1) % LanguageData.Length));
    }

    public SupportedLanguageFaker Inactive()
    {
        RuleFor(l => l.IsActive, false);
        return this;
    }
}
