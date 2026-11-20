using System.Buffers.Binary;
using System.Text;
using EnbNext.Net7.Protocol;

namespace EnbNext.Net7.Tests;

public sealed class Net7SectorJoinWireTests
{
    [Fact]
    public void SuccessfulSectorJoinClientData_MatchesCanonicalOffsets()
    {
        Net7SectorJoinClientData join = Net7SectorJoinClientData.CreateSuccessful(
            0x12345678, 1079, 2);
        byte[] wire = join.ToWireBytes();

        Assert.Equal(0x40, wire.Length);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, wire.AsSpan(0x0C, 4).ToArray());
        Assert.Equal(0x12345678u, BinaryPrimitives.ReadUInt32BigEndian(wire.AsSpan(0x10, 4)));
        Assert.Equal(1079u, BinaryPrimitives.ReadUInt32BigEndian(wire.AsSpan(0x14, 4)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(0x1C, 4)));
        Assert.Equal("MY_Avatar_Ticket", Encoding.ASCII.GetString(wire, 0x2C, 16));
        Assert.Equal(new byte[4], wire.AsSpan(0x3C, 4).ToArray());
    }

    [Fact]
    public void SectorLoginPayload_IsExact0x89()
    {
        byte[] payload = Net7SectorLoginWire.Build(
            Net7SectorJoinClientData.CreateSuccessful(1, 1079, 0),
            0xAABBCCDD);

        Assert.Equal(0x89, payload.Length);
        Assert.Equal(0xAABBCCDDu, BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0x40, 4)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0x85, 4)));
    }
    [Fact]
    public void SectorLoginGmName_TruncatesAt64BytesAndKeepsExplicitTerminator()
    {
        byte[] payload = Net7SectorLoginWire.Build(
            Net7SectorJoinClientData.CreateSuccessful(1, 1079, 0),
            0x01020304,
            new string('G', 80));

        Assert.All(payload.AsSpan(0x44, 0x40).ToArray(), b => Assert.Equal((byte)'G', b));
        Assert.Equal(0, payload[0x84]);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0x85, 4)));
    }

}
