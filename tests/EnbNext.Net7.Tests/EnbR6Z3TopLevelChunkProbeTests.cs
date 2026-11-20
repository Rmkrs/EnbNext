using System.Buffers.Binary;
using EnbNext.Assets;

namespace EnbNext.Net7.Tests;

public sealed class EnbR6Z3TopLevelChunkProbeTests
{
    [Fact]
    public void ProbeReportsEveryTopLevelChunkContainingAuthoredMemberToken()
    {
        byte[] bytes = Concat(
            Chunk(0x0500u, true, "definition:PWEAPEXHAUST\0"u8.ToArray()),
            Chunk(0x0700u, true, "hlod-member:pweapexhaust\0"u8.ToArray()),
            Chunk(0x0100u, true, "unrelated"u8.ToArray()));

        Assert.True(EnbW3dTopLevelChunkProbe.TryInspect(
            bytes,
            "PWEAPEXHAUST",
            out EnbW3dTopLevelChunkProbeResult result,
            out string? diagnostic), diagnostic);

        Assert.Equal(new uint[] { 0x0500u, 0x0700u, 0x0100u }, result.Chunks.Select(static chunk => chunk.Type));
        Assert.Equal(new uint[] { 0x0500u, 0x0700u }, result.Matches.Select(static match => match.ChunkType));
        Assert.All(result.Matches, static match => Assert.True(match.IsContainer));
    }

    [Fact]
    public void ProbeFailsClosedForTruncatedTopLevelChunk()
    {
        byte[] bytes = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), 0x0500u);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 0x8000_0010u);

        Assert.False(EnbW3dTopLevelChunkProbe.TryInspect(
            bytes,
            "PWEAPEXHAUST",
            out _,
            out string? diagnostic));
        Assert.Contains("resource boundary", diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] Chunk(uint type, bool container, byte[] payload)
    {
        byte[] bytes = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), type);
        uint size = (uint)payload.Length | (container ? 0x8000_0000u : 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), size);
        payload.CopyTo(bytes, 8);
        return bytes;
    }

    private static byte[] Concat(params byte[][] values)
    {
        byte[] result = new byte[values.Sum(static value => value.Length)];
        int offset = 0;
        foreach (byte[] value in values)
        {
            value.CopyTo(result, offset);
            offset += value.Length;
        }
        return result;
    }
}
