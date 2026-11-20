using System.Buffers.Binary;
using System.Text;

namespace EnbNext.Assets;

/// <summary>
/// Minimal, read-only W3D structure probe used by the native presentation path.
/// It intentionally extracts only geometry counts and referenced texture names;
/// rendering semantics remain a separate concern.
/// </summary>
public static class EnbW3dProbe
{
    private const uint MeshChunk = 0x0000;
    private const uint MeshHeader3Chunk = 0x001F;
    private const uint TextureNameChunk = 0x0032;
    private const uint ContainerFlag = 0x8000_0000;
    private const uint SizeMask = 0x7FFF_FFFF;
    private const int ChunkHeaderLength = 8;
    private const int MeshHeader3Length = 116;
    private const int MaximumDepth = 32;

    public static bool TryInspect(ReadOnlySpan<byte> bytes, out EnbW3dProbeResult result)
    {
        result = default!;
        if (bytes.Length < ChunkHeaderLength)
            return false;

        var state = new ProbeState();
        if (!Walk(bytes, 0, bytes.Length, 0, state) || !state.SawMesh || !state.SawMeshHeader)
            return false;

        result = new EnbW3dProbeResult(
            state.MeshName ?? string.Empty,
            state.VertexCount,
            state.TriangleCount,
            state.BoundingRadius,
            state.TextureNames.ToArray());
        return true;
    }

    private static bool Walk(
        ReadOnlySpan<byte> bytes,
        int start,
        int end,
        int depth,
        ProbeState state)
    {
        if (depth > MaximumDepth || start < 0 || end < start || end > bytes.Length)
            return false;

        int offset = start;
        while (offset < end)
        {
            if (end - offset < ChunkHeaderLength)
                return false;

            uint chunkType = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));
            uint rawSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 4, 4));
            bool isContainer = (rawSize & ContainerFlag) != 0;
            uint chunkSize = rawSize & SizeMask;

            if (chunkSize > int.MaxValue)
                return false;

            int payloadStart = offset + ChunkHeaderLength;
            int payloadEnd;
            try
            {
                payloadEnd = checked(payloadStart + (int)chunkSize);
            }
            catch (OverflowException)
            {
                return false;
            }

            if (payloadEnd > end)
                return false;

            if (chunkType == MeshChunk)
                state.SawMesh = true;

            if (chunkType == MeshHeader3Chunk)
            {
                ReadOnlySpan<byte> payload = bytes.Slice(payloadStart, (int)chunkSize);
                if (payload.Length < MeshHeader3Length)
                    return false;

                if (!state.SawMeshHeader)
                {
                    state.SawMeshHeader = true;
                    state.MeshName = ReadFixedAscii(payload.Slice(8, 16));
                    uint triangleCount = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(40, 4));
                    uint vertexCount = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(44, 4));
                    if (triangleCount > int.MaxValue || vertexCount > int.MaxValue)
                        return false;

                    state.TriangleCount = (int)triangleCount;
                    state.VertexCount = (int)vertexCount;
                    state.BoundingRadius = ReadSingle(payload.Slice(112, 4));
                    if (!float.IsFinite(state.BoundingRadius) || state.BoundingRadius <= 0f)
                        state.BoundingRadius = 0f;
                }
            }
            else if (chunkType == TextureNameChunk)
            {
                string textureName = ReadNullTerminatedAscii(bytes.Slice(payloadStart, (int)chunkSize));
                if (!string.IsNullOrWhiteSpace(textureName) &&
                    !state.TextureNames.Contains(textureName, StringComparer.OrdinalIgnoreCase))
                {
                    state.TextureNames.Add(textureName);
                }
            }

            if (isContainer && !Walk(bytes, payloadStart, payloadEnd, depth + 1, state))
                return false;

            offset = payloadEnd;
        }

        return offset == end;
    }

    private static float ReadSingle(ReadOnlySpan<byte> bytes)
        => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes));

    private static string ReadFixedAscii(ReadOnlySpan<byte> bytes)
    {
        int zero = bytes.IndexOf((byte)0);
        ReadOnlySpan<byte> value = zero >= 0 ? bytes[..zero] : bytes;
        return Encoding.ASCII.GetString(value).Trim();
    }

    private static string ReadNullTerminatedAscii(ReadOnlySpan<byte> bytes)
    {
        int zero = bytes.IndexOf((byte)0);
        ReadOnlySpan<byte> value = zero >= 0 ? bytes[..zero] : bytes;
        return Encoding.ASCII.GetString(value).Trim();
    }

    private sealed class ProbeState
    {
        public bool SawMesh { get; set; }
        public bool SawMeshHeader { get; set; }
        public string? MeshName { get; set; }
        public int VertexCount { get; set; }
        public int TriangleCount { get; set; }
        public float BoundingRadius { get; set; }
        public List<string> TextureNames { get; } = [];
    }
}

public sealed record EnbW3dProbeResult(
    string MeshName,
    int VertexCount,
    int TriangleCount,
    float BoundingRadius,
    IReadOnlyList<string> TextureNames);
