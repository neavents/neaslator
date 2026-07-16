using FluentAssertions;
using Neaslator.Domain.Enums;
using Neaslator.Infrastructure.Diff;
using Neaslator.Infrastructure.Normalization;

namespace Neaslator.Tests.Diff;

public sealed class DiffEngineTests
{
    private static readonly Ulid Section1Id = Ulid.NewUlid();
    private static readonly Ulid Section2Id = Ulid.NewUlid();
    private static readonly Ulid Item1Id = Ulid.NewUlid();
    private static readonly Ulid Item2Id = Ulid.NewUlid();
    private static readonly Ulid Item3Id = Ulid.NewUlid();

    [Fact]
    public void NullPreviousSnapshot_ReturnsAllUnits()
    {
        MenuSnapshot current = CreateSnapshot(
            new SectionData(Section1Id, "Starters",
                new ItemData(Item1Id, "Soup", "Tomato soup"),
                new ItemData(Item2Id, "Salad", null)));

        IReadOnlyList<TranslationUnit> result = DiffEngine.ComputeDiff(current, null);

        result.Should().HaveCount(4);
        result.Should().Contain(u => u.UnitType == TranslationUnitType.SectionName);
        result.Where(u => u.UnitType == TranslationUnitType.ItemName).Should().HaveCount(2);
        result.Where(u => u.UnitType == TranslationUnitType.ItemDescription).Should().HaveCount(1);
    }

    [Fact]
    public void IdenticalSnapshots_ReturnsEmptyDiff()
    {
        MenuSnapshot snapshot = CreateSnapshot(
            new SectionData(Section1Id, "Starters",
                new ItemData(Item1Id, "Soup", "Tomato soup")));

        IReadOnlyList<TranslationUnit> result = DiffEngine.ComputeDiff(snapshot, snapshot);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ItemNameChanged_ReturnsOnlyNameUnit()
    {
        MenuSnapshot previous = CreateSnapshot(
            new SectionData(Section1Id, "Starters",
                new ItemData(Item1Id, "Soup", "Tomato soup")));

        MenuSnapshot current = CreateSnapshot(
            new SectionData(Section1Id, "Starters",
                new ItemData(Item1Id, "Broth", "Tomato soup")));

        IReadOnlyList<TranslationUnit> result = DiffEngine.ComputeDiff(current, previous);

        result.Should().HaveCount(1);
        result[0].UnitType.Should().Be(TranslationUnitType.ItemName);
        result[0].NormalizedSourceText.Should().Be("Broth");
        result[0].ItemId.Should().Be(Item1Id);
    }

    [Fact]
    public void ItemDescriptionChanged_ReturnsOnlyDescriptionUnit()
    {
        MenuSnapshot previous = CreateSnapshot(
            new SectionData(Section1Id, "Starters",
                new ItemData(Item1Id, "Soup", "Tomato soup")));

        MenuSnapshot current = CreateSnapshot(
            new SectionData(Section1Id, "Starters",
                new ItemData(Item1Id, "Soup", "Cream of tomato")));

        IReadOnlyList<TranslationUnit> result = DiffEngine.ComputeDiff(current, previous);

        result.Should().HaveCount(1);
        result[0].UnitType.Should().Be(TranslationUnitType.ItemDescription);
        result[0].NormalizedSourceText.Should().Be("Cream of tomato");
    }

    [Fact]
    public void NewSectionAdded_ReturnsAllUnitsForNewSection()
    {
        MenuSnapshot previous = CreateSnapshot(
            new SectionData(Section1Id, "Starters",
                new ItemData(Item1Id, "Soup", "Tomato soup")));

        MenuSnapshot current = CreateSnapshot(
            new SectionData(Section1Id, "Starters",
                new ItemData(Item1Id, "Soup", "Tomato soup")),
            new SectionData(Section2Id, "Mains",
                new ItemData(Item2Id, "Steak", "Grilled beef")));

        IReadOnlyList<TranslationUnit> result = DiffEngine.ComputeDiff(current, previous);

        result.Should().HaveCount(3);
        result.Where(u => u.ParentSectionId == Section2Id).Should().HaveCount(3);
        result.Should().Contain(u => u.UnitType == TranslationUnitType.SectionName && u.ParentSectionId == Section2Id);
        result.Should().Contain(u => u.UnitType == TranslationUnitType.ItemName && u.ItemId == Item2Id);
        result.Should().Contain(u => u.UnitType == TranslationUnitType.ItemDescription && u.ItemId == Item2Id);
    }

    [Fact]
    public void NewItemInExistingSection_ReturnsOnlyNewItemUnits()
    {
        MenuSnapshot previous = CreateSnapshot(
            new SectionData(Section1Id, "Starters",
                new ItemData(Item1Id, "Soup", "Tomato soup")));

        MenuSnapshot current = CreateSnapshot(
            new SectionData(Section1Id, "Starters",
                new ItemData(Item1Id, "Soup", "Tomato soup"),
                new ItemData(Item2Id, "Salad", "Caesar salad")));

        IReadOnlyList<TranslationUnit> result = DiffEngine.ComputeDiff(current, previous);

        result.Should().HaveCount(2);
        result.Should().Contain(u => u.UnitType == TranslationUnitType.ItemName && u.ItemId == Item2Id);
        result.Should().Contain(u => u.UnitType == TranslationUnitType.ItemDescription && u.ItemId == Item2Id);
    }

    [Fact]
    public void SectionNameChanged_ReturnsSectionUnit()
    {
        MenuSnapshot previous = CreateSnapshot(
            new SectionData(Section1Id, "Starters",
                new ItemData(Item1Id, "Soup", "Tomato soup")));

        MenuSnapshot current = CreateSnapshot(
            new SectionData(Section1Id, "Appetizers",
                new ItemData(Item1Id, "Soup", "Tomato soup")));

        IReadOnlyList<TranslationUnit> result = DiffEngine.ComputeDiff(current, previous);

        result.Should().HaveCount(1);
        result[0].UnitType.Should().Be(TranslationUnitType.SectionName);
        result[0].NormalizedSourceText.Should().Be("Appetizers");
        result[0].ItemId.Should().Be(Ulid.Empty);
    }

    [Fact]
    public void ItemReorderedSameText_ReturnsEmptyDiff()
    {
        MenuSnapshot previous = CreateSnapshot(
            new SectionData(Section1Id, "Starters",
                new ItemData(Item1Id, "Soup", null),
                new ItemData(Item2Id, "Salad", null)));

        MenuSnapshot current = CreateSnapshot(
            new SectionData(Section1Id, "Starters",
                new ItemData(Item2Id, "Salad", null),
                new ItemData(Item1Id, "Soup", null)));

        IReadOnlyList<TranslationUnit> result = DiffEngine.ComputeDiff(current, previous);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ItemDeleted_ReturnsNothing()
    {
        MenuSnapshot previous = CreateSnapshot(
            new SectionData(Section1Id, "Starters",
                new ItemData(Item1Id, "Soup", null),
                new ItemData(Item2Id, "Salad", null)));

        MenuSnapshot current = CreateSnapshot(
            new SectionData(Section1Id, "Starters",
                new ItemData(Item1Id, "Soup", null)));

        IReadOnlyList<TranslationUnit> result = DiffEngine.ComputeDiff(current, previous);

        result.Should().BeEmpty();
    }

    [Fact]
    public void MultipleChangesAcrossSections_AllReturned()
    {
        MenuSnapshot previous = CreateSnapshot(
            new SectionData(Section1Id, "Starters",
                new ItemData(Item1Id, "Soup", "Tomato soup")),
            new SectionData(Section2Id, "Mains",
                new ItemData(Item2Id, "Steak", "Grilled beef")));

        MenuSnapshot current = CreateSnapshot(
            new SectionData(Section1Id, "Starters",
                new ItemData(Item1Id, "Broth", "Tomato soup")),
            new SectionData(Section2Id, "Mains",
                new ItemData(Item2Id, "Steak", "Pan-seared beef")));

        IReadOnlyList<TranslationUnit> result = DiffEngine.ComputeDiff(current, previous);

        result.Should().HaveCount(2);
        result.Should().Contain(u => u.UnitType == TranslationUnitType.ItemName && u.ItemId == Item1Id);
        result.Should().Contain(u => u.UnitType == TranslationUnitType.ItemDescription && u.ItemId == Item2Id);
    }

    [Fact]
    public void EmptySection_ReturnsOnlySectionUnit()
    {
        MenuSnapshot current = CreateSnapshot(
            new SectionData(Section1Id, "Empty Section"));

        IReadOnlyList<TranslationUnit> result = DiffEngine.ComputeDiff(current, null);

        result.Should().HaveCount(1);
        result[0].UnitType.Should().Be(TranslationUnitType.SectionName);
    }

    [Fact]
    public void DescriptionAddedWhereNoneExisted_ReturnsDescriptionUnit()
    {
        MenuSnapshot previous = CreateSnapshot(
            new SectionData(Section1Id, "Starters",
                new ItemData(Item1Id, "Soup", null)));

        MenuSnapshot current = CreateSnapshot(
            new SectionData(Section1Id, "Starters",
                new ItemData(Item1Id, "Soup", "Fresh tomato soup")));

        IReadOnlyList<TranslationUnit> result = DiffEngine.ComputeDiff(current, previous);

        result.Should().HaveCount(1);
        result[0].UnitType.Should().Be(TranslationUnitType.ItemDescription);
        result[0].NormalizedSourceText.Should().Be("Fresh tomato soup");
    }

    [Fact]
    public void DescriptionRemoved_ReturnsNothing()
    {
        MenuSnapshot previous = CreateSnapshot(
            new SectionData(Section1Id, "Starters",
                new ItemData(Item1Id, "Soup", "Fresh tomato soup")));

        MenuSnapshot current = CreateSnapshot(
            new SectionData(Section1Id, "Starters",
                new ItemData(Item1Id, "Soup", null)));

        IReadOnlyList<TranslationUnit> result = DiffEngine.ComputeDiff(current, previous);

        result.Should().BeEmpty();
    }

    [Fact]
    public void DescriptionSetToEmpty_ReturnsNothing()
    {
        MenuSnapshot previous = CreateSnapshot(
            new SectionData(Section1Id, "Starters",
                new ItemData(Item1Id, "Soup", "Fresh tomato soup")));

        MenuSnapshot current = CreateSnapshot(
            new SectionData(Section1Id, "Starters",
                new ItemData(Item1Id, "Soup", "")));

        IReadOnlyList<TranslationUnit> result = DiffEngine.ComputeDiff(current, previous);

        result.Should().BeEmpty();
    }

    [Fact]
    public void SectionUnit_HasCorrectParentIdAndEmptyItemId()
    {
        MenuSnapshot current = CreateSnapshot(
            new SectionData(Section1Id, "Starters"));

        IReadOnlyList<TranslationUnit> result = DiffEngine.ComputeDiff(current, null);

        result[0].ParentSectionId.Should().Be(Section1Id);
        result[0].ItemId.Should().Be(Ulid.Empty);
    }

    [Fact]
    public void ItemUnit_HasCorrectSectionAndItemIds()
    {
        MenuSnapshot current = CreateSnapshot(
            new SectionData(Section1Id, "Starters",
                new ItemData(Item1Id, "Soup", null)));

        IReadOnlyList<TranslationUnit> result = DiffEngine.ComputeDiff(current, null);

        TranslationUnit itemUnit = result.First(u => u.UnitType == TranslationUnitType.ItemName);
        itemUnit.ParentSectionId.Should().Be(Section1Id);
        itemUnit.ItemId.Should().Be(Item1Id);
    }

    [Fact]
    public void WhitespaceOnlyChangeInName_DetectedAfterNormalization()
    {
        MenuSnapshot previous = CreateSnapshot(
            new SectionData(Section1Id, "Starters",
                new ItemData(Item1Id, "Grilled  Chicken", null)));

        MenuSnapshot current = CreateSnapshot(
            new SectionData(Section1Id, "Starters",
                new ItemData(Item1Id, "Grilled Chicken", null)));

        IReadOnlyList<TranslationUnit> result = DiffEngine.ComputeDiff(current, previous);

        result.Should().BeEmpty();
    }

    [Fact]
    public void NullPreviousSnapshot_ItemWithDescription_ProducesBothNameAndDescriptionUnits()
    {
        MenuSnapshot current = CreateSnapshot(
            new SectionData(Section1Id, "Starters",
                new ItemData(Item1Id, "Soup", "Delicious soup")));

        IReadOnlyList<TranslationUnit> result = DiffEngine.ComputeDiff(current, null);

        result.Where(u => u.ItemId == Item1Id && u.UnitType == TranslationUnitType.ItemName).Should().HaveCount(1);
        result.Where(u => u.ItemId == Item1Id && u.UnitType == TranslationUnitType.ItemDescription).Should().HaveCount(1);
    }

    [Fact]
    public void NullPreviousSnapshot_ItemWithoutDescription_ProducesOnlyNameUnit()
    {
        MenuSnapshot current = CreateSnapshot(
            new SectionData(Section1Id, "Starters",
                new ItemData(Item1Id, "Soup", null)));

        IReadOnlyList<TranslationUnit> result = DiffEngine.ComputeDiff(current, null);

        result.Where(u => u.ItemId == Item1Id).Should().HaveCount(1);
        result.First(u => u.ItemId == Item1Id).UnitType.Should().Be(TranslationUnitType.ItemName);
    }

    [Fact]
    public void EmptyCurrentSnapshot_ReturnsEmpty()
    {
        MenuSnapshot current = new() { Sections = [] };

        IReadOnlyList<TranslationUnit> result = DiffEngine.ComputeDiff(current, null);

        result.Should().BeEmpty();
    }

    [Fact]
    public void BothNameAndDescriptionChanged_ReturnsBothUnits()
    {
        MenuSnapshot previous = CreateSnapshot(
            new SectionData(Section1Id, "Starters",
                new ItemData(Item1Id, "Soup", "Tomato soup")));

        MenuSnapshot current = CreateSnapshot(
            new SectionData(Section1Id, "Starters",
                new ItemData(Item1Id, "Broth", "Chicken broth")));

        IReadOnlyList<TranslationUnit> result = DiffEngine.ComputeDiff(current, previous);

        result.Should().HaveCount(2);
        result.Should().Contain(u => u.UnitType == TranslationUnitType.ItemName);
        result.Should().Contain(u => u.UnitType == TranslationUnitType.ItemDescription);
    }

    [Fact]
    public void SourceHash_IsConsistentWithNormalizedText()
    {
        MenuSnapshot current = CreateSnapshot(
            new SectionData(Section1Id, "Starters",
                new ItemData(Item1Id, "Grilled Chicken", null)));

        IReadOnlyList<TranslationUnit> result = DiffEngine.ComputeDiff(current, null);

        TranslationUnit nameUnit = result.First(u => u.UnitType == TranslationUnitType.ItemName);
        string expectedNormalized = TextNormalizer.Normalize("Grilled Chicken".AsSpan());
        nameUnit.NormalizedSourceText.Should().Be(expectedNormalized);
        nameUnit.SourceHash.Should().NotBe(0L);
    }

    private sealed record SectionData(Ulid Id, string Name, params ItemData[] Items);
    private sealed record ItemData(Ulid Id, string Name, string? Description);

    private static MenuSnapshot CreateSnapshot(params SectionData[] sections)
    {
        return new MenuSnapshot
        {
            Sections = sections.Select(s => new SectionSnapshot
            {
                Id = s.Id,
                Name = s.Name,
                Items = s.Items.Select(i => new ItemSnapshot
                {
                    Id = i.Id,
                    Name = i.Name,
                    Description = i.Description
                }).ToList()
            }).ToList()
        };
    }
}
