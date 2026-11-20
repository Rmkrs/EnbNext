using System.Buffers.Binary;
using System.Text;

namespace EnbNext.Assets;

/// <summary>
/// Read-only census of the three HAnim top-level families handled directly by
/// the current retail WW3D asset manager. This probe intentionally stops at
/// authored headers: it does not claim that compressed/adaptive channel payloads
/// are executable by EnbNext.
/// </summary>
public static class EnbW3dHAnimHeaderProbe
{
    private const uint ContainerFlag = 0x8000_0000u;
    private const uint SizeMask = 0x7FFF_FFFFu;
    private const int ChunkHeaderLength = 8;

    public static bool TryInspect(
        ReadOnlySpan<byte> bytes,
        out IReadOnlyList<EnbW3dHAnimHeaderInfo> headers,
        out string? diagnostic)
    {
        headers = Array.Empty<EnbW3dHAnimHeaderInfo>();
        diagnostic = null;

        var found = new List<EnbW3dHAnimHeaderInfo>();
        int offset = 0;
        // 0091D270 stops when 009D4310 cannot read a complete eight-byte
        // top-level header. A short trailing fragment cannot register a HAnim.
        while (bytes.Length - offset >= ChunkHeaderLength)
        {
            if (!TryReadChunk(bytes, offset, bytes.Length, out ChunkView top))
            {
                diagnostic = $"Top-level W3D HAnim census encountered an invalid chunk at 0x{offset:X}.";
                return false;
            }

            if (top.Type is 0x0200u or 0x0280u or 0x02C0u)
            {
                // 00964930 dispatches by type; 009D4310 opens the child using
                // the masked parent length without requiring its container bit.
                if (!TryReadChunk(bytes, top.PayloadStart, top.PayloadEnd, out ChunkView headerChunk))
                {
                    diagnostic = $"Current-retail HAnim top-level chunk 0x{top.Type:X4} at 0x{offset:X} has no complete first child header.";
                    return false;
                }

                ReadOnlySpan<byte> payload = bytes.Slice(headerChunk.PayloadStart, headerChunk.PayloadLength);
                if (!TryDecodeHeader(top.Type, headerChunk.Type, payload, offset, out EnbW3dHAnimHeaderInfo header, out diagnostic))
                    return false;

                found.Add(header);
            }

            offset = top.PayloadEnd;
        }

        headers = found.ToArray();
        return true;
    }

    private static bool TryDecodeHeader(
        uint family,
        uint childType,
        ReadOnlySpan<byte> payload,
        int topLevelOffset,
        out EnbW3dHAnimHeaderInfo header,
        out string? diagnostic)
    {
        header = default!;
        diagnostic = null;

        if (family == 0x0200u)
        {
            if (childType != 0x0201u || payload.Length != 44)
            {
                diagnostic = $"Classic 0x0200 HAnim at 0x{topLevelOffset:X} does not begin with the retail 0x0201/44-byte header.";
                return false;
            }

            uint numFrames = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(36, 4));
            uint frameRate = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(40, 4));
            header = new EnbW3dHAnimHeaderInfo(
                EnbW3dHAnimFamily.Classic0200,
                BinaryPrimitives.ReadUInt32LittleEndian(payload[..4]),
                ReadFixedAscii(payload.Slice(4, 16)),
                ReadFixedAscii(payload.Slice(20, 16)),
                numFrames,
                frameRate,
                CompressionFlavor: null,
                TopLevelOffset: topLevelOffset);
            return true;
        }

        if (family == 0x0280u)
        {
            if (childType != 0x0281u || payload.Length != 44)
            {
                diagnostic = $"Compressed 0x0280 HAnim at 0x{topLevelOffset:X} does not begin with the retail 0x0281/44-byte header.";
                return false;
            }

            uint numFrames = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(36, 4));
            ushort frameRate = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(40, 2));
            ushort flavor = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(42, 2));
            header = new EnbW3dHAnimHeaderInfo(
                EnbW3dHAnimFamily.Compressed0280,
                BinaryPrimitives.ReadUInt32LittleEndian(payload[..4]),
                ReadFixedAscii(payload.Slice(4, 16)),
                ReadFixedAscii(payload.Slice(20, 16)),
                numFrames,
                frameRate,
                CompressionFlavor: flavor,
                TopLevelOffset: topLevelOffset);
            return true;
        }

        // Current retail FUN_00987CC0 consumes the first 0x02C0 child as a
        // 48-byte header without comparing the child type first. Preserve that
        // behavior rather than inventing a stronger 0x02C1 requirement.
        if (payload.Length < 48)
        {
            diagnostic = $"0x02C0 HAnim at 0x{topLevelOffset:X} has fewer than the 48 header bytes consumed by current retail.";
            return false;
        }

        uint adaptiveNumFrames = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(36, 4));
        float adaptiveFrameRate = BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(40, 4));
        header = new EnbW3dHAnimHeaderInfo(
            EnbW3dHAnimFamily.Adaptive02C0,
            BinaryPrimitives.ReadUInt32LittleEndian(payload[..4]),
            ReadFixedAscii(payload.Slice(4, 16)),
            ReadFixedAscii(payload.Slice(20, 16)),
            adaptiveNumFrames,
            adaptiveFrameRate,
            CompressionFlavor: null,
            TopLevelOffset: topLevelOffset);
        return true;
    }

    private static bool TryReadChunk(
        ReadOnlySpan<byte> bytes,
        int offset,
        int end,
        out ChunkView chunk)
    {
        chunk = default;
        if (offset < 0 || end < offset || end > bytes.Length || end - offset < ChunkHeaderLength)
            return false;

        uint type = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));
        uint sizeAndFlags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 4, 4));
        uint payloadLengthU32 = sizeAndFlags & SizeMask;
        if (payloadLengthU32 > int.MaxValue)
            return false;

        int payloadLength = (int)payloadLengthU32;
        int payloadStart = offset + ChunkHeaderLength;
        if (payloadLength > end - payloadStart)
            return false;

        chunk = new ChunkView(
            type,
            (sizeAndFlags & ContainerFlag) != 0,
            payloadStart,
            payloadLength,
            payloadStart + payloadLength);
        return true;
    }

    private static string ReadFixedAscii(ReadOnlySpan<byte> bytes)
    {
        int length = bytes.IndexOf((byte)0);
        if (length < 0)
            length = bytes.Length;
        return Encoding.ASCII.GetString(bytes[..length]);
    }

    private readonly record struct ChunkView(
        uint Type,
        bool IsContainer,
        int PayloadStart,
        int PayloadLength,
        int PayloadEnd);
}

public enum EnbW3dHAnimFamily : uint
{
    Classic0200 = 0x0200,
    Compressed0280 = 0x0280,
    Adaptive02C0 = 0x02C0,
}

public sealed record EnbW3dHAnimHeaderInfo(
    EnbW3dHAnimFamily Family,
    uint Version,
    string Name,
    string HierarchyName,
    uint NumFrames,
    float FrameRate,
    ushort? CompressionFlavor,
    int TopLevelOffset)
{
    public string RegistryName => HierarchyName + "." + Name;
}
