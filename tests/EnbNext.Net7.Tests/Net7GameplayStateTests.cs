using System.Buffers.Binary;
using System.Text;
using EnbNext.Net7.Gameplay;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class Net7GameplayStateTests
{
    [Fact]
    public void CanonicalEffectsMaterializeAndRemoveByEffectId()
    {
        var state = new Net7GameplayState();
        byte[] unary = new byte[17];
        unary[0] = 0x07;
        BinaryPrimitives.WriteUInt32LittleEndian(unary.AsSpan(1, 4), 0x100);
        BinaryPrimitives.WriteUInt16LittleEndian(unary.AsSpan(5, 2), 0x22);
        BinaryPrimitives.WriteUInt32LittleEndian(unary.AsSpan(7, 4), 0x200);
        BinaryPrimitives.WriteUInt32LittleEndian(unary.AsSpan(11, 4), 0x300);
        BinaryPrimitives.WriteInt16LittleEndian(unary.AsSpan(15, 2), 400);
        Assert.True(state.Apply(Packet.ForOpcode(0x0009, unary)));
        Net7EffectState e = Assert.Single(state.Effects).Value;
        Assert.Equal(0x200u, e.EffectId);
        Assert.Equal(0x100u, e.SourceId);
        Assert.Equal(0x22, e.DefinitionId);
        Assert.Equal(400, e.DurationMilliseconds);

        byte[] remove = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(remove, 0x200);
        Assert.True(state.Apply(Packet.ForOpcode(0x000F, remove)));
        Assert.Empty(state.Effects);
    }

    [Fact]
    public void OrdinaryEffectsPreserveDefaultZeroEffectIdAndRemovalCanAddressIt()
    {
        var state = new Net7GameplayState();

        byte[] unary = new byte[7];
        BinaryPrimitives.WriteUInt32LittleEndian(unary.AsSpan(1, 4), 0x100);
        BinaryPrimitives.WriteUInt16LittleEndian(unary.AsSpan(5, 2), 0x22);
        Assert.True(state.Apply(Packet.ForOpcode(0x0009, unary)));
        Net7EffectState zeroUnary = Assert.Single(state.Effects).Value;
        Assert.Equal(0u, zeroUnary.EffectId);
        Assert.Equal(0u, zeroUnary.Timestamp);
        Assert.Equal(0, zeroUnary.DurationMilliseconds);
        Assert.Equal(1.0f, zeroUnary.Scale);
        Assert.Equal(System.Numerics.Vector3.Zero, zeroUnary.EffectOffset);

        byte[] removeZero = new byte[4];
        Assert.True(state.Apply(Packet.ForOpcode(0x000F, removeZero)));
        Assert.Empty(state.Effects);

        byte[] linked = new byte[13];
        BinaryPrimitives.WriteUInt32LittleEndian(linked.AsSpan(2, 4), 0x200);
        BinaryPrimitives.WriteUInt32LittleEndian(linked.AsSpan(6, 4), 0x300);
        BinaryPrimitives.WriteUInt16LittleEndian(linked.AsSpan(10, 2), 3);
        Assert.True(state.Apply(Packet.ForOpcode(0x000B, linked)));
        Net7EffectState zeroLinked = Assert.Single(state.Effects).Value;
        Assert.Equal(0u, zeroLinked.EffectId);
        Assert.True(zeroLinked.IsObjectToObject);

        Assert.True(state.Apply(Packet.ForOpcode(0x000F, removeZero)));
        Assert.Empty(state.Effects);
    }

    [Fact]
    public void SyntheticProspectEffectPreservesZeroEffectIdAndRemovalCanAddressIt()
    {
        var state = new Net7GameplayState();
        state.AddSyntheticEffect(new Net7EffectState(
            0,
            0x100,
            0x200,
            0x00BF,
            0x300,
            0,
            string.Empty,
            0x0003,
            true));

        Net7EffectState effect = Assert.Single(state.Effects).Value;
        Assert.Equal(0u, effect.EffectId);
        Assert.Equal(0x00BF, effect.DefinitionId);

        state.RemoveEffect(0);
        Assert.Empty(state.Effects);
    }

    [Fact]
    public void ObjectEffectAcceptsProvenMaskZeroProxyTrackerIdSuffixAndKeepsRetailDefaultEffectId()
    {
        var state = new Net7GameplayState();

        // Exact 0x0009 payload from the 2026-08-21 live sector-entry capture.
        // The final DWORD is consumed by current Net7Proxy's static-effect handler
        // even though current retail leaves it unread when optional-mask bit 0 is clear.
        byte[] payload = Convert.FromHexString("00A8860100584F20D61300");
        Assert.True(state.Apply(Packet.ForOpcode(0x0009, payload)));

        Net7EffectState effect = Assert.Single(state.Effects).Value;
        Assert.Equal(0u, effect.EffectId);
        Assert.Equal(0x000186A8u, effect.SourceId);
        Assert.Equal((ushort)0x4F58, effect.DefinitionId);
        Assert.Equal(0u, effect.Timestamp);
        Assert.Equal(0, effect.DurationMilliseconds);

        byte[] unusedTail = Convert.FromHexString("00A8860100584F20D613");
        Assert.True(state.Apply(Packet.ForOpcode(0x0009, unusedTail)));
        Assert.Equal(0u, Assert.Single(state.Effects).Value.EffectId);
    }

    [Theory]
    [InlineData(0x0009, 7)]
    [InlineData(0x000B, 13)]
    public void OrdinaryEffectTailDoesNotPreventRegistrationOrLaterRemoval(int opcode, int baseLength)
    {
        var state = new Net7GameplayState();
        byte[] payload = new byte[baseLength + 7];
        payload[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(baseLength), 123);
        payload[^3] = 0xAB;
        payload[^2] = 0xCD;
        payload[^1] = 0xEF;
        Assert.True(state.Apply(Packet.ForOpcode((ushort)opcode, payload)));
        Assert.Equal(123u, Assert.Single(state.Effects).Key);
        byte[] removal = [123, 0, 0, 0, 0xAB, 0xCD];
        Assert.True(state.Apply(Packet.ForOpcode(0x000F, removal)));
        Assert.Empty(state.Effects);
        Assert.True(state.Apply(Packet.ForOpcode((ushort)opcode, payload)));
        Assert.Equal(123u, Assert.Single(state.Effects).Key);
    }

    [Fact]
    public void ObjectEffectRetainsAllCanonicalOptionalPresentationFields()
    {
        var state = new Net7GameplayState();
        byte[] payload = new byte[33];
        payload[0] = 0x7F;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1, 4), 0x100);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(5, 2), 0x22);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(7, 4), 0x200);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(11, 4), 0x300);
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(15, 2), -1);
        WriteSingle(payload, 17, 1.5f);
        WriteSingle(payload, 21, 2f);
        WriteSingle(payload, 25, 3f);
        WriteSingle(payload, 29, 4f);

        Assert.True(state.Apply(Packet.ForOpcode(0x0009, payload)));
        Net7EffectState effect = Assert.Single(state.Effects).Value;
        Assert.True(effect.HasInfiniteDuration);
        Assert.Equal(1.5f, effect.Scale);
        Assert.Equal(new System.Numerics.Vector3(2f, 3f, 4f), effect.EffectOffset);
        Assert.Equal(System.Numerics.Vector3.Zero, effect.SpatialOffset);
        Assert.Equal(1.0f, effect.EffectScalar);
    }

    [Fact]
    public void ObjectToObjectEffectUsesExactOptionalOrderAndRetainsUnterminatedBone()
    {
        var state = new Net7GameplayState();
        byte[] bone = Encoding.Latin1.GetBytes("BONE\0");
        byte[] payload = new byte[12 + bone.Length + 4 + 4 + 2 + 12 + 2 + 2 + 4 + 4 + 4 + 4 + 4];
        const ushort mask = 0x0FFF; // all twelve bits; bit 5 is presence-only and consumes no bytes.
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), mask);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(2, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(6, 4), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(10, 2), 3);
        bone.CopyTo(payload, 12);
        int o = 12 + bone.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(o, 4), 4); o += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(o, 4), 5); o += 4;
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(o, 2), -6); o += 2;
        WriteSingle(payload, o, 1f); o += 4;
        WriteSingle(payload, o, 2f); o += 4;
        WriteSingle(payload, o, 3f); o += 4;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(o, 2), 0x1234); o += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(o, 2), 0x5678); o += 2;
        WriteSingle(payload, o, 1.25f); o += 4;
        WriteSingle(payload, o, 4f); o += 4;
        WriteSingle(payload, o, 5f); o += 4;
        WriteSingle(payload, o, 6f); o += 4;
        WriteSingle(payload, o, 0.75f); o += 4;
        Assert.Equal(payload.Length, o);

        Assert.True(state.Apply(Packet.ForOpcode(0x000B, payload)));
        Net7EffectState effect = Assert.Single(state.Effects).Value;
        Assert.Equal("BONE", effect.BoneName);
        Assert.Equal(4u, effect.EffectId);
        Assert.True(effect.IsObjectToObject);
        Assert.True(effect.HasInfiniteDuration);
        Assert.Equal(new System.Numerics.Vector3(1f, 2f, 3f), effect.SpatialOffset);
        Assert.Equal((ushort)0x1234, effect.EndpointShortA);
        Assert.Equal((ushort)0x5678, effect.EndpointShortB);
        Assert.True(effect.DescriptorFlag);
        Assert.Equal(1.25f, effect.Scale);
        Assert.Equal(new System.Numerics.Vector3(4f, 5f, 6f), effect.EffectOffset);
        Assert.Equal(0.75f, effect.EffectScalar);

        byte[] malformed = new byte[13];
        BinaryPrimitives.WriteUInt32LittleEndian(malformed.AsSpan(2, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(malformed.AsSpan(6, 4), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(malformed.AsSpan(10, 2), 3);
        malformed[12] = (byte)'X';
        var unterminated = new Net7GameplayState();
        Assert.True(unterminated.Apply(Packet.ForOpcode(0x000B, malformed)));
        Assert.Equal("X", Assert.Single(unterminated.Effects).Value.BoneName);
    }

    [Fact]
    public void TradeManufacturingMessageAndPresentationStateRemainNative()
    {
        var state = new Net7GameplayState();

        byte[] trade = new byte[5];
        BinaryPrimitives.WriteUInt32LittleEndian(trade.AsSpan(0, 4), 0x11223344);
        trade[4] = 2;
        Assert.True(state.Apply(Packet.ForOpcode(0x001F, trade)));
        Assert.Equal(new Net7TradeState(0x11223344, 2), state.Trade);

        byte[] mfg = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(mfg, 0x55667788);
        Assert.True(state.Apply(Packet.ForOpcode(0x007F, mfg)));
        state.SetRequestedManufacturingMode(Net7ManufacturingMode.Analyze);
        Assert.Equal(new Net7ManufacturingState(0x55667788, Net7ManufacturingMode.Analyze), state.Manufacturing);

        byte[] message = new byte[3 + 6];
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(0, 2), 6);
        message[2] = 8;
        Encoding.Latin1.GetBytes("hello\0").CopyTo(message, 3);
        Assert.True(state.Apply(Packet.ForOpcode(0x001D, message)));
        Assert.Equal(new Net7MessageState(8, "hello") { Sequence = 1 }, Assert.Single(state.Messages));

        byte[] type = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(type, 2);
        Assert.True(state.Apply(Packet.ForOpcode(0x003C, type)));
        Assert.Equal(2u, state.SectorType);
        Assert.Equal(5, state.PresentationMode);
        Assert.Equal(3, state.PresentationRefreshCountdown);
        Assert.True(state.PresentationNotificationPending);
        state.AdvancePresentationRefresh();
        Assert.Equal(2, state.PresentationRefreshCountdown);
        Assert.True(state.ConsumePresentationNotification());
        Assert.False(state.ConsumePresentationNotification());
    }

    [Fact]
    public void RepeatedValidModeAndHandoffRetainMeshResetNotifications()
    {
        var state = new Net7GameplayState();
        byte[] type = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(type, 1);
        state.Apply(Packet.ForOpcode(0x003C, type));
        Assert.Equal(1ul, state.MeshBufferResetRevision);
        state.ConsumePresentationNotification();
        state.Apply(Packet.ForOpcode(0x003C, type));
        Assert.Equal(1, state.PresentationMode);
        Assert.Equal(2ul, state.MeshBufferResetRevision);
        BinaryPrimitives.WriteUInt32LittleEndian(type, uint.MaxValue);
        state.Apply(Packet.ForOpcode(0x003C, type));
        Assert.Equal(2ul, state.MeshBufferResetRevision);
        state.ResetForServerHandoff();
        Assert.Equal(3ul, state.MeshBufferResetRevision);
        var builder = new EnbNext.Presentation.EnbPresentationSnapshotBuilder();
        var snapshot = builder.Build(new EnbNext.Net7.World.Net7WorldState(), effects: null,
            assets: null, clientTime: 0, gameplay: state);
        Assert.Equal(3ul, snapshot.MeshBufferResetRevision);
        state.Apply(Packet.ForOpcode(0x003C, new byte[4]));
        Assert.Equal(3ul, snapshot.MeshBufferResetRevision);
        Assert.Equal(4ul, state.MeshBufferResetRevision);
    }

    [Fact]
    public void CombatPresentationPacketsDoNotPretendToOwnPersistentHealthState()
    {
        var state = new Net7GameplayState();
        byte[] hull = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(hull.AsSpan(0, 4), 10);
        BinaryPrimitives.WriteUInt32LittleEndian(hull.AsSpan(4, 4), 20);
        Assert.True(state.Apply(Packet.ForOpcode(0x0059, hull)));
        Assert.True(state.Apply(Packet.ForOpcode(0x0059, Array.Empty<byte>())));
        Assert.True(state.Apply(Packet.ForOpcode(0x0059, new byte[1])));

        byte[] damage = new byte[24];
        BinaryPrimitives.WriteInt32LittleEndian(damage.AsSpan(0, 4), BitConverter.SingleToInt32Bits(10.5f));
        BinaryPrimitives.WriteInt32LittleEndian(damage.AsSpan(4, 4), BitConverter.SingleToInt32Bits(2f));
        BinaryPrimitives.WriteUInt32LittleEndian(damage.AsSpan(8, 4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(damage.AsSpan(12, 4), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(damage.AsSpan(16, 4), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(damage.AsSpan(20, 4), 5);
        Assert.True(state.Apply(Packet.ForOpcode(0x0064, damage)));
        Assert.Equal(new Net7DamagePresentation(10.5f, 2f, 2, 3, 4, 5), state.LastDamagePresentation);

        Assert.True(state.Apply(Packet.ForOpcode(0x0091, Encoding.Latin1.GetBytes("rescued\0"))));
        Net7MessageState rescue = Assert.Single(state.Messages);
        Assert.Equal((byte)5, rescue.Type);
        Assert.Equal("rescued", rescue.Text);
    }

    [Fact]
    public void GuildStateDecodesPromptPermissionsRanksAndRename()
    {
        var state = new Net7GameplayState();
        Assert.True(state.Apply(Packet.ForOpcode(0x00CC, BuildGuildSimplePrompt(16, "Pilot"))));
        Assert.Equal(new Net7GuildPrompt(0x00CC, 16, "Pilot", string.Empty), state.GuildPrompt);

        byte[] permissions = Convert.FromHexString("0102030405060708090A0B0C0D0E0F10");
        Assert.True(state.Apply(Packet.ForOpcode(0x00D2, permissions)));
        Assert.Equal(new Net7GuildPermissions(0x01020304, 0x05060708, 0x090A0B0C, 0x0D0E0F10), state.GuildPermissions);

        byte[] ranks = BuildRanks(("Leader", 1), ("Member", 5));
        Assert.True(state.Apply(Packet.ForOpcode(0x00D3, ranks)));
        Assert.Equal(new[] { new Net7GuildRankName("Leader", 1), new Net7GuildRankName("Member", 5) }, state.GuildRankNames);

        Assert.True(state.Apply(Packet.ForOpcode(0x00D8, BuildTwoSharedStrings("Old", "New"))));
        Assert.Equal(new Net7GuildPrompt(0x00D8, 15, "Old", "New"), state.GuildPrompt);
    }

    [Fact]
    public void GuildPromptsConsumeStringExtentsButDisplayOnlyTheirTerminatedNames()
    {
        var state = new Net7GameplayState();
        state.Apply(Packet.ForOpcode(0x00C8, BuildTwoSharedStrings("Pilot\0unused", "Guild\0unused")));
        Assert.Equal(new Net7GuildPrompt(0x00C8, 0, "Pilot", "Guild"), state.GuildPrompt);
        state.Apply(Packet.ForOpcode(0x00CC, BuildGuildSimplePrompt(16, "Pilot\0unused")));
        Assert.Equal(new Net7GuildPrompt(0x00CC, 16, "Pilot", ""), state.GuildPrompt);
        state.Apply(Packet.ForOpcode(0x00D8, BuildTwoSharedStrings("Old\0unused", "New\0unused")));
        Assert.Equal(new Net7GuildPrompt(0x00D8, 15, "Old", "New"), state.GuildPrompt);
        byte[] names = BuildTwoSharedStrings("Pilot\0unused", "Guild\0unused");
        state.Apply(Packet.ForOpcode(0x00D0, new byte[] {99, 0, 0, 0}.Concat(names).ToArray()));
        Assert.Equal(new Net7GuildMessage(99, "Pilot", "Guild"), state.GuildMessage);
        Assert.DoesNotContain("unused", state.Social.Messages[^1].Text);
        state.Apply(Packet.ForOpcode(0x00D0, new byte[] {99, 0, 0, 0}));
        Assert.Equal(new Net7GuildMessage(99, "", ""), state.GuildMessage);
        state.Apply(Packet.ForOpcode(0x00CC, new byte[] {16, 0, 0, 0}));
        Assert.Equal(new Net7GuildPrompt(0x00CC, 16, "", ""), state.GuildPrompt);
        state.Apply(Packet.ForOpcode(0x00D8, Array.Empty<byte>()));
        Assert.Equal(new Net7GuildPrompt(0x00D8, 15, "", ""), state.GuildPrompt);
    }

    [Fact]
    public void HandoffResetsTransientDomainStateButPreservesSectorPresentationAndGuildTables()
    {
        var state = new Net7GameplayState();
        byte[] sector = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(sector, 1);
        state.Apply(Packet.ForOpcode(0x003C, sector));
        state.AddSyntheticEffect(new Net7EffectState(1, 2, 3, 4, 5, 6, string.Empty, 0, false));
        state.AddCameraEvent(3, 4);
        state.SetRequestedManufacturingMode(Net7ManufacturingMode.Manufacture);

        byte[] permissions = Convert.FromHexString("0102030405060708090A0B0C0D0E0F10");
        state.Apply(Packet.ForOpcode(0x00D2, permissions));

        state.ResetForServerHandoff();

        Assert.Empty(state.Effects);
        Assert.Empty(state.CameraEvents);
        Assert.Equal(Net7ManufacturingMode.None, state.Manufacturing.RequestedMode);
        Assert.Equal(1u, state.SectorType);
        Assert.Equal(1, state.PresentationMode);
        Assert.NotNull(state.GuildPermissions);
    }

    private static byte[] BuildGuildSimplePrompt(uint type, string value)
    {
        byte[] text = Encoding.Latin1.GetBytes(value);
        byte[] result = new byte[6 + text.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0, 4), type);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(4, 2), checked((ushort)text.Length));
        text.CopyTo(result, 6);
        return result;
    }

    private static byte[] BuildRanks(params (string Name, uint Rank)[] entries)
    {
        using var stream = new MemoryStream();
        byte[] dword = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(dword, checked((uint)entries.Length));
        stream.Write(dword);
        foreach ((string name, uint rank) in entries)
        {
            byte[] text = Encoding.Latin1.GetBytes(name);
            byte[] length = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(length, checked((ushort)text.Length));
            stream.Write(length);
            stream.Write(text);
            BinaryPrimitives.WriteUInt32BigEndian(dword, rank);
            stream.Write(dword);
        }
        return stream.ToArray();
    }

    private static byte[] BuildTwoSharedStrings(string a, string b)
    {
        using var stream = new MemoryStream();
        byte[] length = new byte[2];
        foreach (string value in new[] { a, b })
        {
            byte[] text = Encoding.Latin1.GetBytes(value);
            BinaryPrimitives.WriteUInt16LittleEndian(length, checked((ushort)text.Length));
            stream.Write(length);
            stream.Write(text);
        }
        return stream.ToArray();
    }
    private static void WriteSingle(byte[] payload, int offset, float value)
        => BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset, 4), BitConverter.SingleToInt32Bits(value));

}
