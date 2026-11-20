using EnbNext.Net7.Protocol;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class Net7AvatarSelectionResponseTests
{
    [Theory]
    [InlineData(0x006F)]
    [InlineData(0x2009)]
    public void BothCertifiedRole10ResponseOpcodesAreAccepted(ushort opcode)
    {
        Packet packet = TestProtocolFixtures.BuildPacket(
            opcode,
            TestProtocolFixtures.BuildAvatarSelectionPayload(0x80000001, 0xAABBCCDD));

        Net7AvatarSelectionResponse response = Net7AvatarSelectionResponse.DecodeAndValidate(packet);

        Assert.Equal(opcode, response.ResponseOpcode);
        Assert.Equal(0xAABBCCDDu, response.FixedSessionId);
        Assert.Equal(new byte[] { 1, 0, 0, 0x80 }, response.OpaqueResultBytes);
    }

    [Fact]
    public void OpaqueResultOnlyTreatsAllFFAsFailure_NotSignedHighBit()
    {
        Packet accepted = TestProtocolFixtures.BuildPacket(
            0x006F,
            TestProtocolFixtures.BuildAvatarSelectionPayload(0x80000000, 0x01020304));
        Net7AvatarSelectionResponse.DecodeAndValidate(accepted);

        Packet failed = TestProtocolFixtures.BuildPacket(
            0x006F,
            TestProtocolFixtures.BuildAvatarSelectionPayload(0xFFFFFFFF, 0x01020304));
        Assert.Throws<Net7AvatarSelectionRejectedException>(() => Net7AvatarSelectionResponse.DecodeAndValidate(failed));
    }

    [Fact]
    public void ZeroFixedIdOrShortPayloadIsRejectedBeforeCommit()
    {
        Packet zero = TestProtocolFixtures.BuildPacket(
            0x2009,
            TestProtocolFixtures.BuildAvatarSelectionPayload(0, 0));
        Assert.Throws<Net7AvatarSelectionRejectedException>(() => Net7AvatarSelectionResponse.DecodeAndValidate(zero));

        Packet shortPacket = TestProtocolFixtures.BuildPacket(0x2009, new byte[0x17]);
        Assert.Throws<InvalidDataException>(() => Net7AvatarSelectionResponse.DecodeAndValidate(shortPacket));
    }

    [Fact]
    public void OtherOpcodesCannotMasqueradeAsSelectionResponse()
    {
        Packet packet = TestProtocolFixtures.BuildPacket(
            0x2001,
            TestProtocolFixtures.BuildAvatarSelectionPayload(0, 0x01020304));
        Assert.Throws<InvalidDataException>(() => Net7AvatarSelectionResponse.DecodeAndValidate(packet));
    }
}
