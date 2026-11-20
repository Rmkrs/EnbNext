using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;

namespace EnbNext.Assets;

/// <summary>
/// Small read-only structural probe for top-level W3D chunks. R6Z3 uses this
/// only to correlate an authored HLOD member name with the top-level prototype
/// chunk(s) that contain the same ASCII token. It does not decode or classify
/// the payload and therefore cannot turn an unsupported W3D family into a
/// renderable EnbNext model by itself.
/// </summary>
public static class EnbW3dTopLevelChunkProbe
{
    private const uint ContainerFlag = 0x8000_0000u;
    private const uint SizeMask = 0x7FFF_FFFFu;
    private const int ChunkHeaderLength = 8;

    public static bool TryInspect(
        ReadOnlySpan<byte> bytes,
        string asciiNeedle,
        out EnbW3dTopLevelChunkProbeResult result,
        out string? diagnostic)
    {
        result = default!;
        diagnostic = null;
        ArgumentException.ThrowIfNullOrWhiteSpace(asciiNeedle);

        byte[] needle = Encoding.ASCII.GetBytes(asciiNeedle);
        if (needle.Length == 0)
        {
            diagnostic = "ASCII probe token is empty.";
            return false;
        }

        var chunks = ImmutableArray.CreateBuilder<EnbW3dTopLevelChunkInfo>();
        var matches = ImmutableArray.CreateBuilder<EnbW3dTopLevelAsciiMatch>();
        int offset = 0;
        while (offset < bytes.Length)
        {
            if (bytes.Length - offset < ChunkHeaderLength)
            {
                diagnostic = $"Top-level W3D chunk header is truncated at 0x{offset:X}.";
                return false;
            }

            uint type = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));
            uint sizeAndFlags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 4, 4));
            uint payloadLengthU32 = sizeAndFlags & SizeMask;
            if (payloadLengthU32 > int.MaxValue)
            {
                diagnostic = $"Top-level W3D chunk 0x{type:X8} has an unsupported payload length 0x{payloadLengthU32:X8}.";
                return false;
            }

            int payloadLength = (int)payloadLengthU32;
            int payloadOffset = offset + ChunkHeaderLength;
            if (payloadLength > bytes.Length - payloadOffset)
            {
                diagnostic = $"Top-level W3D chunk 0x{type:X8} at 0x{offset:X} extends past the resource boundary.";
                return false;
            }

            bool isContainer = (sizeAndFlags & ContainerFlag) != 0;
            chunks.Add(new EnbW3dTopLevelChunkInfo(type, isContainer, offset, payloadOffset, payloadLength));

            ReadOnlySpan<byte> payload = bytes.Slice(payloadOffset, payloadLength);
            int searchStart = 0;
            while (searchStart <= payload.Length - needle.Length)
            {
                int relative = IndexOfAsciiIgnoreCase(payload.Slice(searchStart), needle);
                if (relative < 0)
                    break;

                relative += searchStart;
                matches.Add(new EnbW3dTopLevelAsciiMatch(type, isContainer, offset, payloadOffset + relative));
                searchStart = relative + Math.Max(1, needle.Length);
            }

            offset = payloadOffset + payloadLength;
        }

        if (offset != bytes.Length)
        {
            diagnostic = $"Top-level W3D chunk walk ended at 0x{offset:X}, not resource length 0x{bytes.Length:X}.";
            return false;
        }

        result = new EnbW3dTopLevelChunkProbeResult(chunks.ToImmutable(), matches.ToImmutable());
        return true;
    }

    private static int IndexOfAsciiIgnoreCase(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        for (int start = 0; start <= haystack.Length - needle.Length; start++)
        {
            bool equal = true;
            for (int i = 0; i < needle.Length; i++)
            {
                if (ToUpperAscii(haystack[start + i]) == ToUpperAscii(needle[i]))
                    continue;
                equal = false;
                break;
            }

            if (equal)
                return start;
        }

        return -1;
    }

    private static byte ToUpperAscii(byte value)
        => value is >= (byte)'a' and <= (byte)'z' ? (byte)(value - 0x20) : value;
}

public sealed record EnbW3dTopLevelChunkProbeResult(
    ImmutableArray<EnbW3dTopLevelChunkInfo> Chunks,
    ImmutableArray<EnbW3dTopLevelAsciiMatch> Matches);

public readonly record struct EnbW3dTopLevelChunkInfo(
    uint Type,
    bool IsContainer,
    int HeaderOffset,
    int PayloadOffset,
    int PayloadLength);

public readonly record struct EnbW3dTopLevelAsciiMatch(
    uint ChunkType,
    bool IsContainer,
    int ChunkHeaderOffset,
    int MatchOffset);
