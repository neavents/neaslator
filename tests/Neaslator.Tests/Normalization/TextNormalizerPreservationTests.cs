using FluentAssertions;
using Neaslator.Infrastructure.Normalization;

namespace Neaslator.Tests.Normalization;

/// <summary>
/// Content-preservation guarantees. Because the normalized text is what gets sent to the LLM
/// (and hashed for caching), any character the normalizer drops is dropped from the source the
/// translator sees. These pin that real menu content across scripts survives intact — and
/// deliberately document two lossy cases (ZWNJ/ZWJ) so the trade-off is visible, not accidental.
/// </summary>
public sealed class TextNormalizerPreservationTests
{
    private static string N(string s) => TextNormalizer.Normalize(s.AsSpan());

    [Theory]
    [InlineData("شاورما لحم")]           // Arabic (no joiners)
    [InlineData("宫保鸡丁")]              // Chinese
    [InlineData("寿司の盛り合わせ")]        // Japanese
    [InlineData("비빔밥")]                // Korean
    [InlineData("Phở bò tái")]           // Vietnamese diacritics
    [InlineData("Крем-брюле")]           // Cyrillic
    [InlineData("Π	αστίτσιο")]          // Greek (with a tab that collapses)
    public void RealMenuText_AcrossScripts_PreservedContent(string input)
    {
        // Content characters survive; only the tab (if any) collapses to a space.
        N(input).Should().Be(input.Replace('\t', ' '));
    }

    [Fact]
    public void CombiningDiacritics_ThatFormTheWord_ArePreserved()
    {
        // "Việt" carries combining marks that are part of the letters, not decoration to drop.
        N("Cà phê Việt").Should().Be("Cà phê Việt");
    }

    [Fact]
    public void RegionalIndicatorFlagEmoji_Preserved()
    {
        // Flags are two regional-indicator code points (no ZWJ) and must round-trip exactly.
        string flag = "\U0001F1F9\U0001F1F7"; // 🇹🇷
        N($"Kebap {flag}").Should().Be($"Kebap {flag}");
    }

    [Fact]
    public void SkinToneModifierEmoji_Preserved()
    {
        string thumbs = "\U0001F44D\U0001F3FD"; // 👍🏽 (base + skin-tone modifier)
        N($"Best {thumbs}").Should().Be($"Best {thumbs}");
    }

    [Fact]
    public void CurrencyFractionsAndPunctuation_Preserved()
    {
        N("Menu €12.50 – ½ portion (chef's)").Should().Be("Menu €12.50 – ½ portion (chef's)");
    }

    [Fact]
    public void MixedScriptWithinOneName_Preserved()
    {
        N("Ramen 拉麺 (spicy)").Should().Be("Ramen 拉麺 (spicy)");
    }

    [Fact]
    public void RightToLeftText_OrderAndContentPreserved()
    {
        // No bidi control chars present -> the string must be byte-for-byte identical.
        string arabic = "دجاج مشوي مع الأرز";
        N(arabic).Should().Be(arabic);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────
    //  DOCUMENTED CURRENT BEHAVIOR — these two cases are LOSSY. ZWNJ (U+200C) and ZWJ (U+200D)
    //  carry meaning in Persian/Indic orthography and in emoji ZWJ sequences, but the normalizer
    //  currently strips them as "invisible". Pinned here so the behavior is explicit and any
    //  future change is a conscious decision. See the note in the accompanying review summary.
    // ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Documented_PersianZwnj_IsStripped_AlteringOrthography()
    {
        // Persian "کتاب‌ها" (book + ZWNJ + ها plural) normalizes to "کتابها" — the ZWNJ is removed.
        const string withZwnj = "کتاب‌ها";
        N(withZwnj).Should().Be("کتابها");
    }

    [Fact]
    public void Documented_EmojiZwjSequence_IsSplit()
    {
        // Rainbow flag = white flag + VS16 + ZWJ + rainbow. Stripping the ZWJ splits it into
        // two separate emoji (white flag, rainbow).
        const string rainbowFlag = "\U0001F3F3️‍\U0001F308";
        N(rainbowFlag).Should().Be("\U0001F3F3️\U0001F308");
    }
}
