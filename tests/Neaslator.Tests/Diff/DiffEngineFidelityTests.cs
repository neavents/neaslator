using FluentAssertions;
using Neaslator.Domain.Enums;
using Neaslator.Infrastructure.Diff;
using Neaslator.Infrastructure.Hashing;
using Neaslator.Infrastructure.Normalization;

namespace Neaslator.Tests.Diff;

/// <summary>
/// Change-detection fidelity: the diff must catch every meaningful edit (or a stale translation
/// ships) and ignore every meaning-preserving one (or the LLM is billed for nothing). These
/// probe the subtle cases — case, Unicode equivalence, swaps, duplicates, and cross-section
/// moves — where a naive comparison would either lose data or waste work.
/// </summary>
public sealed class DiffEngineFidelityTests
{
    private static readonly Ulid SecA = Ulid.NewUlid();
    private static readonly Ulid SecB = Ulid.NewUlid();
    private static readonly Ulid Item1 = Ulid.NewUlid();
    private static readonly Ulid Item2 = Ulid.NewUlid();

    private static MenuSnapshot Menu(params SectionSnapshot[] sections) => new() { Sections = sections };

    private static SectionSnapshot Section(Ulid id, string name, params ItemSnapshot[] items) =>
        new() { Id = id, Name = name, Items = items };

    private static ItemSnapshot Item(Ulid id, string name) => new() { Id = id, Name = name };

    [Fact]
    public void CaseOnlyChange_IsDetected()
    {
        // "soup" and "Soup" are genuinely different strings; a case edit is a real change.
        MenuSnapshot previous = Menu(Section(SecA, "S", Item(Item1, "soup")));
        MenuSnapshot current = Menu(Section(SecA, "S", Item(Item1, "Soup")));

        IReadOnlyList<TranslationUnit> diff = DiffEngine.ComputeDiff(current, previous);

        diff.Should().ContainSingle(u => u.UnitType == TranslationUnitType.ItemName && u.ItemId == Item1);
        diff.Single(u => u.ItemId == Item1).NormalizedSourceText.Should().Be("Soup");
    }

    [Fact]
    public void UnicodeNfcEquivalentChange_IsNotDetected()
    {
        // Composed é (U+00E9) vs decomposed e + combining acute (U+0065 U+0301): visually and
        // semantically identical after NFC, so re-translating would be pure waste.
        MenuSnapshot previous = Menu(Section(SecA, "S", Item(Item1, "café")));
        MenuSnapshot current = Menu(Section(SecA, "S", Item(Item1, "café")));

        DiffEngine.ComputeDiff(current, previous).Should().BeEmpty();
    }

    [Fact]
    public void DiacriticChange_IsDetected()
    {
        // "a" vs "á" is a real content difference and must be caught.
        MenuSnapshot previous = Menu(Section(SecA, "S", Item(Item1, "Mate")));
        MenuSnapshot current = Menu(Section(SecA, "S", Item(Item1, "Maté")));

        DiffEngine.ComputeDiff(current, previous).Should().ContainSingle(u => u.ItemId == Item1);
    }

    [Fact]
    public void TwoItemsSwapNames_BothDetected()
    {
        MenuSnapshot previous = Menu(Section(SecA, "S",
            Item(Item1, "Soup"), Item(Item2, "Salad")));
        MenuSnapshot current = Menu(Section(SecA, "S",
            Item(Item1, "Salad"), Item(Item2, "Soup")));

        IReadOnlyList<TranslationUnit> diff = DiffEngine.ComputeDiff(current, previous);

        diff.Where(u => u.UnitType == TranslationUnitType.ItemName).Should().HaveCount(2);
        diff.Should().Contain(u => u.ItemId == Item1 && u.NormalizedSourceText == "Salad");
        diff.Should().Contain(u => u.ItemId == Item2 && u.NormalizedSourceText == "Soup");
    }

    [Fact]
    public void DuplicateIdenticalItems_ProduceTwoUnitsSharingAHash()
    {
        MenuSnapshot current = Menu(Section(SecA, "S",
            Item(Item1, "Soup"), Item(Item2, "Soup")));

        IReadOnlyList<TranslationUnit> diff = DiffEngine.ComputeDiff(current, null);

        List<TranslationUnit> itemUnits = diff.Where(u => u.UnitType == TranslationUnitType.ItemName).ToList();
        itemUnits.Should().HaveCount(2);
        itemUnits.Select(u => u.ItemId).Should().BeEquivalentTo([Item1, Item2]);
        long expected = TranslationHasher.ComputeHash(TextNormalizer.Normalize("Soup".AsSpan()));
        itemUnits.Should().OnlyContain(u => u.SourceHash == expected,
            "identical text hashes identically so both resolve from the same cache entry");
    }

    [Fact]
    public void ItemMovedBetweenSections_ReemittedUnderNewSection()
    {
        MenuSnapshot previous = Menu(
            Section(SecA, "A", Item(Item1, "Soup")),
            Section(SecB, "B"));
        MenuSnapshot current = Menu(
            Section(SecA, "A"),
            Section(SecB, "B", Item(Item1, "Soup")));

        IReadOnlyList<TranslationUnit> diff = DiffEngine.ComputeDiff(current, previous);

        diff.Should().ContainSingle(u => u.UnitType == TranslationUnitType.ItemName && u.ItemId == Item1);
        diff.Single(u => u.ItemId == Item1).ParentSectionId.Should().Be(SecB,
            "a moved item must be re-emitted under its new parent section");
    }

    [Fact]
    public void SectionReplacedWithNewId_SameText_ReemittedNotLost()
    {
        Ulid oldSection = Ulid.NewUlid();
        Ulid newSection = Ulid.NewUlid();
        MenuSnapshot previous = Menu(Section(oldSection, "Starters", Item(Item1, "Soup")));
        MenuSnapshot current = Menu(Section(newSection, "Starters", Item(Item2, "Soup")));

        IReadOnlyList<TranslationUnit> diff = DiffEngine.ComputeDiff(current, previous);

        // New section + its item are re-emitted (they resolve from cache by hash, so no real
        // re-translation), and nothing from the old section leaks in.
        diff.Should().Contain(u => u.UnitType == TranslationUnitType.SectionName && u.ParentSectionId == newSection);
        diff.Should().Contain(u => u.UnitType == TranslationUnitType.ItemName && u.ItemId == Item2);
        diff.Should().OnlyContain(u => u.ParentSectionId == newSection);
    }

    [Fact]
    public void TrailingWhitespaceOnlyChange_IsNotDetected()
    {
        MenuSnapshot previous = Menu(Section(SecA, "S", Item(Item1, "Grilled Chicken")));
        MenuSnapshot current = Menu(Section(SecA, "S", Item(Item1, "Grilled Chicken   ")));

        DiffEngine.ComputeDiff(current, previous).Should().BeEmpty();
    }
}
