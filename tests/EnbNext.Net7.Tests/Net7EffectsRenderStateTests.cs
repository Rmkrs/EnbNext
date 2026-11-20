using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.Presentation;
using EnbNext.Net7.Protocol;
using EnbNext.Net7.World;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class Net7EffectsRenderStateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OptionalObjectEffectVectorReachesFactoryAsHsv(bool binary)
    {
        var state=new Net7GameplayState();
        byte[] payload=new byte[binary?29:23];
        if(binary)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(payload,0x701);
            Net7EffectsRenderWireTests.WriteU32(payload,2,1);
            Net7EffectsRenderWireTests.WriteU32(payload,6,2);
            Net7EffectsRenderWireTests.WriteU16(payload,10,102);
        }
        else
        {
            payload[0]=0x71;
            Net7EffectsRenderWireTests.WriteU32(payload,1,1);
            Net7EffectsRenderWireTests.WriteU16(payload,5,102);
        }
        int offset=binary?13:7;
        Net7EffectsRenderWireTests.WriteU32(payload,offset,123);
        Net7EffectsRenderWireTests.WriteVector3(payload,offset+4,new(30,.1f,.2f));
        Assert.True(state.Apply(Packet.ForOpcode(binary?(ushort)11:(ushort)9,payload)));
        var scene=new EnbNext.Presentation.EnbPresentationSnapshotBuilder().Build(new Net7WorldState(),state.Effects,null,0);
        var catalog=new EnbNext.Assets.EnbRetailPresentationCatalog();
        catalog.LoadEffectsText("[EFFECT-102]\nEffectClass="+(binary?"RObjLookAtEffect":"RObjEffect")+"\nBaseAssetID=1058\nRObjHSV=10,0,0\nDuration=1\nSourceBased=TRUE");
        var node=Assert.Single(new EnbNext.Rendering.EnbCurrentRetailEffectRslSchedulerRuntime().Update(scene.Effects,catalog,0).VisualNodes);
        Assert.True(node.TryGetRObjDefinition(out var definition,out var error),error);
        Assert.Equal(new Vector3(40,.1f,.2f),definition.HsvShift);
        Assert.Equal(Vector3.Zero,node.Root.SpatialOffset);
    }
    [Fact]
    public void AllSixOrdinaryEffectFamiliesShareTheI05DefinitionResolver()
    {
        Net7PresentationCatalogs catalogs = BuildCatalogs();
        var state = new Net7GameplayState(catalogs);

        byte[] objectEffect = new byte[11];
        objectEffect[0] = 0x01;
        Net7EffectsRenderWireTests.WriteU32(objectEffect, 1, 1);
        Net7EffectsRenderWireTests.WriteU16(objectEffect, 5, 10);
        Net7EffectsRenderWireTests.WriteU32(objectEffect, 7, 1001);
        Assert.True(state.Apply(Packet.ForOpcode(0x0009, objectEffect)));

        byte[] point = new byte[40];
        Net7EffectsRenderWireTests.WriteU32(point, 0, 1002);
        Net7EffectsRenderWireTests.WriteU32(point, 4, 20);
        Net7EffectsRenderWireTests.WriteVector3(point, 8, new Vector3(1, 2, 3));
        Net7EffectsRenderWireTests.WriteI16(point, 20, -1);
        Net7EffectsRenderWireTests.WriteU16(point, 22, 11);
        Net7EffectsRenderWireTests.WriteF32(point, 24, 2);
        Net7EffectsRenderWireTests.WriteVector3(point, 28, new Vector3(.1f, .2f, .3f));
        Assert.True(state.Apply(Packet.ForOpcode(0x000A, point)));

        byte[] objectToObject = new byte[17];
        BinaryPrimitives.WriteUInt16LittleEndian(objectToObject.AsSpan(0, 2), 0x0001);
        Net7EffectsRenderWireTests.WriteU32(objectToObject, 2, 2);
        Net7EffectsRenderWireTests.WriteU32(objectToObject, 6, 3);
        Net7EffectsRenderWireTests.WriteU16(objectToObject, 10, 12);
        objectToObject[12] = 0;
        Net7EffectsRenderWireTests.WriteU32(objectToObject, 13, 1003);
        Assert.True(state.Apply(Packet.ForOpcode(0x000B, objectToObject)));

        byte[] c = Net7EffectsRenderWireTests.Build(w =>
        {
            w.U32(1004); w.U32(30); w.U32(4); w.CString("BONE"); w.U32(5); w.I16(40); w.U16(13);
            w.Vector3(new Vector3(4, 5, 6)); w.I16(-7); w.I16(8); w.F32(1.5f); w.Vector3(Vector3.One); w.F32(1.25f);
        });
        Assert.True(state.Apply(Packet.ForOpcode(0x000C, c)));

        byte[] d = Net7EffectsRenderWireTests.Build(w =>
        {
            w.U32(1005); w.U32(40); w.U32(6); w.CString(string.Empty); w.Vector3(new Vector3(7, 8, 9));
            w.I16(50); w.U16(14); w.F32(.75f); w.Vector3(new Vector3(.4f, .5f, .6f)); w.F32(2.5f);
        });
        Assert.True(state.Apply(Packet.ForOpcode(0x000D, d)));

        byte[] e = Net7EffectsRenderWireTests.Build(w =>
        {
            w.U32(1006); w.U32(50); w.U32(7); w.CString("ROOT"); w.U32(8); w.I16(60); w.U16(15);
            w.Vector3(new Vector3(10, 11, 12)); w.I16(-7); w.I16(8); w.U8(1);
            w.F32(2); w.Vector3(new Vector3(.7f, .8f, .9f)); w.F32(.5f);
        });
        Assert.True(state.Apply(Packet.ForOpcode(0x000E, e)));

        Assert.Equal(6, state.Effects.Count);
        ushort[] definitions = state.Effects.OrderBy(pair => pair.Key).Select(pair => pair.Value.DefinitionId).ToArray();
        Assert.Equal(new ushort[] { 10, 11, 12, 13, 14, 15 }, definitions);
        foreach (Net7EffectState effect in state.Effects.Values)
        {
            Assert.NotNull(effect.Presentation);
            Assert.False(effect.Presentation!.MissingDefinition);
            Assert.Equal(effect.DefinitionId, effect.Presentation.DefinitionId);
        }

        Assert.Equal(Net7EffectGeometryKind.Object, state.Effects[1001].GeometryKind);
        Assert.Equal(Net7EffectGeometryKind.ObjectToObject, state.Effects[1003].GeometryKind);

        Net7EffectState pointState = state.Effects[1002];
        Assert.Equal(Net7EffectGeometryKind.Point, pointState.GeometryKind);
        Assert.Equal(new Vector3(1, 2, 3), pointState.WorldPoint);
        Assert.True(pointState.HasInfiniteDuration);
        Assert.Equal(new Vector3(.1f, .2f, .3f), pointState.HsvShift);

        Net7EffectState cState = state.Effects[1004];
        Assert.Equal((short)-7, cState.AuxiliaryEffectFieldA);
        Assert.Equal((short)8, cState.AuxiliaryEffectFieldB);
        Assert.Equal(new Vector3(4, 5, 6), cState.SpatialOffset);
        Assert.Equal(Net7EffectGeometryKind.ObjectToEffect, cState.GeometryKind);

        Net7EffectState linked = state.Effects[1006];
        Assert.Equal(Net7EffectGeometryKind.ObjectToObjectDurationLinked, linked.GeometryKind);
        Assert.Equal(0, linked.DurationMilliseconds);
        Assert.Equal((short)60, linked.DurationLink);
        Assert.Equal(new Vector3(10, 11, 12), linked.SpatialOffset);
        Assert.Equal(unchecked((ushort)-7), linked.EndpointShortA);
        Assert.Equal((ushort)8, linked.EndpointShortB);
        Assert.True(linked.DescriptorFlag);
    }

    [Fact]
    public void RenderAttachmentAndObjectPresentationFamiliesMaterializeOnResolvedObjects()
    {
        var world = new Net7WorldState(presentation: BuildCatalogs());
        Net7WorldObject parent = world.MaterializeCreate(new Net7CreateUpdate(1, 1, 10, 0x25, 0, 0, 0));
        _ = world.MaterializeCreate(new Net7CreateUpdate(2, 1, 10, 0x25, 0, 0, 0));

        byte[] decal = new byte[30];
        Net7EffectsRenderWireTests.WriteU32(decal, 0, 1);
        Net7EffectsRenderWireTests.WriteU16(decal, 4, 1);
        Net7EffectsRenderWireTests.WriteU32(decal, 6, 101);
        Net7EffectsRenderWireTests.WriteU32(decal, 10, 102);
        Enumerable.Repeat((byte)0xA5, 16).ToArray().CopyTo(decal, 14);
        world.Apply(Packet.ForOpcode(0x0010, decal), 0);
        Assert.Single(parent.Decals);
        Assert.Equal(102u, parent.Decals[0].Field1);

        byte[] color = new byte[22];
        Net7EffectsRenderWireTests.WriteU32(color, 0, 1);
        Net7EffectsRenderWireTests.WriteU16(color, 4, 1);
        Net7EffectsRenderWireTests.WriteU32(color, 6, 201);
        Enumerable.Repeat((byte)1, 12).ToArray().CopyTo(color, 10);
        world.Apply(Packet.ForOpcode(0x0011, color), 0);
        Assert.Single(parent.ColorizationEntries);
        Assert.Equal(201u, parent.ColorizationEntries[0].Field);

        world.Apply(Packet.ForOpcode(0x002A, Convert.FromHexString("0102030405060708")), 0);
        world.Apply(Packet.ForOpcode(0x002B, Enumerable.Range(0, 16).Select(i => (byte)i).ToArray()), 0);
        Assert.Equal(Convert.FromHexString("01020304"), world.ZBand!.FieldA);
        Assert.Equal((byte)15, world.BoundingBox!.BlockB[7]);

        byte[] init = new byte[12];
        Net7EffectsRenderWireTests.WriteU32(init, 0, 1);
        Net7EffectsRenderWireTests.WriteU32(init, 4, 300);
        Net7EffectsRenderWireTests.WriteU32(init, 8, 301);
        world.Apply(Packet.ForOpcode(0x002F, init), 0);
        Assert.Equal(301u, parent.RenderStateLinks[300]);

        byte[] action = new byte[8];
        Net7EffectsRenderWireTests.WriteU32(action, 0, 1);
        Net7EffectsRenderWireTests.WriteU32(action, 4, 300);
        world.Apply(Packet.ForOpcode(0x0030, action), 0);
        world.Apply(Packet.ForOpcode(0x0031, action), 0);
        Assert.Contains(300u, parent.ActiveRenderStates);
        Assert.Equal(new ushort[] { 0x002f, 0x0030, 0x0031 }, parent.RenderStateActions.Select(a => a.Opcode));
        Assert.Equal(301u,parent.RenderStateActions[0].InitializerArgument);
        world.Apply(Packet.ForOpcode(0x0032, action), 0);
        Assert.DoesNotContain(300u, parent.ActiveRenderStates);
        Assert.Equal((ushort)0x0032, parent.RenderStateActions[^1].Opcode);

        byte[] spline = new byte[7];
        Net7EffectsRenderWireTests.WriteU32(spline, 0, 3);
        Convert.FromHexString("AABBCC").CopyTo(spline, 4);
        world.Apply(Packet.ForOpcode(0x0038, spline), 0);
        Assert.Equal(Convert.FromHexString("AABBCC"), world.Spline!.Data);

        byte[] attachment = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(attachment.AsSpan(0, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(attachment.AsSpan(4, 4), 2);
        BinaryPrimitives.WriteUInt32BigEndian(attachment.AsSpan(8, 4), 0xABCDEF01);
        world.Apply(Packet.ForOpcode(0x004A, attachment), 0);
        Assert.Equal(0xABCDEF01u, parent.Attachments.Single(a => a.ChildId == 2).AttachmentSelector);

        byte[] name = new byte[48];
        Net7EffectsRenderWireTests.WriteU32(name, 0, 1);
        Encoding.Latin1.GetBytes("VANTA\0").CopyTo(name, 4);
        Net7EffectsRenderWireTests.WriteVector3(name, 36, new Vector3(.2f, .3f, .4f));
        world.Apply(Packet.ForOpcode(0x00B2, name), 0);
        Assert.Equal("VANTA", parent.NameDecal!.Name);
        Assert.Equal(new Vector3(.2f, .3f, .4f), parent.NameDecal.Rgb);

        byte[] subparts = Net7EffectsRenderWireTests.Build(w =>
        {
            w.NetworkU32(1); w.NetworkU32(2);
            w.CString("LEFT_WING"); w.NetworkU32(401);
            w.CString("RIGHT_WING"); w.NetworkU32(402);
        });
        world.Apply(Packet.ForOpcode(0x00B4, subparts), 0);
        Assert.Equal(2, parent.SubParts.Count);
        Assert.Equal(402u, parent.SubParts[1].PartValue);
    }

    [Fact]
    public void MissingObjectPresentationPacketsAreConsumedWithoutInventingObjects()
    {
        var world = new Net7WorldState();

        byte[] init = new byte[12];
        Net7EffectsRenderWireTests.WriteU32(init, 0, 999);
        Net7EffectsRenderWireTests.WriteU32(init, 4, 1);
        Net7EffectsRenderWireTests.WriteU32(init, 8, 2);
        Assert.True(world.Apply(Packet.ForOpcode(0x002F, init), 0).PacketMaterialized);

        byte[] attach = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(attach.AsSpan(0, 4), 999);
        BinaryPrimitives.WriteUInt32BigEndian(attach.AsSpan(4, 4), 1000);
        BinaryPrimitives.WriteUInt32BigEndian(attach.AsSpan(8, 4), 7);
        Assert.True(world.Apply(Packet.ForOpcode(0x004A, attach), 0).PacketMaterialized);
        Assert.Empty(world.Objects.Snapshot());
    }

    [Fact]
    public void ClientSoundAndSoundCommandKeepExactQueueAndCommandSemantics()
    {
        var gameplay = new Net7GameplayState();
        byte[] name = Encoding.Latin1.GetBytes("engine.wav\0");
        byte[] sound = new byte[4 + name.Length + 5];
        Net7EffectsRenderWireTests.WriteU32(sound, 0, (uint)name.Length);
        name.CopyTo(sound, 4);
        Net7EffectsRenderWireTests.WriteU32(sound, 4 + name.Length, 12);
        sound[^1] = 1;
        Assert.True(gameplay.Apply(Packet.ForOpcode(0x006A, sound)));
        Assert.Equal("engine.wav", gameplay.ClientSounds.Single().SoundName);
        Assert.Equal(12u, gameplay.ClientSounds.Single().Channel);
        Assert.Equal((byte)1, gameplay.ClientSounds.Single().Queue);

        byte[] command = new byte[8];
        Net7EffectsRenderWireTests.WriteU32(command, 0, 2);
        Net7EffectsRenderWireTests.WriteU32(command, 4, 15);
        gameplay.Apply(Packet.ForOpcode(0x006B, command));
        Assert.True(gameplay.SoundCommands[^1].AppliesToCurrentSoundManager);
        Net7EffectsRenderWireTests.WriteU32(command, 4, 16);
        gameplay.Apply(Packet.ForOpcode(0x006B, command));
        // 007384A0: command 2 stops all channels and ignores this field.
        Assert.True(gameplay.SoundCommands[^1].AppliesToCurrentSoundManager);
        Net7EffectsRenderWireTests.WriteU32(command, 0, 1);
        gameplay.Apply(Packet.ForOpcode(0x006B, command));
        Assert.False(gameplay.SoundCommands[^1].AppliesToCurrentSoundManager);
        var events = gameplay.ClientSoundEvents;
        int commandCount = gameplay.SoundCommands.Count;
        Net7EffectsRenderWireTests.WriteU32(command, 0, uint.MaxValue);
        Net7EffectsRenderWireTests.WriteU32(command, 4, 0);
        Assert.True(gameplay.Apply(Packet.ForOpcode(0x006B, command)));
        Assert.Same(events, gameplay.ClientSoundEvents);
        Assert.Equal(commandCount, gameplay.SoundCommands.Count);
        // The unknown command must not block subsequent packets.
        Assert.True(gameplay.Apply(Packet.ForOpcode(0x006A, sound)));
        Assert.Equal(events.Count + 1, gameplay.ClientSoundEvents.Count);
    }

    [Fact]
    public void I03TransientStateResetsAcrossHandoffAndCharacterSelection()
    {
        var catalogs = BuildCatalogs();
        var gameplay = new Net7GameplayState(catalogs);
        var world = new Net7WorldState(presentation: catalogs);
        world.MaterializeCreate(new Net7CreateUpdate(1, 1, 10, 0x25, 0, 0, 0));

        byte[] point = new byte[40];
        Net7EffectsRenderWireTests.WriteU32(point, 0, 1);
        Net7EffectsRenderWireTests.WriteU16(point, 22, 10);
        Net7EffectsRenderWireTests.WriteF32(point, 24, 1);
        gameplay.Apply(Packet.ForOpcode(0x000A, point));
        world.Apply(Packet.ForOpcode(0x002A, new byte[8]), 0);
        byte[] spline = new byte[4];
        world.Apply(Packet.ForOpcode(0x0038, spline), 0);

        Assert.NotEmpty(gameplay.Effects);
        Assert.NotNull(world.ZBand);
        Assert.NotNull(world.Spline);

        gameplay.ResetForServerHandoff();
        byte[] handoff = new byte[Net7SectorJoinClientData.WireSize + 8]; // Four empty SharedStrings follow the 0x40 join image.
        world.Apply(Packet.ForOpcode(0x003A, handoff), 0);
        Assert.Empty(gameplay.Effects);
        Assert.Null(world.ZBand);
        Assert.Null(world.BoundingBox);
        Assert.Null(world.Spline);

        gameplay.ResetForCharacterSelection();
        world.ResetForCharacterSelection();
        Assert.Empty(gameplay.ClientSounds);
        Assert.Empty(gameplay.SoundCommands);
    }


    private static Net7PresentationCatalogs BuildCatalogs()
    {
        var catalogs = new Net7PresentationCatalogs();
        var text = new StringBuilder();
        for (int id = 10; id <= 15; id++)
        {
            text.AppendLine($"[EFFECT-{id}]");
            text.AppendLine("EffectClass=LineEffect");
            text.AppendLine("BaseAssetID=68");
        }
        text.AppendLine("[EFFECT-99]");
        text.AppendLine("EffectClass=PassEffect");
        catalogs.LoadBaseAssetsText("[BASE-10]\nRSLID=10\n[BASE-68]\nRSLID=68\n");
        catalogs.LoadEffectsText(text.ToString());
        return catalogs;
    }
}
