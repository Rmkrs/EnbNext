using System.Buffers.Binary;
using System.Text;
using EnbNext.Net7.Protocol;

namespace EnbNext.Net7.Tests;

public sealed class Net7AccountWireTests
{
    [Fact]
    public void AuthLoginEscaping_MatchesCurrentExactByteSetAndUppercaseHex()
    {
        const string input = "a b!\"#$%&*+,/:;<=>?@[\\]^`{|}~_-AZ09";
        string escaped = Net7AccountWire.EscapeAuthLoginCredential(input);

        Assert.Equal(
            "a%20b%21%22%23%24%25%26%2A%2B%2C%2F%3A%3B%3C%3D%3E%3F%40%5B%5C%5D%5E%60%7B%7C%7D%7E_-AZ09",
            escaped);
    }

    [Fact]
    public void AccountLoginPayload_UsesEscapedTokensWithLittleEndianLengthPrefixes()
    {
        byte[] payload = Net7AccountWire.BuildAccountLoginPayload("user name", "p+a ss");
        ReadOnlySpan<byte> span = payload;

        ushort userLength = BinaryPrimitives.ReadUInt16LittleEndian(span[..2]);
        string userToken = Encoding.Latin1.GetString(span.Slice(2, userLength));
        int passwordLengthOffset = 2 + userLength;
        ushort passwordLength = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(passwordLengthOffset, 2));
        string passwordToken = Encoding.Latin1.GetString(span.Slice(passwordLengthOffset + 2, passwordLength));

        Assert.Equal("user%20name", userToken);
        Assert.Equal("p%2Ba%20ss", passwordToken);
        Assert.Equal(payload.Length, passwordLengthOffset + 2 + passwordLength);
    }

    [Fact]
    public void Authenv04Ownership_ProjectsParsedTicketThroughRetailCStringSemantics()
    {
        Assert.Equal("pilot-token", Net7AccountWire.OwnEaTicketFromAuthLoginTicket("pilot-token\0ignored"));
        Assert.Equal(string.Empty, Net7AccountWire.OwnEaTicketFromAuthLoginTicket("\0ignored"));
        Assert.Equal("pilot", Net7AccountWire.OwnEaTicketFromAuthLoginTicket("pilot\0\u20AC"));
        Assert.Equal(new string('x', 255), Net7AccountWire.OwnEaTicketFromAuthLoginTicket(new string('x', 255) + "\0ignored"));
        Assert.Equal(new string('x', 256), Net7AccountWire.OwnEaTicketFromAuthLoginTicket(new string('x', 256) + "\0ignored"));
        // 0076A7B0: CopyCStringBounded(EATicket, 0x101) keeps the first 0x100 bytes; the assertion only logs.
        Assert.Equal(new string('x', 256), Net7AccountWire.OwnEaTicketFromAuthLoginTicket(new string('x', 300)));
    }

    [Fact]
    public void AvatarListRequest_IsFullEaTicketByteForByteWithoutPrefixOrNul()
    {
        const string ticket = "pilot-token-rest";
        byte[] payload = Net7AccountWire.BuildAvatarListRequest(ticket);

        Assert.Equal(Encoding.Latin1.GetBytes(ticket), payload);
        Assert.DoesNotContain((byte)0, payload);
    }

    [Fact]
    public void AvatarListRequest_AllowsEmptyTicketAtWireLayer()
    {
        Assert.Empty(Net7AccountWire.BuildAvatarListRequest(string.Empty));
    }

    [Theory]
    [InlineData("pilot-rest", "pilot")]
    [InlineData("---pilot-rest", "pilot")]
    [InlineData("pilot", "pilot")]
    [InlineData("-pilot", "pilot")]
    public void TicketIdentity_UsesFirstNonEmptyHyphenDelimitedToken(string ticket, string expected)
    {
        Assert.Equal(expected, Net7AccountWire.ExtractAvatarLoginUsernameFromEaTicket(ticket));
    }

    [Theory]
    [InlineData("")]
    [InlineData("-")]
    [InlineData("----")]
    public void TicketIdentity_DelimiterOnlyOrEmptyHasNoToken(string ticket)
    {
        // Net7Proxy 00401170: strtok returns NULL and 00414240 copies nothing.
        Assert.Null(Net7AccountWire.ExtractAvatarLoginUsernameFromEaTicket(ticket));
    }

    [Fact]
    public void AvatarLoginPayload_UsesBigEndianSlotAndLittleEndianStringLength()
    {
        byte[] payload = Net7AccountWire.BuildAvatarLoginPayload(3, "pilot");

        Assert.Equal(3u, BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(0, 4)));
        Assert.Equal(5, BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(4, 2)));
        Assert.Equal("pilot", Encoding.Latin1.GetString(payload.AsSpan(6)));
    }

    [Fact]
    public void AvatarLoginPayload_RejectsSlotsOutsideCurrentFiveSlotRoster()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Net7AccountWire.BuildAvatarLoginPayload(-1, "pilot"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Net7AccountWire.BuildAvatarLoginPayload(5, "pilot"));
    }

    [Fact]
    public void BytePreservingFields_RejectEmbeddedNulOrNonLatin1()
    {
        Assert.Throws<ArgumentException>(() => Net7AccountWire.BuildAvatarListRequest("abc\0def"));
        Assert.Throws<ArgumentException>(() => Net7AccountWire.BuildAccountLoginPayload("abc\u20AC", "pw"));
    }
    [Theory]
    [InlineData(Net7FeatureFlags.UdpPacketOptimisation, 0x01)]
    [InlineData(Net7FeatureFlags.SpMode, 0x02)]
    [InlineData(Net7FeatureFlags.SectorCaching, 0x04)]
    [InlineData(Net7FeatureFlags.UdpPacketOptimisation | Net7FeatureFlags.SpMode | Net7FeatureFlags.SectorCaching, 0x07)]
    public void MasterLoginPayload_AdvertisesKnownCapabilityBitsInOptionalFifthByte(
        Net7FeatureFlags flags,
        byte expected)
    {
        byte[] payload = Net7AccountWire.BuildMasterLoginPayload(1079, flags);
        Assert.Equal(5, payload.Length);
        Assert.Equal(1079u, BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0, 4)));
        Assert.Equal(expected, payload[4]);
    }

    [Fact]
    public void MasterLoginPayload_OmitsFeatureByteWhenNoCapabilitiesAreEnabled()
    {
        byte[] payload = Net7AccountWire.BuildMasterLoginPayload(1079, Net7FeatureFlags.None);
        Assert.Equal(4, payload.Length);
        Assert.Equal(1079u, BinaryPrimitives.ReadUInt32LittleEndian(payload));
    }

    [Fact]
    public void MasterLoginPayload_RejectsUnknownCapabilityBits()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Net7AccountWire.BuildMasterLoginPayload(1079, (Net7FeatureFlags)0x08));
    }

}
