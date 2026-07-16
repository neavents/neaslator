using System.Buffers;
using System.Text;

namespace Neaslator.Infrastructure.Normalization;

public static class TextNormalizer
{
    private static readonly SearchValues<char> InvisibleCharacters = SearchValues.Create(
    [
        '\u200B', '\u200C', '\u200D', '\uFEFF', '\u200E', '\u200F',
        '\u2028', '\u2029', '\u00AD', '\u034F', '\u061C', '\u2060',
        '\u2061', '\u2062', '\u2063', '\u2064', '\u206A', '\u206B',
        '\u206C', '\u206D', '\u206E', '\u206F'
    ]);

    public static string Normalize(ReadOnlySpan<char> input)
    {
        if (input.IsEmpty)
            return string.Empty;

        string nfcNormalized = new string(input).Normalize(NormalizationForm.FormC);
        ReadOnlySpan<char> source = nfcNormalized.AsSpan();

        int maxLength = source.Length;
        char[]? rentedBuffer = null;
        Span<char> buffer = maxLength <= 512
            ? stackalloc char[maxLength]
            : (rentedBuffer = ArrayPool<char>.Shared.Rent(maxLength));

        try
        {
            int written = 0;
            bool previousWasWhitespace = false;

            for (int i = 0; i < source.Length; i++)
            {
                char current = source[i];

                if (InvisibleCharacters.Contains(current))
                    continue;

                if (char.IsWhiteSpace(current))
                {
                    if (!previousWasWhitespace && written > 0)
                    {
                        buffer[written++] = ' ';
                        previousWasWhitespace = true;
                    }
                    continue;
                }

                buffer[written++] = current;
                previousWasWhitespace = false;
            }

            if (written > 0 && buffer[written - 1] == ' ')
                written--;

            return new string(buffer[..written]);
        }
        finally
        {
            if (rentedBuffer is not null)
                ArrayPool<char>.Shared.Return(rentedBuffer);
        }
    }
}
