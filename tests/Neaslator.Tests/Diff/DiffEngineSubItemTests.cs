using FluentAssertions;
using Neaslator.Domain.Enums;
using Neaslator.Infrastructure.Diff;

namespace Neaslator.Tests.Diff;

/// <summary>
/// Edge-case coverage for the parts of <see cref="DiffEngine"/> that the base
/// suite does not exercise: sub-items, the DoNotTranslate* flags, and text that
/// normalizes to empty. These assert the <em>intended</em> behavior — a failure
/// here means the engine is wrong, not the test.
/// </summary>
public sealed class DiffEngineSubItemTests
{
    private static readonly Ulid SectionId = Ulid.NewUlid();
    private static readonly Ulid ItemId = Ulid.NewUlid();
    private static readonly Ulid SubItemId = Ulid.NewUlid();
    private static readonly Ulid SubItem2Id = Ulid.NewUlid();

    // ───── Sub-items ─────

    [Fact]
    public void NullPrevious_SubItemWithNameOnly_ProducesSubItemNameUnit()
    {
        MenuSnapshot current = Menu(
            Section(SectionId, "Mains",
                Item(ItemId, "Burger", null,
                    SubItem(SubItemId, "Add Bacon", null))));

        IReadOnlyList<TranslationUnit> result = DiffEngine.ComputeDiff(current, null);

        result.Should().Contain(u => u.ItemId == SubItemId && u.UnitType == TranslationUnitType.ItemName);
        result.First(u => u.ItemId == SubItemId).NormalizedSourceText.Should().Be("Add Bacon");
        result.First(u => u.ItemId == SubItemId).ParentSectionId.Should().Be(SectionId);
    }

    [Fact]
    public void NullPrevious_SubItemWithDescription_ProducesNameAndDescriptionUnits()
    {
        MenuSnapshot current = Menu(
            Section(SectionId, "Mains",
                Item(ItemId, "Burger", null,
                    SubItem(SubItemId, "Add Bacon", "Two crispy strips"))));

        IReadOnlyList<TranslationUnit> result = DiffEngine.ComputeDiff(current, null);

        result.Where(u => u.ItemId == SubItemId).Should().HaveCount(2);
        result.Should().Contain(u => u.ItemId == SubItemId && u.UnitType == TranslationUnitType.ItemName);
        result.Should().Contain(u => u.ItemId == SubItemId && u.UnitType == TranslationUnitType.ItemDescription);
    }

    [Fact]
    public void SubItemNameChanged_ParentUnchanged_ReturnsOnlySubItemUnit()
    {
        MenuSnapshot previous = Menu(
            Section(SectionId, "Mains",
                Item(ItemId, "Burger", null,
                    SubItem(SubItemId, "Add Bacon", null))));

        MenuSnapshot current = Menu(
            Section(SectionId, "Mains",
                Item(ItemId, "Burger", null,
                    SubItem(SubItemId, "Add Smoked Bacon", null))));

        IReadOnlyList<TranslationUnit> result = DiffEngine.ComputeDiff(current, previous);

        result.Should().ContainSingle();
        result[0].ItemId.Should().Be(SubItemId);
        result[0].NormalizedSourceText.Should().Be("Add Smoked Bacon");
    }

    [Fact]
    public void SubItemAddedToExistingItem_ReturnsOnlyNewSubItemUnit()
    {
        MenuSnapshot previous = Menu(
            Section(SectionId, "Mains",
                Item(ItemId, "Burger", null,
                    SubItem(SubItemId, "Add Bacon", null))));

        MenuSnapshot current = Menu(
            Section(SectionId, "Mains",
                Item(ItemId, "Burger", null,
                    SubItem(SubItemId, "Add Bacon", null),
                    SubItem(SubItem2Id, "Add Cheese", null))));

        IReadOnlyList<TranslationUnit> result = DiffEngine.ComputeDiff(current, previous);

        result.Should().ContainSingle();
        result[0].ItemId.Should().Be(SubItem2Id);
        result[0].NormalizedSourceText.Should().Be("Add Cheese");
    }

    [Fact]
    public void SubItemDeleted_ReturnsNothing()
    {
        MenuSnapshot previous = Menu(
            Section(SectionId, "Mains",
                Item(ItemId, "Burger", null,
                    SubItem(SubItemId, "Add Bacon", null),
                    SubItem(SubItem2Id, "Add Cheese", null))));

        MenuSnapshot current = Menu(
            Section(SectionId, "Mains",
                Item(ItemId, "Burger", null,
                    SubItem(SubItemId, "Add Bacon", null))));

        IReadOnlyList<TranslationUnit> result = DiffEngine.ComputeDiff(current, previous);

        result.Should().BeEmpty();
    }

    [Fact]
    public void SubItemUnchanged_ReturnsNothing()
    {
        MenuSnapshot snapshot = Menu(
            Section(SectionId, "Mains",
                Item(ItemId, "Burger", null,
                    SubItem(SubItemId, "Add Bacon", "Crispy"))));

        IReadOnlyList<TranslationUnit> result = DiffEngine.ComputeDiff(snapshot, snapshot);

        result.Should().BeEmpty();
    }

    [Fact]
    public void NewItemWithSubItems_ReturnsItemAndSubItemUnits()
    {
        MenuSnapshot previous = Menu(Section(SectionId, "Mains"));

        MenuSnapshot current = Menu(
            Section(SectionId, "Mains",
                Item(ItemId, "Burger", null,
                    SubItem(SubItemId, "Add Bacon", null))));

        IReadOnlyList<TranslationUnit> result = DiffEngine.ComputeDiff(current, previous);

        result.Should().Contain(u => u.ItemId == ItemId && u.UnitType == TranslationUnitType.ItemName);
        result.Should().Contain(u => u.ItemId == SubItemId && u.UnitType == TranslationUnitType.ItemName);
    }

    [Fact]
    public void SubItemDescriptionChanged_ReturnsOnlySubItemDescriptionUnit()
    {
        MenuSnapshot previous = Menu(
            Section(SectionId, "Mains",
                Item(ItemId, "Burger", null,
                    SubItem(SubItemId, "Add Bacon", "One strip"))));

        MenuSnapshot current = Menu(
            Section(SectionId, "Mains",
                Item(ItemId, "Burger", null,
                    SubItem(SubItemId, "Add Bacon", "Two strips"))));

        IReadOnlyList<TranslationUnit> result = DiffEngine.ComputeDiff(current, previous);

        result.Should().ContainSingle();
        result[0].UnitType.Should().Be(TranslationUnitType.ItemDescription);
        result[0].ItemId.Should().Be(SubItemId);
        result[0].NormalizedSourceText.Should().Be("Two strips");
    }

    // ───── DoNotTranslate flags ─────

    [Fact]
    public void NullPrevious_SectionDoNotTranslateName_SkipsSectionUnit()
    {
        MenuSnapshot current = Menu(
            Section(SectionId, "Specials", doNotTranslateName: true,
                Item(ItemId, "Soup", null)));

        IReadOnlyList<TranslationUnit> result = DiffEngine.ComputeDiff(current, null);

        result.Should().NotContain(u => u.UnitType == TranslationUnitType.SectionName);
        result.Should().Contain(u => u.UnitType == TranslationUnitType.ItemName);
    }

    [Fact]
    public void SectionNameChanged_ButDoNotTranslateName_SkipsSectionUnit()
    {
        MenuSnapshot previous = Menu(
            Section(SectionId, "Specials", doNotTranslateName: true,
                Item(ItemId, "Soup", null)));

        MenuSnapshot current = Menu(
            Section(SectionId, "Daily Specials", doNotTranslateName: true,
                Item(ItemId, "Soup", null)));

        IReadOnlyList<TranslationUnit> result = DiffEngine.ComputeDiff(current, previous);

        result.Should().BeEmpty();
    }

    [Fact]
    public void NullPrevious_ItemDoNotTranslateName_SkipsItemNameUnit()
    {
        MenuSnapshot current = Menu(
            Section(SectionId, "Mains",
                FlagItem(ItemId, "Coca-Cola", "Chilled", doNotTranslateName: true)));

        IReadOnlyList<TranslationUnit> result = DiffEngine.ComputeDiff(current, null);

        result.Should().NotContain(u => u.ItemId == ItemId && u.UnitType == TranslationUnitType.ItemName);
        result.Should().Contain(u => u.ItemId == ItemId && u.UnitType == TranslationUnitType.ItemDescription);
    }

    [Fact]
    public void NullPrevious_ItemDoNotTranslateDescription_SkipsDescriptionUnit()
    {
        MenuSnapshot current = Menu(
            Section(SectionId, "Mains",
                FlagItem(ItemId, "Burger", "Contains E621", doNotTranslateDescription: true)));

        IReadOnlyList<TranslationUnit> result = DiffEngine.ComputeDiff(current, null);

        result.Should().Contain(u => u.ItemId == ItemId && u.UnitType == TranslationUnitType.ItemName);
        result.Should().NotContain(u => u.ItemId == ItemId && u.UnitType == TranslationUnitType.ItemDescription);
    }

    [Fact]
    public void ItemNameChanged_ButDoNotTranslateName_SkipsNameUnit()
    {
        MenuSnapshot previous = Menu(
            Section(SectionId, "Mains",
                FlagItem(ItemId, "Coke", null, doNotTranslateName: true)));

        MenuSnapshot current = Menu(
            Section(SectionId, "Mains",
                FlagItem(ItemId, "Coca-Cola", null, doNotTranslateName: true)));

        IReadOnlyList<TranslationUnit> result = DiffEngine.ComputeDiff(current, previous);

        result.Should().BeEmpty();
    }

    [Fact]
    public void SubItemDoNotTranslateName_SkipsSubItemNameUnit()
    {
        MenuSnapshot current = Menu(
            Section(SectionId, "Mains",
                Item(ItemId, "Burger", null,
                    SubItem(SubItemId, "Pepsi", "Cold", doNotTranslateName: true))));

        IReadOnlyList<TranslationUnit> result = DiffEngine.ComputeDiff(current, null);

        result.Should().NotContain(u => u.ItemId == SubItemId && u.UnitType == TranslationUnitType.ItemName);
        result.Should().Contain(u => u.ItemId == SubItemId && u.UnitType == TranslationUnitType.ItemDescription);
    }

    // ───── Text that normalizes to empty must never become a translation unit ─────

    [Fact]
    public void NullPrevious_WhitespaceOnlyDescription_ProducesNoDescriptionUnit()
    {
        MenuSnapshot current = Menu(
            Section(SectionId, "Mains",
                Item(ItemId, "Soup", "   ")));

        IReadOnlyList<TranslationUnit> result = DiffEngine.ComputeDiff(current, null);

        result.Should().NotContain(u => u.UnitType == TranslationUnitType.ItemDescription);
        result.Should().NotContain(u => u.NormalizedSourceText.Length == 0);
    }

    [Fact]
    public void DescriptionChangedToWhitespaceOnly_ProducesNoEmptyUnit()
    {
        MenuSnapshot previous = Menu(
            Section(SectionId, "Mains",
                Item(ItemId, "Soup", "Fresh tomato soup")));

        MenuSnapshot current = Menu(
            Section(SectionId, "Mains",
                Item(ItemId, "Soup", "   ")));

        IReadOnlyList<TranslationUnit> result = DiffEngine.ComputeDiff(current, previous);

        result.Should().NotContain(u => u.NormalizedSourceText.Length == 0);
        result.Should().NotContain(u => u.SourceHash == 0);
    }

    [Fact]
    public void DescriptionChangedToInvisibleCharsOnly_ProducesNoEmptyUnit()
    {
        MenuSnapshot previous = Menu(
            Section(SectionId, "Mains",
                Item(ItemId, "Soup", "Fresh tomato soup")));

        // Zero-width space + BOM only -> normalizes to empty
        MenuSnapshot current = Menu(
            Section(SectionId, "Mains",
                Item(ItemId, "Soup", "​﻿")));

        IReadOnlyList<TranslationUnit> result = DiffEngine.ComputeDiff(current, previous);

        result.Should().NotContain(u => u.NormalizedSourceText.Length == 0);
    }

    [Fact]
    public void SubItemDescriptionChangedToWhitespace_ProducesNoEmptyUnit()
    {
        MenuSnapshot previous = Menu(
            Section(SectionId, "Mains",
                Item(ItemId, "Burger", null,
                    SubItem(SubItemId, "Add Bacon", "Two strips"))));

        MenuSnapshot current = Menu(
            Section(SectionId, "Mains",
                Item(ItemId, "Burger", null,
                    SubItem(SubItemId, "Add Bacon", "  "))));

        IReadOnlyList<TranslationUnit> result = DiffEngine.ComputeDiff(current, previous);

        result.Should().NotContain(u => u.NormalizedSourceText.Length == 0);
    }

    // ───── Builders ─────

    private static MenuSnapshot Menu(params SectionSnapshot[] sections) =>
        new() { Sections = sections };

    private static SectionSnapshot Section(Ulid id, string name, params ItemSnapshot[] items) =>
        new() { Id = id, Name = name, Items = items };

    private static SectionSnapshot Section(Ulid id, string name, bool doNotTranslateName, params ItemSnapshot[] items) =>
        new() { Id = id, Name = name, DoNotTranslateName = doNotTranslateName, Items = items };

    private static ItemSnapshot Item(Ulid id, string name, string? description, params SubItemSnapshot[] subItems) =>
        new() { Id = id, Name = name, Description = description, SubItems = subItems };

    private static ItemSnapshot FlagItem(Ulid id, string name, string? description, bool doNotTranslateName = false, bool doNotTranslateDescription = false) =>
        new()
        {
            Id = id,
            Name = name,
            Description = description,
            DoNotTranslateName = doNotTranslateName,
            DoNotTranslateDescription = doNotTranslateDescription
        };

    private static SubItemSnapshot SubItem(Ulid id, string name, string? description, bool doNotTranslateName = false, bool doNotTranslateDescription = false) =>
        new()
        {
            Id = id,
            Name = name,
            Description = description,
            DoNotTranslateName = doNotTranslateName,
            DoNotTranslateDescription = doNotTranslateDescription
        };
}
