using FluentAssertions;
using Neaslator.Infrastructure.Hashing;
using Neaslator.Infrastructure.Normalization;

namespace Neaslator.Tests.Hashing;

public sealed class TranslationHasherTests
{
    [Fact]
    public void EmptyInput_ReturnsZero()
    {
        TranslationHasher.ComputeHash("".AsSpan()).Should().Be(0L);
    }

    [Fact]
    public void SameInput_SameHash()
    {
        long hash1 = TranslationHasher.ComputeHash("Grilled Chicken".AsSpan());
        long hash2 = TranslationHasher.ComputeHash("Grilled Chicken".AsSpan());
        hash1.Should().Be(hash2);
    }

    [Fact]
    public void DifferentInput_DifferentHash()
    {
        long hash1 = TranslationHasher.ComputeHash("Grilled Chicken".AsSpan());
        long hash2 = TranslationHasher.ComputeHash("Fried Chicken".AsSpan());
        hash1.Should().NotBe(hash2);
    }

    [Fact]
    public void NormalizedEquivalents_SameHash()
    {
        string normalized1 = TextNormalizer.Normalize("Grilled  Chicken".AsSpan());
        string normalized2 = TextNormalizer.Normalize("Grilled\tChicken".AsSpan());
        TranslationHasher.ComputeHash(normalized1.AsSpan())
            .Should().Be(TranslationHasher.ComputeHash(normalized2.AsSpan()));
    }

    [Fact]
    public void NonEmptyInput_ReturnsNonZero()
    {
        TranslationHasher.ComputeHash("A".AsSpan()).Should().NotBe(0L);
    }

    [Fact]
    public void LongText_UsesArrayPool_ProducesCorrectHash()
    {
        string longText = new string('Z', 2000);
        long hash1 = TranslationHasher.ComputeHash(longText.AsSpan());
        long hash2 = TranslationHasher.ComputeHash(longText.AsSpan());
        hash1.Should().Be(hash2);
        hash1.Should().NotBe(0L);
    }

    [Fact]
    public void UnicodeText_HashesCorrectly()
    {
        long hash = TranslationHasher.ComputeHash("café".AsSpan());
        hash.Should().NotBe(0L);
    }

    [Fact]
    public void UnicodeText_DifferentFromAsciiEquivalent()
    {
        long hashUnicode = TranslationHasher.ComputeHash("café".AsSpan());
        long hashAscii = TranslationHasher.ComputeHash("cafe".AsSpan());
        hashUnicode.Should().NotBe(hashAscii);
    }

    [Fact]
    public void JapaneseText_HashesCorrectly()
    {
        long hash = TranslationHasher.ComputeHash("寿司セット".AsSpan());
        hash.Should().NotBe(0L);
    }

    [Fact]
    public void SingleCharDifference_DifferentHash()
    {
        long hash1 = TranslationHasher.ComputeHash("abc".AsSpan());
        long hash2 = TranslationHasher.ComputeHash("abd".AsSpan());
        hash1.Should().NotBe(hash2);
    }

    [Fact]
    public void CaseSensitive()
    {
        long lower = TranslationHasher.ComputeHash("pizza".AsSpan());
        long upper = TranslationHasher.ComputeHash("PIZZA".AsSpan());
        lower.Should().NotBe(upper);
    }

    [Fact]
    public void WhitespaceDifference_DifferentHash()
    {
        long hash1 = TranslationHasher.ComputeHash("A B".AsSpan());
        long hash2 = TranslationHasher.ComputeHash("AB".AsSpan());
        hash1.Should().NotBe(hash2);
    }

    [Fact]
    public void TextExactlyAtArrayPoolThreshold_WorksCorrectly()
    {
        string text = new string('A', 341);
        long hash1 = TranslationHasher.ComputeHash(text.AsSpan());
        long hash2 = TranslationHasher.ComputeHash(text.AsSpan());
        hash1.Should().Be(hash2);
    }

    [Fact]
    public void TextJustAboveArrayPoolThreshold_WorksCorrectly()
    {
        string text = new string('A', 342);
        long hash1 = TranslationHasher.ComputeHash(text.AsSpan());
        long hash2 = TranslationHasher.ComputeHash(text.AsSpan());
        hash1.Should().Be(hash2);
    }
}
