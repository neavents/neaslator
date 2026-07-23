using FluentAssertions;
using Neaslator.Infrastructure.Normalization;

namespace Neaslator.Tests.Normalization;

/// <summary>
/// Boundary coverage around the 512-char stackalloc / ArrayPool switch inside
/// <see cref="TextNormalizer"/>, plus idempotency guarantees. The rented-buffer path
/// and the stack path must produce byte-identical output.
/// </summary>
public sealed class TextNormalizerBoundaryTests
{
    [Theory]
    [InlineData(511)]
    [InlineData(512)]
    [InlineData(513)]
    [InlineData(1024)]
    public void PlainText_AtAndAroundStackAllocThreshold_Preserved(int length)
    {
        string input = new string('A', length);
        TextNormalizer.Normalize(input.AsSpan()).Should().Be(input);
    }

    [Fact]
    public void ExactlyAtThreshold_WithInternalCollapse_ProducesCorrectResult()
    {
        // 256 'A', two spaces, 254 'B' -> 512 chars in, collapses one space -> 511 out.
        string input = new string('A', 256) + "  " + new string('B', 254);
        string expected = new string('A', 256) + " " + new string('B', 254);
        TextNormalizer.Normalize(input.AsSpan()).Should().Be(expected);
    }

    [Fact]
    public void JustOverThreshold_WithInternalCollapse_ProducesCorrectResult()
    {
        string input = new string('A', 300) + "   " + new string('B', 300);
        string expected = new string('A', 300) + " " + new string('B', 300);
        TextNormalizer.Normalize(input.AsSpan()).Should().Be(expected);
    }

    [Fact]
    public void LargeAllInvisible_UsesRentedBuffer_ReturnsEmpty()
    {
        string input = new string('​', 600);
        TextNormalizer.Normalize(input.AsSpan()).Should().BeEmpty();
    }

    [Fact]
    public void LargeAllWhitespace_ReturnsEmpty()
    {
        string input = new string(' ', 600);
        TextNormalizer.Normalize(input.AsSpan()).Should().BeEmpty();
    }

    [Fact]
    public void LongTextWithTrailingWhitespaceOverThreshold_TrimmedCorrectly()
    {
        string input = new string('A', 520) + "      ";
        TextNormalizer.Normalize(input.AsSpan()).Should().Be(new string('A', 520));
    }

    [Fact]
    public void LongTextWithLeadingWhitespaceOverThreshold_TrimmedCorrectly()
    {
        string input = "      " + new string('A', 520);
        TextNormalizer.Normalize(input.AsSpan()).Should().Be(new string('A', 520));
    }

    [Theory]
    [InlineData("Grilled   Chicken")]
    [InlineData("  café  ")]
    [InlineData("Line1\nLine2\tLine3")]
    [InlineData("Zero​Width﻿Stuff")]
    public void Normalize_IsIdempotent(string input)
    {
        string once = TextNormalizer.Normalize(input.AsSpan());
        string twice = TextNormalizer.Normalize(once.AsSpan());
        twice.Should().Be(once);
    }

    [Fact]
    public void SurrogatePairsAcrossThreshold_NotCorrupted()
    {
        // 300 emoji (surrogate pairs) => 600 chars, exercises the rented path
        // and must not split or drop any surrogate.
        string emoji = string.Concat(Enumerable.Repeat("\U0001F355", 300));
        string result = TextNormalizer.Normalize(emoji.AsSpan());
        result.Should().Be(emoji);
    }

    [Fact]
    public void MixedContentOverThreshold_CollapsesAndStripsConsistently()
    {
        string input = new string('A', 300) + "  ​  " + new string('B', 300);
        string expected = new string('A', 300) + " " + new string('B', 300);
        TextNormalizer.Normalize(input.AsSpan()).Should().Be(expected);
    }
}
