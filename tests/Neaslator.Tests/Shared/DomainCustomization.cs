using AutoFixture;
using Neaslator.Tests.Shared.Fakers;

namespace Neaslator.Tests.Shared;

public sealed class DomainCustomization : ICustomization
{
    public void Customize(IFixture fixture)
    {
        fixture.Customizations.Add(new UlidSpecimenBuilder());
        fixture.Customizations.Add(new StringLengthSpecimenBuilder(maxLength: 200));

        fixture.Register(() => new TranslationMemoryEntryFaker().Generate());
        fixture.Register(() => new SupportedLanguageFaker().Generate());
        fixture.Register(() => new MenuPublishSnapshotFaker().Generate());
    }
}
