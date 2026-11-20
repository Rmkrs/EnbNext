using EnbNext.Net7.Reliability;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class Net7DecodedSpecialOwnershipTests
{
    [Fact]
    public void LegacyProxyShortAuxGuardUsesCompleteDecodedInnerLengthBoundary()
    {
        Assert.True(Net7DecodedSpecialOwnership.IsLegacyProxyMalformedShortAuxData(Packet.ForOpcode(0x001B, new byte[0])));
        Assert.True(Net7DecodedSpecialOwnership.IsLegacyProxyMalformedShortAuxData(Packet.ForOpcode(0x001B, new byte[3])));
        Assert.False(Net7DecodedSpecialOwnership.IsLegacyProxyMalformedShortAuxData(Packet.ForOpcode(0x001B, new byte[4])));
        Assert.False(Net7DecodedSpecialOwnership.IsLegacyProxyMalformedShortAuxData(Packet.ForOpcode(0x001C, new byte[0])));
    }

    [Theory]
    [InlineData(0x2012)]
    [InlineData(0x2013)]
    [InlineData(0x2014)]
    [InlineData(0x2025)]
    [InlineData(0x202A)]
    public void LaterDomainSpecialsRemainValidDecodedInnerOwners(int opcode)
        => Assert.True(Net7DecodedSpecialOwnership.IsGameplayDomainSpecial(checked((ushort)opcode)));

    [Theory]
    [InlineData(0x2010)]
    [InlineData(0x2018)]
    [InlineData(0x2019)]
    [InlineData(0x2016)]
    [InlineData(0x201A)]
    [InlineData(0x2027)]
    [InlineData(0x202C)]
    [InlineData(0x3005)]
    [InlineData(0x3007)]
    public void DirectOuterOnlyCollisionsAreNotGameplayDomainDecodedSpecials(int opcode)
        => Assert.False(Net7DecodedSpecialOwnership.IsGameplayDomainSpecial(checked((ushort)opcode)));
}
