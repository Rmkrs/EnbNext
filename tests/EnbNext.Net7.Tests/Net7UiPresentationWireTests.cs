using System.Buffers.Binary;
using System.Text;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.Presentation;
using EnbNext.Net7.Protocol;

namespace EnbNext.Net7.Tests;

public sealed class Net7UiPresentationWireTests
{
    [Theory]
    [InlineData("80000000")]
    [InlineData("FFFFFFFF")]
    public void NegativeVerbCountsAreEmptyAndFollowingUpdatesStillApply(string count)
    {
        byte[] list = Convert.FromHexString("00000007" + count);
        Assert.Empty(Net7UiPresentationWire.DecodeVerbList(list).Verbs);
        byte[] update = Convert.FromHexString("00000007" + count + "0000000100000100");
        var decoded = Net7UiPresentationWire.DecodeVerbUpdate(update);
        Assert.Empty(decoded.Drop);
        Assert.Equal(new uint[] { 0x00010000 }, decoded.Add);
        var state = new EnbNext.Net7.Gameplay.Net7GameplayState();
        Assert.True(state.Apply(EnbNext.Protocol.Net.Packet.ForOpcode(0x005B, list)));
        Assert.True(state.Apply(EnbNext.Protocol.Net.Packet.ForOpcode(0x005C, update)));
        Assert.Equal(new uint[] { 0x00010000 }, state.VerbLists[7]);
    }

    [Fact]
    public void MessageLineFamiliesPreserveCStringAndRawLittleEndianFields()
    {
        byte[] payload = Build(w =>
        {
            w.CString("Incoming message");
            w.CString("MISSION");
            w.U32(0x11223344);
            w.U32(0x55667788);
        });

        foreach ((ushort opcode, Net7MessageLineKind kind) in new[]
        {
            ((ushort)0x0020, Net7MessageLineKind.Priority),
            ((ushort)0x0021, Net7MessageLineKind.Queue),
            ((ushort)0x0022, Net7MessageLineKind.Push),
        })
        {
            Net7MessageLineUpdate update = Net7UiPresentationWire.DecodeMessageLine(payload, opcode);
            Assert.Equal(kind, update.Kind);
            Assert.Equal("Incoming message", update.Text);
            Assert.Equal("MISSION", update.EphemeralType);
            Assert.Equal(0x11223344u, update.TimeMilliseconds);
            Assert.Equal(0x55667788u, update.DisplayPriority);
        }

        Assert.Equal("", Net7UiPresentationWire.DecodeSuperLine(new byte[] { 0 }).Text);
        Net7UiPresentationWire.DecodeClearMessageLine(Array.Empty<byte>());
        Net7UiPresentationWire.DecodeClearMessageLine(new byte[] { 0 });
        Assert.Equal(0x00667788u, Net7UiPresentationWire.DecodeMessageLine(payload[..^1], 0x0020).DisplayPriority);
        Assert.Throws<ArgumentOutOfRangeException>(() => Net7UiPresentationWire.DecodeMessageLine(payload, 0x001F));
    }

    [Fact]
    public void StarbaseSetTowAndRoomUpdateUseExactCurrentFixedGeometry()
    {
        byte[] set = Convert.FromHexString("443322110203");
        Assert.Equal(new Net7StarbaseSetUpdate(0x11223344, 2, 3), Net7UiPresentationWire.DecodeStarbaseSet(set));

        byte[] tow = new byte[24];
        for (int i = 0; i < 6; i++)
            WriteU32(tow, i * 4, (uint)(i + 1));
        Net7StarbaseTowUpdate towUpdate = Net7UiPresentationWire.DecodeStarbaseTow(tow);
        Assert.Equal(
            new[]
            {
                new Net7StarbaseTowOption(1, 4),
                new Net7StarbaseTowOption(2, 5),
                new Net7StarbaseTowOption(3, 6),
            },
            towUpdate.Options);

        byte[] room = new byte[12];
        WriteU32(room, 0, 0xAABBCCDD);
        WriteU32(room, 4, 7);
        WriteU32(room, 8, 6);
        Assert.Equal(new Net7StarbaseRoomUpdate(0xAABBCCDD, 7, 6), Net7UiPresentationWire.DecodeStarbaseRoomUpdate(room));

        Assert.Throws<InvalidDataException>(() => Net7UiPresentationWire.DecodeStarbaseSet(set[..^1]));
        Assert.Equal(towUpdate.Options, Net7UiPresentationWire.DecodeStarbaseTow(tow.Concat(new byte[] { 0 }).ToArray()).Options);
    }

    [Fact]
    public void LoungeNpcReconstructsRoomsFacilitiesNpcsAndSharedAvatarData()
    {
        byte[] avatar = MinimalAvatar("Jenquai");
        byte[] payload = Build(w =>
        {
            w.U32(0x01020304); // neutral StarbaseDefinitionValue
            w.U32(2); // rooms
            w.U32(10); w.U32(0x101); w.U32(1); w.U32(2); w.U32(3); w.U32(4); w.U32(5);
            w.U32(20); w.U32(0x0AA); w.U32(6); w.U32(7); w.U32(8); w.U32(9); w.U32(10);
            w.U32(1); // facilities
            w.U32(10); w.U32(3); w.U32(42); w.U32(0xDEADBEEF);
            w.U32(1); // NPCs
            w.U32(20); w.U32(4); w.U32(0xABCDEF01); w.U32(7); w.U32(8);
            byte[] name = Encoding.Latin1.GetBytes("Quartermaster\0PAD");
            w.U32((uint)name.Length); w.Bytes(name); w.Bytes(avatar);
        });

        Net7LoungeDefinitionUpdate update = Net7UiPresentationWire.DecodeLoungeNpc(payload);
        Assert.Equal(0x01020304u, update.StarbaseDefinitionValue);
        Assert.Equal(2, update.Rooms.Count);
        Assert.Equal(new uint[] { 1, 2, 3, 4, 5 }, update.Rooms[0].Parameters);
        Assert.Equal(new[] { true, false, false, false, false, false, false, false, true }, update.Rooms[0].CapabilityBits);
        Assert.Equal(0xDEADBEEFu, update.Facilities.Single().FourthField);
        Net7LoungeNpc npc = update.Npcs.Single();
        Assert.Equal(0xABCDEF01u, npc.Field2);
        Assert.Equal("Quartermaster", npc.Name);
        Assert.Equal("Jenquai", npc.Avatar.Skeleton);
        Assert.Equal(avatar.Length, Net7AvatarRosterCodec.DecodeAvatarData(avatar).Consumed);

        Net7LoungeNpc partial = Net7UiPresentationWire.DecodeLoungeNpc(payload[..^1]).Npcs.Single();
        Assert.Equal(npc.Name, partial.Name);
        Assert.Equal(npc.Avatar.Skeleton, partial.Avatar.Skeleton);
        Assert.Equal(npc.Avatar.HeadWeight, partial.Avatar.HeadWeight);

        // Two declared rooms with no room bytes: the first room read is
        // undefined, so nothing after the starbase value is kept.
        var overcounted = Net7UiPresentationWire.DecodeLoungeNpc(Build(w => { w.U32(1); w.U32(2); }));
        Assert.Equal(1u, overcounted.StarbaseDefinitionValue);
        Assert.Empty(overcounted.Rooms);
    }

    [Fact]
    public void LoungeCountsShareOneZeroedLocalAndShortRecordsKeepThePrefix()
    {
        // 008978D0: an absent room count is the zeroed local; absent facility
        // and NPC counts keep the previous count.
        var bare = Net7UiPresentationWire.DecodeLoungeNpc(Build(w => w.U32(9)));
        Assert.Equal(9u, bare.StarbaseDefinitionValue);
        Assert.Empty(bare.Rooms); Assert.Empty(bare.Facilities); Assert.Empty(bare.Npcs);
        Assert.Throws<InvalidDataException>(() => Net7UiPresentationWire.DecodeLoungeNpc(new byte[3]));

        // One room, then the payload ends: the facility count keeps 1 and its
        // first record read is undefined, so decoding stops after the room.
        var oneRoom = Net7UiPresentationWire.DecodeLoungeNpc(Build(w =>
        {
            w.U32(9); w.U32(1);
            w.U32(10); w.U32(0x101); w.U32(1); w.U32(2); w.U32(3); w.U32(4); w.U32(5);
        }));
        Assert.Single(oneRoom.Rooms);
        Assert.Empty(oneRoom.Facilities);

        // Zero rooms and an absent facility count: facilities and NPCs are 0.
        var zero = Net7UiPresentationWire.DecodeLoungeNpc(Build(w => { w.U32(9); w.U32(0); }));
        Assert.Empty(zero.Rooms); Assert.Empty(zero.Facilities); Assert.Empty(zero.Npcs);
    }

    [Fact]
    public void TalkTreeResponseCountIsSignedAndAbsentIdsRetainThePreviousId()
    {
        // 008A6CA0: signed char count; response IDs share one zeroed local.
        Assert.Empty(Net7UiPresentationWire.DecodeTalkTree(Build(w => { w.CString("Hi"); w.CString(""); w.U8(0x80); w.U32(1); w.CString("A"); })).Responses);
        var responses = Net7UiPresentationWire.DecodeTalkTree(Build(w => { w.CString("Hi"); w.CString(""); w.U8(3); w.U32(0x11223344); w.CString("A"); w.Bytes([0x55]); })).Responses;
        Assert.Equal(new[] { (0x11223344u, "A"), (0x11223355u, ""), (0x11223355u, "") }, responses.Select(r => (r.ResponseActionOrId, r.Text)));
        Assert.Equal(new[] { 0u }, Net7UiPresentationWire.DecodeTalkTree(Build(w => { w.CString("Hi"); w.CString(""); w.U8(1); })).Responses.Select(r => r.ResponseActionOrId));
        Assert.Throws<InvalidDataException>(() => Net7UiPresentationWire.DecodeTalkTree(Build(w => { w.CString("Hi"); w.CString(""); })));
    }

    [Fact]
    public void CountedArraysCoverZeroOneAndMultipleBoundaries()
    {
        byte[] zeroTalk = Build(w => { w.CString(""); w.CString(""); w.U8(0); });
        Assert.Empty(Net7UiPresentationWire.DecodeTalkTree(zeroTalk).Responses);

        byte[] oneVerb = Build(w => { w.NetworkU32(0x01020304); w.NetworkU32(1); w.U32(0xAABBCCDD); });
        Net7VerbListUpdate one = Net7UiPresentationWire.DecodeVerbList(oneVerb);
        Assert.Equal(0x01020304u, one.GameId);
        Assert.Equal(new uint[] { 0xAABBCCDD }, one.Verbs);

        byte[] manyTalk = Build(w =>
        {
            w.CString("Greetings"); w.CString("npc.wav"); w.U8(3);
            w.U32(11); w.CString("One");
            w.U32(22); w.CString("Two");
            w.U32(33); w.CString("Three");
        });
        Net7TalkTreeUpdate many = Net7UiPresentationWire.DecodeTalkTree(manyTalk);
        Assert.Equal(3, many.Responses.Count);
        Assert.Equal(33u, many.Responses[2].ResponseActionOrId);

        byte[] verbs = Build(w =>
        {
            w.NetworkU32(0x11121314);
            w.NetworkU32(2); w.U32(7); w.U32(8);
            w.NetworkU32(3); w.U32(9); w.U32(10); w.U32(11);
        });
        Net7VerbUpdate manyVerbs = Net7UiPresentationWire.DecodeVerbUpdate(verbs);
        Assert.Equal(new uint[] { 7, 8 }, manyVerbs.Drop);
        Assert.Equal(new uint[] { 9, 10, 11 }, manyVerbs.Add);
    }

    [Fact]
    public void DialogInterfaceMessageTimeAndClickIndicatorKeepBoundedRawGeometry()
    {
        byte[] time = Convert.FromHexString("0744332211");
        Assert.Equal(new Net7MessageTimeUpdate(7, 0x11223344), Net7UiPresentationWire.DecodeMessageTime(time));

        byte[] dialog = Build(w => { w.U16(3); w.U32(0x55667788); w.Bytes(Convert.FromHexString("AABBCC")); });
        Net7DialogUpdate decodedDialog = Net7UiPresentationWire.DecodeDialog(dialog);
        Assert.Equal((ushort)3, decodedDialog.ByteLength);
        Assert.Equal(0x55667788u, decodedDialog.Type);
        Assert.Equal(Convert.FromHexString("AABBCC"), decodedDialog.Data);
        byte[] malformedDialog = (byte[])dialog.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(malformedDialog.AsSpan(0, 2), 4);
        Assert.Throws<InvalidDataException>(() => Net7UiPresentationWire.DecodeDialog(malformedDialog));

        byte[] open = new byte[8]; WriteU32(open, 0, 5); WriteU32(open, 4, 6);
        Assert.Equal(new Net7OpenInterfaceUpdate(5, 6), Net7UiPresentationWire.DecodeOpenInterface(open));

        byte[] click = new byte[5]; click[0] = 2; WriteU32(click, 1, 0xCAFEBABE);
        Net7ClickIndicatorUpdate indicator = Net7UiPresentationWire.DecodeClickIndicator(click);
        Assert.True(indicator.IsOn);
        Assert.Equal(0xCAFEBABEu, indicator.GameId);
    }

    [Fact]
    public void GadgetControlInboundIsDistinctFromFinalDerived0068OutboundFraming()
    {
        byte[] name = Encoding.Latin1.GetBytes("NAVMAP\0");
        byte[] inbound = Build(w => { w.U16((ushort)name.Length); w.U32(0x10203040); w.Bytes(name); });
        Net7GadgetControlUpdate decoded = Net7UiPresentationWire.DecodeGadgetControl(inbound);
        Assert.Equal((ushort)7, decoded.NameSize);
        Assert.Equal(0x10203040u, decoded.Function);
        Assert.Equal("NAVMAP", decoded.Name);
        Assert.Equal(name, decoded.NameBytes);

        byte[] outbound = Net7GameplayWire.BuildGadgetControl("NAVMAP", 0x10203040, 0x11223344);
        Assert.Equal(inbound.Length + 4, outbound.Length);
        Assert.Equal(inbound, outbound[..inbound.Length]);
        Assert.Equal(Convert.FromHexString("44332211"), outbound[^4..]);
        Assert.Equal(decoded.NameBytes, Net7UiPresentationWire.DecodeGadgetControl(outbound).NameBytes);
    }

    [Fact]
    public void ConfirmedActionsPreserveOfferTypeAndUseSharedString()
    {
        foreach (uint type in new uint[] { 0, 1 })
        {
            byte[] offer = Build(w => { w.NetworkU32(type); w.NetworkU32(0x01020304); w.SharedString("Proceed?"); });
            Net7ConfirmedActionOffer decoded = Net7UiPresentationWire.DecodeConfirmedActionOffer(offer);
            Assert.Equal(type, decoded.Type);
            Assert.Equal(0x01020304u, decoded.Id);
            Assert.Equal("Proceed?", decoded.Message);

            byte[] timeout = Build(w => { w.NetworkU32(type); w.NetworkU32(0x01020304); });
            Assert.Equal(new Net7ConfirmedActionTimeout(type, 0x01020304), Net7UiPresentationWire.DecodeConfirmedActionTimeout(timeout));
        }

        byte[] badOffer = Build(w => { w.NetworkU32(2); w.NetworkU32(1); w.SharedString(""); });
        byte[] badTimeout = Build(w => { w.NetworkU32(2); w.NetworkU32(1); });
        Assert.Equal(2u, Net7UiPresentationWire.DecodeConfirmedActionOffer(badOffer).Type);
        Assert.Throws<InvalidDataException>(() => Net7UiPresentationWire.DecodeConfirmedActionTimeout(badTimeout));

        byte[] badSharedString = Build(w => { w.NetworkU32(0); w.NetworkU32(1); w.U16(5); w.Bytes(Encoding.Latin1.GetBytes("no")); });
        Assert.Throws<InvalidDataException>(() => Net7UiPresentationWire.DecodeConfirmedActionOffer(badSharedString));
    }

    [Fact]
    public void AllTwentyUiFamiliesIgnoreUnusedFrameTail()
    {
        var cases = new Dictionary<ushort, byte[]>
        {
            [0x0020] = Build(w => { w.CString("M"); w.CString("T"); w.U32(1); w.U32(2); }),
            [0x0021] = Build(w => { w.CString("M"); w.CString("T"); w.U32(1); w.U32(2); }),
            [0x0022] = Build(w => { w.CString("M"); w.CString("T"); w.U32(1); w.U32(2); }),
            [0x0023] = new byte[] { (byte)'S', 0 },
            [0x0024] = Array.Empty<byte>(),
            [0x004F] = new byte[6],
            [0x0050] = new byte[24],
            [0x0052] = Build(w => { w.U32(1); w.U32(0); w.U32(0); w.U32(0); }),
            [0x0054] = new byte[] { 0, 0, 0 },
            [0x0056] = new byte[4],
            [0x005B] = Build(w => { w.NetworkU32(1); w.NetworkU32(0); }),
            [0x005C] = Build(w => { w.NetworkU32(1); w.NetworkU32(0); w.NetworkU32(0); }),
            [0x0060] = new byte[5],
            [0x0062] = new byte[6],
            [0x0066] = new byte[8],
            [0x0067] = new byte[6],
            [0x0069] = new byte[5],
            [0x00A0] = new byte[12],
            [0x00BE] = Build(w => { w.NetworkU32(0); w.NetworkU32(1); w.SharedString(""); }),
            [0x00BF] = Build(w => { w.NetworkU32(0); w.NetworkU32(1); }),
        };
        Assert.Equal(20, cases.Count);

        foreach ((ushort opcode, byte[] valid) in cases)
        {
            Decode(opcode, valid);
            if (valid.Length > 0 && opcode is not (0x0020 or 0x0021 or 0x0022 or 0x0023 or 0x0052 or 0x00BE or 0x00BF))
                Assert.ThrowsAny<Exception>(() => Decode(opcode, valid[..^1]));
            Decode(opcode, valid.Concat(new byte[] { 0xEE }).ToArray());
        }
    }

    [Fact]
    public void MessageStringsAndInitializedFieldsKeepAvailablePrefix()
    {
        foreach (ushort opcode in new ushort[] { 0x0020, 0x0021, 0x0022 })
        {
            var message = Net7UiPresentationWire.DecodeMessageLine(Encoding.Latin1.GetBytes("Message"), opcode);
            Assert.Equal("Message", message.Text);
            Assert.Empty(message.EphemeralType);
            Assert.Equal(0u, message.TimeMilliseconds);
            Assert.Equal(0u, message.DisplayPriority);
        }
        Assert.Equal("Super", Net7UiPresentationWire.DecodeSuperLine(Encoding.Latin1.GetBytes("Super")).Text);
        byte[] offer = Build(w => { w.NetworkU32(0); w.NetworkU32(1); w.SharedString("Visible\0unused"); });
        Assert.Equal("Visible", Net7UiPresentationWire.DecodeConfirmedActionOffer(offer).Message);
    }

    internal static byte[] MinimalAvatar(string skeleton = "")
    {
        byte[] skeletonBytes = Encoding.Latin1.GetBytes(skeleton);
        byte[] avatar = new byte[Net7AvatarRosterCodec.AvatarDataBaseSize + skeletonBytes.Length];
        skeletonBytes.CopyTo(avatar, Net7AvatarRosterCodec.AvatarSkeletonOffset);
        avatar[Net7AvatarRosterCodec.AvatarSkeletonOffset + skeletonBytes.Length] = 0;
        return avatar;
    }

    [Fact]
    public void OvercountedVerbRecordsRetainTheNativeRecordLocal()
    {
        // 008AF150: count 5, one full verb and two bytes of a second. The second
        // overlays its bytes on the retained local; the exhausted iterations then
        // re-insert that value into the native set.
        byte[] list = Convert.FromHexString("00000007" + "00000005" + "01000200" + "0300");
        Assert.Equal(new uint[] { 0x00020001, 0x00020003 }, Net7UiPresentationWire.DecodeVerbList(list).Verbs);

        // A verb read before any retained value reads uninitialized storage.
        Assert.Throws<InvalidDataException>(() => Net7UiPresentationWire.DecodeVerbList(Convert.FromHexString("00000007" + "00000002" + "0100")));

        // 008AFD10 shares that local across both lists: an add record retains the
        // last drop verb.
        byte[] update = Convert.FromHexString("00000007" + "00000001" + "05000100" + "00000003" + "0600");
        var decoded = Net7UiPresentationWire.DecodeVerbUpdate(update);
        Assert.Equal(new uint[] { 0x00010005 }, decoded.Drop);
        Assert.Equal(new uint[] { 0x00010006 }, decoded.Add);

        var state = new EnbNext.Net7.Gameplay.Net7GameplayState();
        Assert.True(state.Apply(EnbNext.Protocol.Net.Packet.ForOpcode(0x005B, list)));
        // 00659240 replaces the existing slot of the same high-word type.
        Assert.Equal(new uint[] { 0x00020003 }, state.VerbLists[7]);
    }

    internal static byte[] Build(Action<WireWriter> write)
    {
        var writer = new WireWriter();
        write(writer);
        return writer.ToArray();
    }

    internal static void WriteU32(byte[] data, int offset, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), value);

    private static void Decode(ushort opcode, byte[] payload)
    {
        switch (opcode)
        {
            case 0x0020:
            case 0x0021:
            case 0x0022: _ = Net7UiPresentationWire.DecodeMessageLine(payload, opcode); break;
            case 0x0023: _ = Net7UiPresentationWire.DecodeSuperLine(payload); break;
            case 0x0024: Net7UiPresentationWire.DecodeClearMessageLine(payload); break;
            case 0x004F: _ = Net7UiPresentationWire.DecodeStarbaseSet(payload); break;
            case 0x0050: _ = Net7UiPresentationWire.DecodeStarbaseTow(payload); break;
            case 0x0052: _ = Net7UiPresentationWire.DecodeLoungeNpc(payload); break;
            case 0x0054: _ = Net7UiPresentationWire.DecodeTalkTree(payload); break;
            case 0x0056: _ = Net7UiPresentationWire.DecodeTalkTreeAction(payload); break;
            case 0x005B: _ = Net7UiPresentationWire.DecodeVerbList(payload); break;
            case 0x005C: _ = Net7UiPresentationWire.DecodeVerbUpdate(payload); break;
            case 0x0060: _ = Net7UiPresentationWire.DecodeMessageTime(payload); break;
            case 0x0062: _ = Net7UiPresentationWire.DecodeDialog(payload); break;
            case 0x0066: _ = Net7UiPresentationWire.DecodeOpenInterface(payload); break;
            case 0x0067: _ = Net7UiPresentationWire.DecodeGadgetControl(payload); break;
            case 0x0069: _ = Net7UiPresentationWire.DecodeClickIndicator(payload); break;
            case 0x00A0: _ = Net7UiPresentationWire.DecodeStarbaseRoomUpdate(payload); break;
            case 0x00BE: _ = Net7UiPresentationWire.DecodeConfirmedActionOffer(payload); break;
            case 0x00BF: _ = Net7UiPresentationWire.DecodeConfirmedActionTimeout(payload); break;
            default: throw new ArgumentOutOfRangeException(nameof(opcode));
        }
    }

    internal sealed class WireWriter
    {
        private readonly List<byte> _bytes = [];
        public void U8(byte value) => _bytes.Add(value);
        public void U16(ushort value) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, value); _bytes.AddRange(b.ToArray()); }
        public void U32(uint value) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, value); _bytes.AddRange(b.ToArray()); }
        public void NetworkU32(uint value) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, value); _bytes.AddRange(b.ToArray()); }
        public void CString(string value) { _bytes.AddRange(Encoding.Latin1.GetBytes(value)); _bytes.Add(0); }
        public void SharedString(string value) { byte[] bytes = Encoding.Latin1.GetBytes(value); U16(checked((ushort)bytes.Length)); Bytes(bytes); }
        public void Bytes(ReadOnlySpan<byte> value) => _bytes.AddRange(value.ToArray());
        public byte[] ToArray() => _bytes.ToArray();
    }
}
