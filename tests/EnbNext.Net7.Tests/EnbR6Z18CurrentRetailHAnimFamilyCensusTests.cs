using System.Buffers.Binary;
using System.Text;
using EnbNext.Assets;

namespace EnbNext.Net7.Tests;

public sealed class EnbR6Z18CurrentRetailHAnimFamilyCensusTests
{
    [Fact]
    public void Compressed0280HeaderPreservesCurrentRetailRegistryIdentityAndFlavor()
    {
        byte[] bytes = BuildHAnimContainer(
            topType: 0x0280,
            childType: 0x0281,
            childPayload: Build0280Header("TSG02A-1", "TSG02A", numFrames: 31, frameRate: 30, flavor: 0));

        Assert.True(EnbW3dHAnimHeaderProbe.TryInspect(
            bytes,
            out IReadOnlyList<EnbW3dHAnimHeaderInfo> headers,
            out string? diagnostic), diagnostic);

        EnbW3dHAnimHeaderInfo header = Assert.Single(headers);
        Assert.Equal(EnbW3dHAnimFamily.Compressed0280, header.Family);
        Assert.Equal("TSG02A-1", header.Name);
        Assert.Equal("TSG02A", header.HierarchyName);
        Assert.Equal("TSG02A.TSG02A-1", header.RegistryName);
        Assert.Equal(31u, header.NumFrames);
        Assert.Equal(30f, header.FrameRate);
        Assert.Equal((ushort?)0, header.CompressionFlavor);
        Assert.Equal(0, header.TopLevelOffset);
    }

    [Fact]
    public void Adaptive02C0HeaderUsesFloatFrameRateAndDoesNotInventChildTypeGate()
    {
        byte[] bytes = BuildHAnimContainer(
            topType: 0x02C0,
            childType: 0x7F7F,
            childPayload: Build02C0Header("DOOR-OPEN", "DOOR", numFrames: 17, frameRate: 29.5f));

        Assert.True(EnbW3dHAnimHeaderProbe.TryInspect(
            bytes,
            out IReadOnlyList<EnbW3dHAnimHeaderInfo> headers,
            out string? diagnostic), diagnostic);

        EnbW3dHAnimHeaderInfo header = Assert.Single(headers);
        Assert.Equal(EnbW3dHAnimFamily.Adaptive02C0, header.Family);
        Assert.Equal("DOOR.DOOR-OPEN", header.RegistryName);
        Assert.Equal(17u, header.NumFrames);
        Assert.Equal(29.5f, header.FrameRate);
        Assert.Null(header.CompressionFlavor);
    }

    [Fact]
    public void Classic0200HeaderCensusMatchesEstablishedRegistryIdentity()
    {
        byte[] bytes = BuildHAnimContainer(
            topType: 0x0200,
            childType: 0x0201,
            childPayload: Build0200Header("TSG02-1", "TSG02", numFrames: 12, frameRate: 24));

        Assert.True(EnbW3dHAnimHeaderProbe.TryInspect(
            bytes,
            out IReadOnlyList<EnbW3dHAnimHeaderInfo> headers,
            out string? diagnostic), diagnostic);

        EnbW3dHAnimHeaderInfo header = Assert.Single(headers);
        Assert.Equal(EnbW3dHAnimFamily.Classic0200, header.Family);
        Assert.Equal("TSG02.TSG02-1", header.RegistryName);
        Assert.Equal(12u, header.NumFrames);
        Assert.Equal(24f, header.FrameRate);
    }

    private static byte[] Build0200Header(string name, string hierarchyName, uint numFrames, uint frameRate)
    {
        byte[] header = new byte[44];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), 0x0004_0002u);
        WriteFixedAscii(header.AsSpan(4, 16), name);
        WriteFixedAscii(header.AsSpan(20, 16), hierarchyName);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(36, 4), numFrames);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40, 4), frameRate);
        return header;
    }

    private static byte[] Build0280Header(
        string name,
        string hierarchyName,
        uint numFrames,
        ushort frameRate,
        ushort flavor)
    {
        byte[] header = new byte[44];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), 0x0004_0002u);
        WriteFixedAscii(header.AsSpan(4, 16), name);
        WriteFixedAscii(header.AsSpan(20, 16), hierarchyName);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(36, 4), numFrames);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(40, 2), frameRate);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(42, 2), flavor);
        return header;
    }

    private static byte[] Build02C0Header(string name, string hierarchyName, uint numFrames, float frameRate)
    {
        byte[] header = new byte[48];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), 0x0004_0002u);
        WriteFixedAscii(header.AsSpan(4, 16), name);
        WriteFixedAscii(header.AsSpan(20, 16), hierarchyName);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(36, 4), numFrames);
        BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(40, 4), frameRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(44, 4), 3u);
        return header;
    }

    private static byte[] BuildHAnimContainer(uint topType, uint childType, byte[] childPayload)
    {
        byte[] child = new byte[8 + childPayload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(child.AsSpan(0, 4), childType);
        BinaryPrimitives.WriteUInt32LittleEndian(child.AsSpan(4, 4), (uint)childPayload.Length);
        childPayload.CopyTo(child.AsSpan(8));

        byte[] top = new byte[8 + child.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(top.AsSpan(0, 4), topType);
        BinaryPrimitives.WriteUInt32LittleEndian(top.AsSpan(4, 4), 0x8000_0000u | (uint)child.Length);
        child.CopyTo(top.AsSpan(8));
        return top;
    }

    private static void WriteFixedAscii(Span<byte> destination, string value)
    {
        byte[] encoded = Encoding.ASCII.GetBytes(value);
        Assert.True(encoded.Length < destination.Length);
        encoded.CopyTo(destination);
        destination[encoded.Length] = 0;
    }
}
