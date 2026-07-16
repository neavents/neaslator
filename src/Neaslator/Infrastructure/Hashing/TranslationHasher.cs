using System.Buffers;
using System.IO.Hashing;
using System.Text;

namespace Neaslator.Infrastructure.Hashing;

public static class TranslationHasher
{
    public static long ComputeHash(ReadOnlySpan<char> normalizedText)
    {
        if (normalizedText.IsEmpty)
            return 0;

        int maxByteCount = Encoding.UTF8.GetMaxByteCount(normalizedText.Length);
        byte[]? rentedBuffer = null;
        Span<byte> utf8Buffer = maxByteCount <= 1024
            ? stackalloc byte[maxByteCount]
            : (rentedBuffer = ArrayPool<byte>.Shared.Rent(maxByteCount));

        try
        {
            int bytesWritten = Encoding.UTF8.GetBytes(normalizedText, utf8Buffer);
            ulong hash = XxHash3.HashToUInt64(utf8Buffer[..bytesWritten]);
            return unchecked((long)hash);
        }
        finally
        {
            if (rentedBuffer is not null)
                ArrayPool<byte>.Shared.Return(rentedBuffer);
        }
    }
}
