using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Neaslator.Domain.Entities;

namespace Neaslator.Persistence.Configurations;

public sealed class SupportedLanguageConfiguration : IEntityTypeConfiguration<SupportedLanguage>
{
    public void Configure(EntityTypeBuilder<SupportedLanguage> builder)
    {
        builder.ToTable("supported_languages");
        builder.HasKey(e => e.Code);
        builder.Property(e => e.Code).HasMaxLength(10);
        builder.Property(e => e.EnglishName).HasMaxLength(128).IsRequired();
        builder.Property(e => e.NativeName).HasMaxLength(128).IsRequired();
        builder.Property(e => e.IsActive).HasDefaultValue(true);
        builder.Property(e => e.SortOrder).HasDefaultValue((short)0);

        builder.HasData(
            new SupportedLanguage { Code = "en", EnglishName = "English", NativeName = "English", IsActive = true, SortOrder = 1 },
            new SupportedLanguage { Code = "tr", EnglishName = "Turkish", NativeName = "Türkçe", IsActive = true, SortOrder = 2 },
            new SupportedLanguage { Code = "de", EnglishName = "German", NativeName = "Deutsch", IsActive = true, SortOrder = 3 },
            new SupportedLanguage { Code = "fr", EnglishName = "French", NativeName = "Français", IsActive = true, SortOrder = 4 },
            new SupportedLanguage { Code = "es", EnglishName = "Spanish", NativeName = "Español", IsActive = true, SortOrder = 5 },
            new SupportedLanguage { Code = "it", EnglishName = "Italian", NativeName = "Italiano", IsActive = true, SortOrder = 6 },
            new SupportedLanguage { Code = "pt", EnglishName = "Portuguese", NativeName = "Português", IsActive = true, SortOrder = 7 },
            new SupportedLanguage { Code = "nl", EnglishName = "Dutch", NativeName = "Nederlands", IsActive = true, SortOrder = 8 },
            new SupportedLanguage { Code = "ru", EnglishName = "Russian", NativeName = "Русский", IsActive = true, SortOrder = 9 },
            new SupportedLanguage { Code = "ar", EnglishName = "Arabic", NativeName = "العربية", IsActive = true, SortOrder = 10 },
            new SupportedLanguage { Code = "zh", EnglishName = "Chinese", NativeName = "中文", IsActive = true, SortOrder = 11 },
            new SupportedLanguage { Code = "ja", EnglishName = "Japanese", NativeName = "日本語", IsActive = true, SortOrder = 12 },
            new SupportedLanguage { Code = "ko", EnglishName = "Korean", NativeName = "한국어", IsActive = true, SortOrder = 13 },
            new SupportedLanguage { Code = "pl", EnglishName = "Polish", NativeName = "Polski", IsActive = true, SortOrder = 14 },
            new SupportedLanguage { Code = "sv", EnglishName = "Swedish", NativeName = "Svenska", IsActive = true, SortOrder = 15 },
            new SupportedLanguage { Code = "da", EnglishName = "Danish", NativeName = "Dansk", IsActive = true, SortOrder = 16 },
            new SupportedLanguage { Code = "no", EnglishName = "Norwegian", NativeName = "Norsk", IsActive = true, SortOrder = 17 },
            new SupportedLanguage { Code = "fi", EnglishName = "Finnish", NativeName = "Suomi", IsActive = true, SortOrder = 18 },
            new SupportedLanguage { Code = "el", EnglishName = "Greek", NativeName = "Ελληνικά", IsActive = true, SortOrder = 19 },
            new SupportedLanguage { Code = "cs", EnglishName = "Czech", NativeName = "Čeština", IsActive = true, SortOrder = 20 },
            new SupportedLanguage { Code = "hu", EnglishName = "Hungarian", NativeName = "Magyar", IsActive = true, SortOrder = 21 },
            new SupportedLanguage { Code = "ro", EnglishName = "Romanian", NativeName = "Română", IsActive = true, SortOrder = 22 },
            new SupportedLanguage { Code = "bg", EnglishName = "Bulgarian", NativeName = "Български", IsActive = true, SortOrder = 23 },
            new SupportedLanguage { Code = "uk", EnglishName = "Ukrainian", NativeName = "Українська", IsActive = true, SortOrder = 24 },
            new SupportedLanguage { Code = "he", EnglishName = "Hebrew", NativeName = "עברית", IsActive = true, SortOrder = 25 },
            new SupportedLanguage { Code = "th", EnglishName = "Thai", NativeName = "ไทย", IsActive = true, SortOrder = 26 },
            new SupportedLanguage { Code = "vi", EnglishName = "Vietnamese", NativeName = "Tiếng Việt", IsActive = true, SortOrder = 27 },
            new SupportedLanguage { Code = "id", EnglishName = "Indonesian", NativeName = "Bahasa Indonesia", IsActive = true, SortOrder = 28 },
            new SupportedLanguage { Code = "ms", EnglishName = "Malay", NativeName = "Bahasa Melayu", IsActive = true, SortOrder = 29 },
            new SupportedLanguage { Code = "hi", EnglishName = "Hindi", NativeName = "हिन्दी", IsActive = true, SortOrder = 30 }
        );
    }
}
