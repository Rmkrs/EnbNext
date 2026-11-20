using System.Buffers.Binary;
using EnbNext.Net7.Gameplay;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class Net7OrdinaryEffectShortReadTests
{
    [Theory]
    [InlineData(0, 0u, 0)]
    [InlineData(1, 0u, 0)]
    [InlineData(2, 0x44u, 0)]
    [InlineData(3, 0x3344u, 0)]
    [InlineData(4, 0x223344u, 0)]
    [InlineData(5, 0x11223344u, 0)]
    [InlineData(6, 0x11223344u, 0x66)]
    [InlineData(7, 0x11223344u, 0x5566)]
    public void UnaryShortPrefixRetainsInitializedFields(int length, uint owner, int definition)
    {
        var state = new Net7GameplayState();
        byte[] bytes = Convert.FromHexString("00443322116655");
        Assert.True(state.Apply(Packet.ForOpcode(9, bytes[..length])));
        var effect = Assert.Single(state.Effects).Value;
        Assert.Equal(owner, effect.SourceId);
        Assert.Equal((ushort)definition, effect.DefinitionId);
        Assert.Equal(1f, effect.Scale);
        Assert.Equal(0u, effect.EffectId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OptionalShortReadKeepsEarlierFieldsAndInitialFloatBytes(bool binary)
    {
        // All optional fields are selected. Stop two bytes into Scale: its
        // initialized high bytes remain 0x3F80, so the result is not zero-filled.
        int prefix = binary ? 13 : 7;
        int scaleOffset = prefix + 10 + (binary ? 16 : 0);
        byte[] bytes = new byte[scaleOffset + 2];
        bytes[0] = 0xFF;
        if (binary) bytes[1] = 0x0F;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(prefix), 0x12345678);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(prefix + 4), 0x87654321);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(prefix + 8), -1);
        bytes[scaleOffset] = 0xCD;
        bytes[scaleOffset + 1] = 0xAB;
        var state = new Net7GameplayState();
        Assert.True(state.Apply(Packet.ForOpcode(binary ? (ushort)11 : (ushort)9, bytes)));
        var effect = Assert.Single(state.Effects).Value;
        Assert.Equal(0x12345678u, effect.EffectId);
        Assert.Equal(0x87654321u, effect.Timestamp);
        Assert.True(effect.HasInfiniteDuration);
        Assert.Equal(0x3F80ABCDu, BitConverter.SingleToUInt32Bits(effect.Scale));
        Assert.Equal(System.Numerics.Vector3.Zero, effect.EffectOffset);
        Assert.Equal(1f, effect.EffectScalar);
    }

    [Fact]
    public void PartialEffectIdReplacesDefaultIdWithoutRollingBackOtherEffects()
    {
        var state = new Net7GameplayState();
        Assert.True(state.Apply(Packet.ForOpcode(9, new byte[7])));
        Assert.True(state.Apply(Packet.ForOpcode(9, Convert.FromHexString("010100000002007856"))));
        Assert.Equal(2, state.Effects.Count);
        Assert.Equal(1u, state.Effects[0x5678].SourceId);
        Assert.Equal((ushort)2, state.Effects[0x5678].DefinitionId);
    }
}
