using FluentAssertions;
using Neaslator.Infrastructure.Normalization;

namespace Neaslator.Tests.Normalization;

public sealed class TextNormalizerTests
{
    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        TextNormalizer.Normalize("".AsSpan()).Should().BeEmpty();
    }

    [Fact]
    public void PlainText_ReturnsUnchanged()
    {
        TextNormalizer.Normalize("Grilled Chicken".AsSpan()).Should().Be("Grilled Chicken");
    }

    [Fact]
    public void MultipleSpaces_CollapsedToSingle()
    {
        TextNormalizer.Normalize("Grilled   Chicken".AsSpan()).Should().Be("Grilled Chicken");
    }

    [Fact]
    public void LeadingWhitespace_Trimmed()
    {
        TextNormalizer.Normalize("  Grilled Chicken".AsSpan()).Should().Be("Grilled Chicken");
    }

    [Fact]
    public void TrailingWhitespace_Trimmed()
    {
        TextNormalizer.Normalize("Grilled Chicken  ".AsSpan()).Should().Be("Grilled Chicken");
    }

    [Fact]
    public void LeadingAndTrailingWhitespace_Trimmed()
    {
        TextNormalizer.Normalize("  Grilled Chicken  ".AsSpan()).Should().Be("Grilled Chicken");
    }

    [Fact]
    public void Tabs_NormalizedToSpace()
    {
        TextNormalizer.Normalize("Grilled\tChicken".AsSpan()).Should().Be("Grilled Chicken");
    }

    [Fact]
    public void NonBreakingSpace_NormalizedToSpace()
    {
        TextNormalizer.Normalize("Grilled Chicken".AsSpan()).Should().Be("Grilled Chicken");
    }

    [Fact]
    public void ZeroWidthSpace_Stripped()
    {
        TextNormalizer.Normalize("Grilled​Chicken".AsSpan()).Should().Be("GrilledChicken");
    }

    [Fact]
    public void ZeroWidthNonJoiner_Stripped()
    {
        TextNormalizer.Normalize("Grilled‌Chicken".AsSpan()).Should().Be("GrilledChicken");
    }

    [Fact]
    public void ZeroWidthJoiner_Stripped()
    {
        TextNormalizer.Normalize("Grilled‍Chicken".AsSpan()).Should().Be("GrilledChicken");
    }

    [Fact]
    public void BOM_Stripped()
    {
        TextNormalizer.Normalize("﻿Grilled Chicken".AsSpan()).Should().Be("Grilled Chicken");
    }

    [Fact]
    public void SoftHyphen_Stripped()
    {
        TextNormalizer.Normalize("Grilled­Chicken".AsSpan()).Should().Be("GrilledChicken");
    }

    [Fact]
    public void LeftToRightMark_Stripped()
    {
        TextNormalizer.Normalize("Grilled‎Chicken".AsSpan()).Should().Be("GrilledChicken");
    }

    [Fact]
    public void RightToLeftMark_Stripped()
    {
        TextNormalizer.Normalize("Grilled‏Chicken".AsSpan()).Should().Be("GrilledChicken");
    }

    [Fact]
    public void LineSeparator_Stripped()
    {
        TextNormalizer.Normalize(("Grilled\u2028Chicken").AsSpan()).Should().Be("GrilledChicken");
    }

    [Fact]
    public void ParagraphSeparator_Stripped()
    {
        TextNormalizer.Normalize(("Grilled\u2029Chicken").AsSpan()).Should().Be("GrilledChicken");
    }

    [Fact]
    public void NfcNormalization_DecomposedAndComposed_ProduceSameResult()
    {
        string decomposed = "café";
        string composed = "café";
        TextNormalizer.Normalize(decomposed.AsSpan())
            .Should().Be(TextNormalizer.Normalize(composed.AsSpan()));
    }

    [Fact]
    public void CasePreserved()
    {
        TextNormalizer.Normalize("FRENCH FRIES".AsSpan())
            .Should().NotBe(TextNormalizer.Normalize("French Fries".AsSpan()));
    }

    [Fact]
    public void DiacriticsPreserved()
    {
        TextNormalizer.Normalize("café".AsSpan())
            .Should().NotBe(TextNormalizer.Normalize("cafe".AsSpan()));
    }

    [Fact]
    public void LongText_UsesArrayPool_ProducesCorrectResult()
    {
        string longText = new string('A', 600) + "  " + new string('B', 600);
        string result = TextNormalizer.Normalize(longText.AsSpan());
        result.Should().Be(new string('A', 600) + " " + new string('B', 600));
    }

    [Fact]
    public void LongText_ExceedsStackAllocThreshold()
    {
        string longText = new string('X', 1000);
        string result = TextNormalizer.Normalize(longText.AsSpan());
        result.Should().Be(longText);
    }

    [Fact]
    public void MixedInvisibleChars_AllStripped()
    {
        string input = "​‌‍﻿‎‏Hello⁠؜World⁪⁯";
        TextNormalizer.Normalize(input.AsSpan()).Should().Be("HelloWorld");
    }

    [Fact]
    public void Newlines_CollapsedToSpace()
    {
        TextNormalizer.Normalize("Line1\nLine2".AsSpan()).Should().Be("Line1 Line2");
    }

    [Fact]
    public void CarriageReturnLineFeed_CollapsedToSingleSpace()
    {
        TextNormalizer.Normalize("Line1\r\nLine2".AsSpan()).Should().Be("Line1 Line2");
    }

    [Fact]
    public void OnlyWhitespace_ReturnsEmpty()
    {
        TextNormalizer.Normalize("   \t\n  ".AsSpan()).Should().BeEmpty();
    }

    [Fact]
    public void OnlyInvisibleChars_ReturnsEmpty()
    {
        TextNormalizer.Normalize("​‌‍﻿".AsSpan()).Should().BeEmpty();
    }

    [Fact]
    public void MixedWhitespaceTypes_CollapsedToSingleSpace()
    {
        TextNormalizer.Normalize("A \t \n \r B".AsSpan()).Should().Be("A B");
    }

    [Theory]
    [InlineData("Pizza", "Pizza")]
    [InlineData("  Pizza  ", "Pizza")]
    [InlineData("Grilled  Fish", "Grilled Fish")]
    public void Theory_BasicNormalization(string input, string expected)
    {
        TextNormalizer.Normalize(input.AsSpan()).Should().Be(expected);
    }

    [Fact]
    public void SingleCharacter_Preserved()
    {
        TextNormalizer.Normalize("A".AsSpan()).Should().Be("A");
    }

    [Fact]
    public void UnicodeEmoji_Preserved()
    {
        TextNormalizer.Normalize("Pizza \U0001F355".AsSpan()).Should().Be("Pizza \U0001F355");
    }

    [Fact]
    public void JapaneseText_Preserved()
    {
        TextNormalizer.Normalize("寿司".AsSpan()).Should().Be("寿司");
    }

    [Fact]
    public void ArabicText_Preserved()
    {
        TextNormalizer.Normalize("شاورما".AsSpan())
            .Should().Be("شاورما");
    }

    [Fact]
    public void WordJoiner_Stripped()
    {
        TextNormalizer.Normalize("Grilled⁠Chicken".AsSpan()).Should().Be("GrilledChicken");
    }

    [Fact]
    public void InvisibleCharBetweenSpaces_CollapsedCorrectly()
    {
        TextNormalizer.Normalize("A ​ B".AsSpan()).Should().Be("A B");
    }

    [Fact]
    public void CombiningGraphemeJoiner_Stripped()
    {
        TextNormalizer.Normalize("Grilled͏Chicken".AsSpan()).Should().Be("GrilledChicken");
    }

    [Fact]
    public void ArabicLetterMark_Stripped()
    {
        TextNormalizer.Normalize("Grilled؜Chicken".AsSpan()).Should().Be("GrilledChicken");
    }

    [Fact]
    public void InvisibleTimesOperator_Stripped()
    {
        TextNormalizer.Normalize("Grilled⁢Chicken".AsSpan()).Should().Be("GrilledChicken");
    }
}
