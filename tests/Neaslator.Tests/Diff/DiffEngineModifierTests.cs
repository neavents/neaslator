using System.Text.Json;
using FluentAssertions;
using Neaslator.Domain.Enums;
using Neaslator.Infrastructure.Diff;
using Neaslator.Infrastructure.Hashing;
using Neaslator.Infrastructure.Normalization;

namespace Neaslator.Tests.Diff;

public sealed class DiffEngineModifierTests
{
    private static readonly Ulid MilkId = Ulid.NewUlid();
    private static readonly Ulid OatId = Ulid.NewUlid();
    private static readonly Ulid SoyId = Ulid.NewUlid();
    private static readonly Ulid ShotId = Ulid.NewUlid();

    private static MenuSnapshot Snapshot(params ModifierGroupSnapshot[] groups) => new() { Sections = [], ModifierGroups = groups };

    private static ModifierGroupSnapshot Milk(string name = "Süt", string oat = "Yulaf", string soy = "Soya") => new()
    {
        Id = MilkId,
        Name = name,
        Options = [new ModifierOptionSnapshot { Id = OatId, Name = oat }, new ModifierOptionSnapshot { Id = SoyId, Name = soy }],
    };

    private static IEnumerable<TranslationUnit> ModifierUnits(IEnumerable<TranslationUnit> units)
        => units.Where(u => u.UnitType is TranslationUnitType.ModifierGroupName or TranslationUnitType.ModifierOptionName);

    [Fact]
    public void A_first_snapshot_translates_every_group_and_option_name_keyed_by_its_id()
    {
        List<TranslationUnit> units = [.. ModifierUnits(DiffEngine.ComputeDiff(Snapshot(Milk()), null))];

        units.Select(u => (u.UnitType, u.ItemId, u.NormalizedSourceText)).Should().Equal(
            (TranslationUnitType.ModifierGroupName, MilkId, "Süt"),
            (TranslationUnitType.ModifierOptionName, OatId, "Yulaf"),
            (TranslationUnitType.ModifierOptionName, SoyId, "Soya"));
        units.Should().OnlyContain(u => u.ParentSectionId == Ulid.Empty);
        units[0].SourceHash.Should().Be(TranslationHasher.ComputeHash(TextNormalizer.Normalize("Süt")));
    }

    [Fact]
    public void Nothing_changed_means_no_units_even_when_the_options_were_reordered_or_respaced()
    {
        MenuSnapshot previous = Snapshot(Milk());
        MenuSnapshot current = Snapshot(Milk() with
        {
            Name = "  Süt ",
            Options = [new ModifierOptionSnapshot { Id = SoyId, Name = "Soya" }, new ModifierOptionSnapshot { Id = OatId, Name = "Yulaf" }],
        });

        ModifierUnits(DiffEngine.ComputeDiff(current, previous)).Should().BeEmpty();
    }

    [Fact]
    public void A_renamed_option_and_a_new_group_are_the_only_units()
    {
        MenuSnapshot previous = Snapshot(Milk());
        ModifierGroupSnapshot shot = new() { Id = ShotId, Name = "Shot", Options = [] };

        List<TranslationUnit> units = [.. ModifierUnits(DiffEngine.ComputeDiff(Snapshot(Milk(oat: "Yulaf sütü"), shot), previous))];

        units.Select(u => (u.ItemId, u.NormalizedSourceText)).Should().Equal((OatId, "Yulaf sütü"), (ShotId, "Shot"));
    }

    [Fact]
    public void A_new_option_in_a_known_group_is_translated_on_its_own()
    {
        Ulid almond = Ulid.NewUlid();
        MenuSnapshot current = Snapshot(Milk() with
        {
            Options = [.. Milk().Options, new ModifierOptionSnapshot { Id = almond, Name = "Badem" }],
        });

        ModifierUnits(DiffEngine.ComputeDiff(current, Snapshot(Milk()))).Select(u => u.ItemId).Should().Equal(almond);
    }

    [Fact]
    public void A_snapshot_stored_before_modifiers_were_read_backfills_them_all()
    {
        string stored = JsonSerializer.Serialize(new { Name = "Menu", Sections = Array.Empty<object>() });
        MenuSnapshot previous = JsonSerializer.Deserialize<MenuSnapshot>(stored)!;

        previous.ModifierGroups.Should().BeEmpty();
        ModifierUnits(DiffEngine.ComputeDiff(Snapshot(Milk()), previous)).Should().HaveCount(3);
    }

    [Fact]
    public void A_blank_name_is_never_a_unit()
    {
        ModifierUnits(DiffEngine.ComputeDiff(Snapshot(Milk(name: " ", oat: "")), null))
            .Select(u => u.ItemId).Should().Equal(SoyId);
    }

    [Fact]
    public void A_name_a_person_wrote_in_some_language_does_not_stop_the_source_being_translated()
    {
        ModifierGroupSnapshot milk = Milk() with { HumanNames = new Dictionary<string, string> { ["en"] = "Dairy" } };

        ModifierUnits(DiffEngine.ComputeDiff(Snapshot(milk), null)).Should().HaveCount(3);
        ModifierUnits(DiffEngine.ComputeDiff(Snapshot(milk), Snapshot(Milk()))).Should().BeEmpty();
    }

    [Fact]
    public void A_snapshot_with_modifiers_survives_storage_with_its_human_names()
    {
        MenuSnapshot snapshot = Snapshot(Milk() with { HumanNames = new Dictionary<string, string> { ["en"] = "Dairy" } });

        MenuSnapshot read = JsonSerializer.Deserialize<MenuSnapshot>(JsonSerializer.Serialize(snapshot))!;

        read.ModifierGroups.Single().Options.Select(o => o.Name).Should().Equal("Yulaf", "Soya");
        read.ModifierGroups.Single().HumanNames!["en"].Should().Be("Dairy");
    }
}
