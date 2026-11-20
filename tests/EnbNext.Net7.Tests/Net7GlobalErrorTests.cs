using System.Buffers.Binary;
using System.Text;
using EnbNext.Net7.Protocol;

namespace EnbNext.Net7.Tests;

public sealed class Net7GlobalErrorTests
{
    [Theory]
    [InlineData(0u, 7u, "Error: You have been temporarily banned.")]
    [InlineData(1u, 8u, "Sorry, that name has already been taken. Please try again.")]
    [InlineData(2u, 9u, "Sorry, your name can only contain the letters a-z. No spaces or other special characters are allowed.  Please try again.")]
    [InlineData(3u, 10u, "Sorry, this name is too short. It must contain at least 3 characters. Please try again.")]
    [InlineData(4u, 11u, "Sorry, this name needs enough vowels (a,e,i,o,u & y) to be pronouncable. Please try again.")]
    [InlineData(5u, 12u, "Sorry, there are too many repeating characters in this name. Please try again.")]
    [InlineData(6u, 13u, "Sorry, this name contains a reserved or illegal word. Please try again.")]
    [InlineData(7u, 14u, "Error: Ticket Validation Failed.")]
    [InlineData(8u, 15u, "Error: Authentication Server (AUTHD) is unavailable.  Try again in a few minutes.")]
    [InlineData(9u, 16u, "Error: You have not compleated registration.")]
    [InlineData(10u, 17u, "Sorry, that ship name is not allowed. Please try again")]
    [InlineData(11u, 18u, "Sorry, Net7 experienced an internal error. Please submit a bug report")]
    public void Outer200E_MapsKnownLittleEndianSelector(uint selector, uint expectedCode, string expectedMessage)
    {
        byte[] payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, selector);

        Net7GlobalError error = Net7GlobalError.DecodeOuterUdp200E(payload);

        Assert.Equal(Net7GlobalErrorSource.OuterUdp200E, error.Source);
        Assert.Equal(selector, error.Selector);
        Assert.Equal(expectedCode, error.Code);
        Assert.Equal(expectedMessage, error.Message);
    }

    [Fact]
    public void Outer200E_UnknownSelectorIsBoundedStructuredUnknown()
    {
        byte[] payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 1234);

        Net7GlobalError error = Net7GlobalError.DecodeOuterUdp200E(payload);

        Assert.Equal(1234u, error.Selector);
        Assert.Null(error.Code);
        Assert.Contains("1234", error.Message);
    }

    [Fact]
    public void Outer200E_RequiresSelectorField()
    {
        Assert.Throws<InvalidDataException>(() => Net7GlobalError.DecodeOuterUdp200E(new byte[3]));
    }

    [Fact]
    public void Direct0075_UsesTwoBigEndianFieldsAndNulTerminatedLatin1Message()
    {
        byte[] payload = Net7GlobalError.EncodeDirect0075(0x01020304, 0xAABBCCDD, "Denied é");
        Assert.Equal(Convert.FromHexString("01020304AABBCCDD"), payload.AsSpan(0, 8).ToArray());
        Net7GlobalError error = Net7GlobalError.DecodeDirect0075(payload);

        Assert.Equal(Net7GlobalErrorSource.Direct0075, error.Source);
        Assert.Equal(0x01020304u, error.OpaqueField0);
        Assert.Equal(0xAABBCCDDu, error.Code);
        Assert.Equal("Denied é", error.Message);
        Assert.False(error.MalformedTerminator);
        Assert.Equal(0, payload[^1]);
    }

    [Fact]
    public void Direct0075_DoesNotImposeLegacyCodeRange()
    {
        byte[] payload = Net7GlobalError.EncodeDirect0075(0, 0xFFFF1234, "future");
        Net7GlobalError error = Net7GlobalError.DecodeDirect0075(payload);
        Assert.Equal(0xFFFF1234u, error.Code);
    }


    [Fact]
    public void Direct0075_PreservesIngressAndSessionContextForDiagnostics()
    {
        byte[] payload = Net7GlobalError.EncodeDirect0075(0x11223344, 14, "X");

        Net7GlobalError error = Net7GlobalError.DecodeDirect0075(
            payload,
            ingress: Net7GlobalErrorIngress.DecodedInner,
            sessionContext: Net7GlobalErrorSessionContext.SectorEntry);

        Assert.Equal(Net7GlobalErrorSource.Direct0075, error.Source);
        Assert.Equal(Net7GlobalErrorIngress.DecodedInner, error.Ingress);
        Assert.Equal(Net7GlobalErrorSessionContext.SectorEntry, error.SessionContext);
        Assert.Equal(0x11223344u, error.OpaqueField0);
        Assert.Equal(14u, error.Code);
    }

    [Fact]
    public void Direct0075_UnterminatedMessageKeepsAvailableText()
    {
        byte[] payload = new byte[9];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4, 4), 2);
        payload[8] = (byte)'x';

        Net7GlobalError audited = Net7GlobalError.DecodeDirect0075(payload);
        Assert.True(audited.MalformedTerminator);
        Assert.Equal("x", audited.Message);
    }

    [Fact]
    public void AccountTcp0075_RecognizesOnlyExactFixedMessageCompatibilityFraming()
    {
        const string message = "Sorry, there are too many repeating characters in this name. Please try again.";
        byte[] text = Encoding.Latin1.GetBytes(message);
        byte[] payload = new byte[text.Length + 8];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)text.Length);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), 12);
        text.CopyTo(payload, 8);

        Assert.True(Net7GlobalError.TryDecodeAccountTcp0075(payload, out Net7GlobalError error));
        Assert.Equal(Net7GlobalErrorSource.AccountTcp0075, error.Source);
        Assert.Equal(Net7GlobalErrorIngress.ServerTcpRole10, error.Ingress);
        Assert.Equal(12u, error.Code);
        Assert.Equal(message, error.Message);

        payload[^1] = (byte)'!';
        Assert.False(Net7GlobalError.TryDecodeAccountTcp0075(payload, out _));
        payload[^1] = (byte)'.';
        BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)text.Length + 1);
        Assert.False(Net7GlobalError.TryDecodeAccountTcp0075(payload, out _));
        Assert.False(Net7GlobalError.TryDecodeAccountTcp0075(
            Net7GlobalError.EncodeDirect0075(0, 12, message), out _));
    }
}
