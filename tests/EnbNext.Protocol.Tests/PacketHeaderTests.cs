using EnbNext.Protocol.Net;

namespace EnbNext.Protocol.Tests;

public sealed class PacketHeaderTests
{
    [Fact]
    public void ReadsLittleEndianFrameHeader()
    {
        PacketHeader header = PacketHeader.Read(new byte[] { 0x14, 0x00, 0x2C, 0x00 });
        Assert.Equal(20, header.Size);
        Assert.Equal(0x002C, header.Opcode);
    }
}
