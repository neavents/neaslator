using System.Text.Json;
using FluentAssertions;
using Neaslator.Infrastructure.Diff;

namespace Neaslator.Tests.Diff;

/// <summary>
/// The saga persists the current MenuSnapshot as jsonb and deserializes the previous one to
/// diff against. If that round-trip is lossy in any field, the diff either misses real changes
/// (stale translations shipped) or reports spurious ones (needless re-translation). These pin
/// the round-trip as lossless — using the same default System.Text.Json settings the saga uses.
/// </summary>
public sealed class SnapshotSerializationTests
{
    private static MenuSnapshot RichSnapshot()
    {
        return new MenuSnapshot
        {
            Sections =
            [
                new SectionSnapshot
                {
                    Id = Ulid.NewUlid(),
                    Name = "Entrées & \"Specials\"",
                    DoNotTranslateName = true,
                    DoNotTranslateDescription = false,
                    Items =
                    [
                        new ItemSnapshot
                        {
                            Id = Ulid.NewUlid(),
                            Name = "Spicy Ramen 🌶️🍜",
                            Description = "Line1\nLine2\twith tab, quote \" and backslash \\",
                            DoNotTranslateName = false,
                            DoNotTranslateDescription = true,
                            SubItems =
                            [
                                new SubItemSnapshot
                                {
                                    Id = Ulid.NewUlid(),
                                    Name = "إضافة جبن",           // Arabic
                                    Description = "追加のトッピング",  // Japanese
                                    DoNotTranslateName = false,
                                    DoNotTranslateDescription = false
                                }
                            ]
                        },
                        new ItemSnapshot
                        {
                            Id = Ulid.NewUlid(),
                            Name = "Café au lait",     // combining/diacritic content
                            Description = null,         // explicit null must survive
                            SubItems = []
                        }
                    ]
                }
            ]
        };
    }

    private static MenuSnapshot RoundTrip(MenuSnapshot s)
    {
        string json = JsonSerializer.Serialize(s);
        return JsonSerializer.Deserialize<MenuSnapshot>(json)!;
    }

    [Fact]
    public void RoundTrip_PreservesEveryFieldExactly()
    {
        MenuSnapshot original = RichSnapshot();
        MenuSnapshot copy = RoundTrip(original);

        copy.Should().BeEquivalentTo(original, "the jsonb round-trip must not lose or alter any field");
    }

    [Fact]
    public void RoundTrip_PreservesUlidIdentity()
    {
        MenuSnapshot original = RichSnapshot();
        MenuSnapshot copy = RoundTrip(original);

        // Ids drive the diff's identity matching; a mangled Ulid would read as delete+add.
        copy.Sections[0].Id.Should().Be(original.Sections[0].Id);
        copy.Sections[0].Items[0].Id.Should().Be(original.Sections[0].Items[0].Id);
        copy.Sections[0].Items[0].SubItems[0].Id.Should().Be(original.Sections[0].Items[0].SubItems[0].Id);
    }

    [Fact]
    public void RoundTrip_PreservesNullDescription()
    {
        MenuSnapshot copy = RoundTrip(RichSnapshot());
        copy.Sections[0].Items[1].Description.Should().BeNull();
    }

    [Fact]
    public void DiffAgainstRoundTrip_IsEmpty_NoSpuriousRetranslation()
    {
        MenuSnapshot original = RichSnapshot();
        MenuSnapshot copy = RoundTrip(original);

        DiffEngine.ComputeDiff(original, copy).Should().BeEmpty();
        DiffEngine.ComputeDiff(copy, original).Should().BeEmpty();
    }

    [Fact]
    public void RoundTrip_StillDetectsARealChange_DiffSensitivityIntact()
    {
        // Guards against a round-trip that "passes" only because it flattened everything.
        MenuSnapshot previous = RoundTrip(RichSnapshot());

        MenuSnapshot current = RoundTrip(previous) with
        {
            Sections =
            [
                previous.Sections[0] with
                {
                    Items =
                    [
                        previous.Sections[0].Items[0] with { Name = "Mild Ramen 🍜" },
                        previous.Sections[0].Items[1]
                    ]
                }
            ]
        };

        IReadOnlyList<TranslationUnit> diff = DiffEngine.ComputeDiff(current, previous);
        diff.Should().ContainSingle();
        diff[0].NormalizedSourceText.Should().Be("Mild Ramen 🍜");
    }

    [Fact]
    public void RoundTrip_OfWhitespaceOnlyDescription_ProducesNoDiffUnit()
    {
        MenuSnapshot original = new()
        {
            Sections =
            [
                new SectionSnapshot
                {
                    Id = Ulid.NewUlid(),
                    Name = "S",
                    Items = [new ItemSnapshot { Id = Ulid.NewUlid(), Name = "Item", Description = "   " }]
                }
            ]
        };

        MenuSnapshot copy = RoundTrip(original);

        DiffEngine.ComputeDiff(copy, original).Should().BeEmpty();
        DiffEngine.ComputeDiff(copy, null).Should().NotContain(u => u.NormalizedSourceText.Length == 0);
    }
}
