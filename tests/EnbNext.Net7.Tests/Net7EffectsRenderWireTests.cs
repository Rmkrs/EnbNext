using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using EnbNext.Net7.Presentation;

namespace EnbNext.Net7.Tests;

public sealed class Net7EffectsRenderWireTests
{
    [Fact]
    public void EffectFamiliesDecodeCurrentGeometryAndIgnoreUnusedTail()
    {
        byte[] point = new byte[40];
        WriteU32(point, 0, 0x101);
        WriteU32(point, 4, 0x202);
        WriteVector3(point, 8, new Vector3(1, 2, 3));
        WriteI16(point, 20, -1);
        WriteU16(point, 22, 10);
        WriteF32(point, 24, 1.5f);
        WriteVector3(point, 28, new Vector3(.1f, .2f, .3f));
        Net7PointEffectUpdate a = Net7EffectsRenderWire.DecodePointEffect(point);
        Assert.Equal(0x101u, a.EffectId);
        Assert.True(a.Duration < 0);
        Assert.Equal((ushort)10, a.EffectDefinitionId);
        Assert.Equal(new Vector3(1, 2, 3), a.Location);
        Assert.Equal(a, Net7EffectsRenderWire.DecodePointEffect(point.Concat(new byte[] { 0xFF }).ToArray()));

        byte[] c = Build(writer =>
        {
            writer.U32(0x303); writer.U32(0x404); writer.U32(0x505); writer.CString("BONE_A"); writer.U32(0x606);
            writer.I16(-2); writer.U16(11); writer.Vector3(new Vector3(4, 5, 6)); writer.I16(-7); writer.I16(8);
            writer.F32(2.5f); writer.Vector3(new Vector3(.4f, .5f, .6f)); writer.F32(1.25f);
        });
        Net7ObjectToEffectEffectUpdate cDecoded = Net7EffectsRenderWire.DecodeObjectToEffectEffect(c);
        Assert.Equal("BONE_A", cDecoded.SourceBoneChain);
        Assert.Equal(0x606u, cDecoded.TargetGameId);
        Assert.Equal((short)-7, cDecoded.AuxiliaryEffectFieldA);
        Assert.Equal((short)8, cDecoded.AuxiliaryEffectFieldB);
        Assert.Equal(1.25f, cDecoded.Speedup);
        Assert.Throws<InvalidDataException>(() => Net7EffectsRenderWire.DecodeObjectToEffectEffect(c[..^1]));

        byte[] d = Build(writer =>
        {
            writer.U32(0x707); writer.U32(0x808); writer.U32(0x909); writer.CString(string.Empty);
            writer.Vector3(new Vector3(7, 8, 9)); writer.I16(12); writer.U16(12); writer.F32(.75f);
            writer.Vector3(new Vector3(.7f, .8f, .9f)); writer.F32(3.5f);
        });
        Net7ObjectToPointEffectUpdate dDecoded = Net7EffectsRenderWire.DecodeObjectToPointEffect(d);
        Assert.Equal(string.Empty, dDecoded.SourceBoneChain);
        Assert.Equal(new Vector3(7, 8, 9), dDecoded.TargetPoint);
        Assert.Equal((ushort)12, dDecoded.EffectDefinitionId);

        byte[] e = Build(writer =>
        {
            writer.U32(0xA01); writer.U32(0xA02); writer.U32(0xA03); writer.CString("ROOT"); writer.U32(0xA04);
            writer.I16(13); writer.U16(15); writer.Vector3(new Vector3(10, 11, 12));
            writer.I16(-7); writer.I16(8); writer.U8(1); writer.F32(4.5f);
            writer.Vector3(new Vector3(.11f, .22f, .33f)); writer.F32(.5f);
        });
        Net7ObjectToObjectDurationLinkedEffectUpdate eDecoded = Net7EffectsRenderWire.DecodeObjectToObjectDurationLinkedEffect(e);
        Assert.Equal((short)13, eDecoded.DurationLink);
        Assert.Equal((ushort)15, eDecoded.EffectDefinitionId);
        Assert.Equal(new Vector3(10, 11, 12), eDecoded.SpatialOffset);
        Assert.Equal((short)-7, eDecoded.EndpointShortA);
        Assert.Equal((short)8, eDecoded.EndpointShortB);
        Assert.Equal((byte)1, eDecoded.DescriptorFlag);
        Assert.Equal(eDecoded, Net7EffectsRenderWire.DecodeObjectToObjectDurationLinkedEffect(e.Concat(new byte[] { 0xFF }).ToArray()));
        Assert.Equal(cDecoded, Net7EffectsRenderWire.DecodeObjectToEffectEffect(c.Concat(new byte[] { 0xFF }).ToArray()));
        Assert.Equal(dDecoded, Net7EffectsRenderWire.DecodeObjectToPointEffect(d.Concat(new byte[] { 0xFF }).ToArray()));
    }

    [Fact]
    public void DecalAndColorizationDecodeExactCountedRecordGeometry()
    {
        byte[] decal = new byte[6 + 2 * 24];
        WriteU32(decal, 0, 0x11223344);
        WriteU16(decal, 4, 2);
        WriteU32(decal, 6, 1); WriteU32(decal, 10, 2); Enumerable.Range(0, 16).Select(i => (byte)i).ToArray().CopyTo(decal, 14);
        WriteU32(decal, 30, 3); WriteU32(decal, 34, 4); Enumerable.Range(16, 16).Select(i => (byte)i).ToArray().CopyTo(decal, 38);
        Net7DecalUpdate decodedDecal = Net7EffectsRenderWire.DecodeDecal(decal);
        Assert.Equal(0x11223344u, decodedDecal.GameId);
        Assert.Equal(2, decodedDecal.Decals.Count);
        Assert.Equal(4u, decodedDecal.Decals[1].Field1);
        Assert.Equal((byte)31, decodedDecal.Decals[1].Data[15]);
        // 008807B0 reads every record through the same stack locals: a short second
        // record keeps the first record's final data byte, and a count beyond the
        // payload appends the retained record again.
        Net7DecalUpdate shortSecond = Net7EffectsRenderWire.DecodeDecal(decal[..^1]);
        Assert.Equal(2, shortSecond.Decals.Count);
        Assert.Equal(15, shortSecond.Decals[1].Data[15]);
        Assert.Equal(30, shortSecond.Decals[1].Data[14]);
        byte[] overcounted = (byte[])decal.Clone(); WriteU16(overcounted, 4, 4);
        Net7DecalUpdate repeated = Net7EffectsRenderWire.DecodeDecal(overcounted);
        Assert.Equal(4, repeated.Decals.Count);
        Assert.Equal(repeated.Decals[1].Data, repeated.Decals[3].Data);
        Assert.Equal(3u, repeated.Decals[3].Field0);

        byte[] color = new byte[6 + 2 * 16];
        WriteU32(color, 0, 0x55667788);
        WriteU16(color, 4, 2);
        WriteU32(color, 6, 0x10);
        Enumerable.Repeat((byte)0xAA, 12).ToArray().CopyTo(color, 10);
        WriteU32(color, 22, 0x20);
        Enumerable.Repeat((byte)0xBB, 12).ToArray().CopyTo(color, 26);
        Net7ColorizationUpdate decodedColor = Net7EffectsRenderWire.DecodeColorization(color);
        Assert.Equal(2, decodedColor.Entries.Count);
        Assert.Equal(0x10u, decodedColor.Entries[0].Field);
        Assert.Equal(Enumerable.Repeat((byte)0xAA, 12), decodedColor.Entries[0].Vector);
        Assert.Equal(0x20u, decodedColor.Entries[1].Field);
        Assert.Equal(Enumerable.Repeat((byte)0xBB, 12), decodedColor.Entries[1].Vector);
    }

    [Fact]
    public void DecalSignedCountAndUnusedTailFollowRetail()
    {
        byte[] packet = new byte[31];
        WriteU32(packet, 0, 77);
        WriteU16(packet, 4, 1);
        WriteU32(packet, 6, 3);
        WriteU32(packet, 10, 9);
        packet[30] = 0xFF;
        var decoded = Net7EffectsRenderWire.DecodeDecal(packet);
        Assert.Equal(77u, decoded.GameId);
        Assert.Equal(9u, Assert.Single(decoded.Decals).Field1);
        foreach (ushort count in new ushort[] { 0, 0x8000, 0xFFFF })
        {
            WriteU16(packet, 4, count);
            Assert.Empty(Net7EffectsRenderWire.DecodeDecal(packet).Decals);
        }
        Assert.Empty(Net7EffectsRenderWire.DecodeDecal(packet.AsSpan(0, 4)).Decals);
        packet[4] = 0;
        Assert.Empty(Net7EffectsRenderWire.DecodeDecal(packet.AsSpan(0, 5)).Decals);
        packet[4] = 1;
        Assert.Throws<InvalidDataException>(() => Net7EffectsRenderWire.DecodeDecal(packet.AsSpan(0, 5)));
    }

    [Fact]
    public void CurrentRetailStartupColorizationFrameDecodesEightSixteenByteEntries()
    {
        // Captured from the current retail client startup stream. The complete
        // retail frame is 138 bytes: 4-byte header + 134-byte payload.
        byte[] frame = Convert.FromHexString(
            "8A0011000A4200400800FFFFFFFFD7A3F03ECDCCCC3CCDCCCC3C00000000000000000000000000000000" +
            "FFFFFFFFD7A3F03ECDCCCC3CCDCCCC3C00000000000000000000000000000000" +
            "FFFFFFFFD7A3F03ECDCCCC3CCDCCCC3C00000000000000000000000000000000" +
            "FFFFFFFFD7A3F03ECDCCCC3CCDCCCC3C00000000000000000000000000000000");
        Assert.Equal(138, frame.Length);

        Net7ColorizationUpdate decoded = Net7EffectsRenderWire.DecodeColorization(frame.AsSpan(4));
        Assert.Equal(0x4000420Au, decoded.GameId);
        Assert.Equal(8, decoded.Entries.Count);
        Assert.Equal(0xFFFFFFFFu, decoded.Entries[0].Field);
        Assert.Equal(Convert.FromHexString("D7A3F03ECDCCCC3CCDCCCC3C"), decoded.Entries[0].Vector);
        Assert.Equal(0u, decoded.Entries[1].Field);
        Assert.Equal(new byte[12], decoded.Entries[1].Vector);
    }

    [Fact]
    public void ColorizationCountIsNativeThirtyTwoByteRecordsAndKeepsCompleteEntries()
    {
        // 0087F510 reads count records of (u32, 12 bytes, u32, 12 bytes). A native
        // count of four with 128 bytes is the same eight paint entries as the
        // current server's count of eight; entries past the payload are not read.
        byte[] native = new byte[6 + 4 * 32];
        WriteU32(native, 0, 9);
        WriteU16(native, 4, 4);
        for (int i = 0; i < 8; i++) WriteU32(native, 6 + i * 16, (uint)i);
        Net7ColorizationUpdate decoded = Net7EffectsRenderWire.DecodeColorization(native);
        Assert.Equal(Enumerable.Range(0, 8).Select(i => (uint)i), decoded.Entries.Select(e => e.Field));
        WriteU16(native, 4, 1);
        Assert.Equal(2, Net7EffectsRenderWire.DecodeColorization(native).Entries.Count);
        WriteU16(native, 4, 8);
        Assert.Equal(8, Net7EffectsRenderWire.DecodeColorization(native).Entries.Count);
        Assert.Equal(7, Net7EffectsRenderWire.DecodeColorization(native[..^16]).Entries.Count);
    }

    [Fact]
    public void RenderBoundsSplineAndAttachmentHonorExactRawAndNetworkOrderContracts()
    {
        byte[] z = Convert.FromHexString("0102030405060708");
        Net7ZBandUpdate zDecoded = Net7EffectsRenderWire.DecodeZBand(z);
        Assert.Equal(Convert.FromHexString("01020304"), zDecoded.FieldA);
        Assert.Equal(Convert.FromHexString("05060708"), zDecoded.FieldB);

        byte[] bbox = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        Net7BoundingBoxUpdate bboxDecoded = Net7EffectsRenderWire.DecodeBoundingBox(bbox);
        Assert.Equal(Enumerable.Range(0, 8).Select(i => (byte)i), bboxDecoded.BlockA);
        Assert.Equal(Enumerable.Range(8, 8).Select(i => (byte)i), bboxDecoded.BlockB);

        byte[] init = new byte[12];
        WriteU32(init, 0, 1); WriteU32(init, 4, 2); WriteU32(init, 8, 3);
        Assert.Equal(new Net7InitializeRenderStateUpdate(1, 2, 3), Net7EffectsRenderWire.DecodeInitializeRenderState(init));

        byte[] action = new byte[8]; WriteU32(action, 0, 4); WriteU32(action, 4, 5);
        Assert.Equal(new Net7RenderStateActionUpdate(4, 5), Net7EffectsRenderWire.DecodeRenderStateAction(action, 0x0031));
        Assert.Throws<ArgumentOutOfRangeException>(() => Net7EffectsRenderWire.DecodeRenderStateAction(action, 0x0033));

        byte[] spline = new byte[9]; WriteU32(spline, 0, 5); Convert.FromHexString("1020304050").CopyTo(spline, 4);
        Assert.Equal(Convert.FromHexString("1020304050"), Net7EffectsRenderWire.DecodeSpline(spline).Data);
        WriteU32(spline, 0, 4);
        Assert.Equal(Convert.FromHexString("10203040"), Net7EffectsRenderWire.DecodeSpline(spline).Data);

        byte[] attach = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(attach.AsSpan(0, 4), 0x01020304);
        BinaryPrimitives.WriteUInt32BigEndian(attach.AsSpan(4, 4), 0x11121314);
        BinaryPrimitives.WriteUInt32BigEndian(attach.AsSpan(8, 4), 0x21222324);
        Assert.Equal(new Net7CreateAttachmentUpdate(0x01020304, 0x11121314, 0x21222324), Net7EffectsRenderWire.DecodeCreateAttachment(attach));
    }

    [Fact]
    public void FixedRenderUpdatesIgnoreUnusedTail()
    {
        byte[] payload = Enumerable.Range(1, 20).Select(i => (byte)i).ToArray();
        Assert.Equal(payload[..4], Net7EffectsRenderWire.DecodeZBand(payload).FieldA);
        Assert.Equal(payload[4..8], Net7EffectsRenderWire.DecodeZBand(payload).FieldB);
        Assert.Equal(payload[..8], Net7EffectsRenderWire.DecodeBoundingBox(payload).BlockA);
        Assert.Equal(payload[8..16], Net7EffectsRenderWire.DecodeBoundingBox(payload).BlockB);
        Assert.Equal(Net7EffectsRenderWire.DecodeInitializeRenderState(payload[..12]),
            Net7EffectsRenderWire.DecodeInitializeRenderState(payload));
        foreach (ushort opcode in new ushort[] { 0x0030, 0x0031, 0x0032 })
            Assert.Equal(Net7EffectsRenderWire.DecodeRenderStateAction(payload[..8], opcode),
                Net7EffectsRenderWire.DecodeRenderStateAction(payload, opcode));
        Assert.Equal(Net7EffectsRenderWire.DecodeCreateAttachment(payload[..12]),
            Net7EffectsRenderWire.DecodeCreateAttachment(payload));
    }

    [Fact]
    public void SoundNameDecalAndSubpartsPreserveCurrentStringAndEndianRules()
    {
        byte[] soundName = Encoding.Latin1.GetBytes("warp.wav\0PAD");
        byte[] sound = new byte[4 + soundName.Length + 5];
        WriteU32(sound, 0, (uint)soundName.Length);
        soundName.CopyTo(sound, 4);
        WriteU32(sound, 4 + soundName.Length, 7);
        sound[^1] = 1;
        Net7ClientSoundUpdate soundDecoded = Net7EffectsRenderWire.DecodeClientSound(sound);
        Assert.Equal("warp.wav", soundDecoded.SoundName);
        Assert.Equal(soundName, soundDecoded.SoundNameBytes);
        Assert.Equal(7u, soundDecoded.Channel);
        Assert.Equal((byte)1, soundDecoded.Queue);

        byte[] noNul = (byte[])sound.Clone();
        Array.Fill(noNul, (byte)'X', 4, soundName.Length);
        var counted = Net7EffectsRenderWire.DecodeClientSound(noNul);
        Assert.Equal(new string('X', soundName.Length), counted.SoundName);
        Assert.Equal(7u, counted.Channel);
        Assert.Equal((byte)1, counted.Queue);
        byte[] empty = new byte[9];
        WriteU32(empty, 4, 3);
        Assert.Equal("", Net7EffectsRenderWire.DecodeClientSound(empty).SoundName);
        Assert.Equal(3u, Net7EffectsRenderWire.DecodeClientSound(empty).Channel);

        byte[] command = new byte[8]; WriteU32(command, 0, 2); WriteU32(command, 4, 15);
        Assert.True(Net7EffectsRenderWire.DecodeClientSoundCommand(command).AppliesToCurrentSoundManager);
        WriteU32(command, 4, 16);
        Assert.True(Net7EffectsRenderWire.DecodeClientSoundCommand(command).AppliesToCurrentSoundManager);
        WriteU32(command, 0, 1);
        Assert.False(Net7EffectsRenderWire.DecodeClientSoundCommand(command).AppliesToCurrentSoundManager);
        WriteU32(command, 0, 3);
        Assert.False(Net7EffectsRenderWire.DecodeClientSoundCommand(command).AppliesToCurrentSoundManager);
        Assert.Equal(new Net7ClientSoundCommandUpdate(3, 16), Net7EffectsRenderWire.DecodeClientSoundCommand(command.Concat(new byte[] { 0xFF }).ToArray()));

        byte[] nameDecal = new byte[48];
        WriteU32(nameDecal, 0, 0xCAFEBABE);
        Encoding.Latin1.GetBytes("ENBNEXT\0").CopyTo(nameDecal, 4);
        WriteVector3(nameDecal, 36, new Vector3(.2f, .4f, .6f));
        Net7NameDecalUpdate decal = Net7EffectsRenderWire.DecodeNameDecal(nameDecal);
        Assert.Equal("ENBNEXT", decal.Name);
        Assert.Equal(new Vector3(.2f, .4f, .6f), decal.Rgb);

        byte[] subparts = Build(writer =>
        {
            writer.NetworkU32(0x01020304); writer.NetworkU32(2);
            writer.CString("BONE_A"); writer.NetworkU32(0x11121314);
            writer.CString("BONE_B"); writer.NetworkU32(0x21222324);
        });
        Net7SubPartsUpdate parts = Net7EffectsRenderWire.DecodeSubParts(subparts);
        Assert.Equal(0x01020304u, parts.GameId);
        Assert.Equal(2, parts.Parts.Count);
        Assert.Equal("BONE_B", parts.Parts[1].Name);
        Assert.Equal(0x21222324u, parts.Parts[1].PartValue);
        Assert.Equal(parts.Parts, Net7EffectsRenderWire.DecodeSubParts(subparts.Concat(new byte[] { 0 }).ToArray()).Parts);
    }

    [Fact]
    public void SubpartsKeepInitializedPrefixesAndSignedCount()
    {
        var empty = Net7EffectsRenderWire.DecodeSubParts([]);
        Assert.Equal(uint.MaxValue, empty.GameId);
        Assert.Empty(empty.Parts);
        Assert.Equal(0x12FFFFFFu, Net7EffectsRenderWire.DecodeSubParts(new byte[] { 0x12 }).GameId);
        foreach (uint count in new uint[] { 0, 0x80000000, uint.MaxValue })
            Assert.Empty(Net7EffectsRenderWire.DecodeSubParts(Build(w => { w.NetworkU32(7); w.NetworkU32(count); })).Parts);
        var payload = Build(w => { w.NetworkU32(7); w.NetworkU32(2); w.CString("A"); w.NetworkU32(3); });
        var parts = Net7EffectsRenderWire.DecodeSubParts(payload).Parts;
        Assert.Equal(new Net7SubPartValue("A", 3), parts[0]);
        Assert.Equal(new Net7SubPartValue("", 0), parts[1]);
        var prefix = Build(w => { w.NetworkU32(7); w.NetworkU32(1); w.U8((byte)'B'); });
        Assert.Equal(new Net7SubPartValue("B", 0), Assert.Single(Net7EffectsRenderWire.DecodeSubParts(prefix).Parts));
        var partialValue = Build(w => { w.NetworkU32(7); w.NetworkU32(1); w.CString("C"); w.U8(0x12); w.U8(0x34); });
        Assert.Equal(0x12340000u, Assert.Single(Net7EffectsRenderWire.DecodeSubParts(partialValue).Parts).PartValue);
    }

    [Fact]
    public void CountedSplineSoundAndNameDecalIgnoreUnusedTail()
    {
        byte[] spline = { 2, 0, 0, 0, 0, 0, 0xFF };
        Assert.Equal(new byte[] { 0, 0 }, Net7EffectsRenderWire.DecodeSpline(spline).Data);
        byte[] sound = { 2, 0, 0, 0, (byte)'A', 0, 3, 0, 0, 0, 1, 0xFF };
        var decoded = Net7EffectsRenderWire.DecodeClientSound(sound);
        Assert.Equal("A", decoded.SoundName);
        Assert.Equal(3u, decoded.Channel);
        Assert.Equal((byte)1, decoded.Queue);
        byte[] decal = new byte[49];
        WriteU32(decal, 0, 77);
        decal[4] = (byte)'N';
        WriteVector3(decal, 36, Vector3.One);
        decal[48] = 0xFF;
        var name = Net7EffectsRenderWire.DecodeNameDecal(decal);
        Assert.Equal(77u, name.GameId);
        Assert.Equal("N", name.Name);
        Assert.Equal(Vector3.One, name.Rgb);
    }

    [Fact]
    public void AllI03FamiliesRejectTruncatedPayloads()
    {
        byte[] point = new byte[40];
        byte[] objectToEffect = Build(w =>
        {
            w.U32(1); w.U32(2); w.U32(3); w.CString("B"); w.U32(4); w.I16(5); w.U16(6);
            w.Vector3(Vector3.Zero); w.I16(7); w.I16(8); w.F32(1); w.Vector3(Vector3.Zero); w.F32(1);
        });
        byte[] objectToPoint = Build(w =>
        {
            w.U32(1); w.U32(2); w.U32(3); w.CString("B"); w.Vector3(Vector3.Zero); w.I16(5); w.U16(6);
            w.F32(1); w.Vector3(Vector3.Zero); w.F32(1);
        });
        byte[] durationLinked = Build(w =>
        {
            w.U32(1); w.U32(2); w.U32(3); w.CString("B"); w.U32(4); w.I16(5); w.U16(6);
            w.Vector3(Vector3.Zero); w.I16(7); w.I16(8); w.U8(0); w.F32(1); w.Vector3(Vector3.Zero); w.F32(1);
        });
        byte[] decal = new byte[6 + 24]; WriteU16(decal, 4, 1);
        byte[] color = new byte[6 + 16]; WriteU16(color, 4, 1);
        byte[] zBand = new byte[8];
        byte[] bbox = new byte[16];
        byte[] renderInit = new byte[12];
        byte[] renderAction = new byte[8];
        byte[] spline = new byte[5]; WriteU32(spline, 0, 1);
        byte[] attachment = new byte[12];
        byte[] sound = new byte[10]; WriteU32(sound, 0, 1); sound[4] = 0;
        byte[] soundCommand = new byte[8];
        byte[] nameDecal = new byte[48];
        byte[] subParts = Build(w => { w.NetworkU32(1); w.NetworkU32(1); w.CString("P"); w.NetworkU32(2); });

        Assert.Throws<InvalidDataException>(() => Net7EffectsRenderWire.DecodePointEffect(point[..^1]));
        Assert.Throws<InvalidDataException>(() => Net7EffectsRenderWire.DecodeObjectToEffectEffect(objectToEffect[..^1]));
        Assert.Throws<InvalidDataException>(() => Net7EffectsRenderWire.DecodeObjectToPointEffect(objectToPoint[..^1]));
        Assert.Throws<InvalidDataException>(() => Net7EffectsRenderWire.DecodeObjectToObjectDurationLinkedEffect(durationLinked[..^1]));
        Assert.Throws<InvalidDataException>(() => Net7EffectsRenderWire.DecodeDecal(decal[..^1]));
        Assert.Empty(Net7EffectsRenderWire.DecodeColorization(color[..^1]).Entries);
        Assert.Throws<InvalidDataException>(() => Net7EffectsRenderWire.DecodeZBand(zBand[..^1]));
        Assert.Throws<InvalidDataException>(() => Net7EffectsRenderWire.DecodeBoundingBox(bbox[..^1]));
        Assert.Throws<InvalidDataException>(() => Net7EffectsRenderWire.DecodeInitializeRenderState(renderInit[..^1]));
        Assert.Throws<InvalidDataException>(() => Net7EffectsRenderWire.DecodeRenderStateAction(renderAction[..^1], 0x0030));
        Assert.Throws<InvalidDataException>(() => Net7EffectsRenderWire.DecodeRenderStateAction(renderAction[..^1], 0x0031));
        Assert.Throws<InvalidDataException>(() => Net7EffectsRenderWire.DecodeRenderStateAction(renderAction[..^1], 0x0032));
        Assert.Throws<InvalidDataException>(() => Net7EffectsRenderWire.DecodeSpline(spline[..^1]));
        Assert.Throws<InvalidDataException>(() => Net7EffectsRenderWire.DecodeCreateAttachment(attachment[..^1]));
        Assert.Throws<InvalidDataException>(() => Net7EffectsRenderWire.DecodeClientSound(sound[..^1]));
        Assert.Throws<InvalidDataException>(() => Net7EffectsRenderWire.DecodeClientSoundCommand(soundCommand[..^1]));
        Assert.Throws<InvalidDataException>(() => Net7EffectsRenderWire.DecodeNameDecal(nameDecal[..^1]));
        Assert.Equal(new Net7SubPartValue("P", 0), Assert.Single(Net7EffectsRenderWire.DecodeSubParts(subParts[..^1]).Parts));
    }

    [Theory]
    [InlineData(new byte[] { 7, 0, 0, 0 })]
    [InlineData(new byte[] { 7, 0, 0, 0, 0, 0 })]
    [InlineData(new byte[] { 7, 0, 0, 0, 0xFF, 0xFF, 1, 2, 3 })]
    [InlineData(new byte[] { 7, 0, 0, 0, 0x00, 0x80 })]
    public void ColorizationNonpositiveSignedCountIsAnEmptyUpdate(byte[] payload)
    {
        // 0087F510 zero-initializes its signed count and loops only while positive;
        // the bytes after a nonpositive count are never read.
        Net7ColorizationUpdate decoded = Net7EffectsRenderWire.DecodeColorization(payload);
        Assert.Equal(7u, decoded.GameId);
        Assert.Empty(decoded.Entries);
    }

    internal static byte[] Build(Action<WireBuilder> action)
    {
        var builder = new WireBuilder();
        action(builder);
        return builder.ToArray();
    }

    internal sealed class WireBuilder
    {
        private readonly List<byte> _bytes = [];
        public void U8(byte value) => _bytes.Add(value);
        public void U16(ushort value) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, value); _bytes.AddRange(b.ToArray()); }
        public void I16(short value) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteInt16LittleEndian(b, value); _bytes.AddRange(b.ToArray()); }
        public void U32(uint value) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, value); _bytes.AddRange(b.ToArray()); }
        public void NetworkU32(uint value) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, value); _bytes.AddRange(b.ToArray()); }
        public void F32(float value) => U32(unchecked((uint)BitConverter.SingleToInt32Bits(value)));
        public void Vector3(Vector3 value) { F32(value.X); F32(value.Y); F32(value.Z); }
        public void CString(string value) { _bytes.AddRange(Encoding.Latin1.GetBytes(value)); _bytes.Add(0); }
        public byte[] ToArray() => _bytes.ToArray();
    }

    internal static void WriteU16(byte[] data, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset, 2), value);
    internal static void WriteI16(byte[] data, int offset, short value) => BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(offset, 2), value);
    internal static void WriteU32(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), value);
    internal static void WriteF32(byte[] data, int offset, float value) => BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset, 4), BitConverter.SingleToInt32Bits(value));
    internal static void WriteVector3(byte[] data, int offset, Vector3 value) { WriteF32(data, offset, value.X); WriteF32(data, offset + 4, value.Y); WriteF32(data, offset + 8, value.Z); }
}
