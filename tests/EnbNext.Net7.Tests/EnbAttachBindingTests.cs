using System.Buffers.Binary;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.World;
using EnbNext.Presentation;
using EnbNext.Protocol.Net;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbAttachBindingTests
{
    [Theory]
    [InlineData(120f)]
    [InlineData(-120f)]
    public void ObjectPacketColourDoesNotDisplaceAttachmentOrSound(float hue)
    {
        var world=new Net7WorldState();world.MaterializeCreate(new(23,1,1620,1,0,0,0));
        byte[] pose=new byte[32];BinaryPrimitives.WriteUInt32LittleEndian(pose,23);
        BinaryPrimitives.WriteSingleLittleEndian(pose.AsSpan(4),42);
        BinaryPrimitives.WriteSingleLittleEndian(pose.AsSpan(28),1);
        world.Apply(Packet.ForOpcode(0x40,pose),0);
        var game=new Net7GameplayState();
        byte[] wire=new byte[23];wire[0]=0x71;
        BinaryPrimitives.WriteUInt32LittleEndian(wire.AsSpan(1),23);
        BinaryPrimitives.WriteUInt16LittleEndian(wire.AsSpan(5),674);
        BinaryPrimitives.WriteUInt32LittleEndian(wire.AsSpan(7),123);
        BinaryPrimitives.WriteSingleLittleEndian(wire.AsSpan(11),hue);
        BinaryPrimitives.WriteSingleLittleEndian(wire.AsSpan(15),.1f);
        BinaryPrimitives.WriteSingleLittleEndian(wire.AsSpan(19),.2f);
        Assert.True(game.Apply(Packet.ForOpcode(9,wire)));
        var scene=new EnbPresentationSnapshotBuilder().Build(world,game.Effects,null,0);
        var catalog=new EnbRetailPresentationCatalog();
        catalog.LoadEffectsText("[EFFECT-674]\nEffectClass=AttachEffect\nDuration=INFINITE\nRObjHSV=10,0,0\nSFX=afterburn.wav");
        var node=Assert.Single(new EnbCurrentRetailEffectRslSchedulerRuntime().Update(scene.Effects,catalog,0).VisualNodes);
        Assert.True(EnbCurrentRetailEffectFactoryHsv.TryRead(node,out var hsv,out var error),error);
        Assert.Equal(new Vector3(hue+10,.1f,.2f),hsv);
        Assert.Equal(Vector3.Zero,node.Root.SpatialOffset);
        node.UpdateAttachment(scene.Objects);
        Assert.Equal(Assert.Single(scene.Objects),node.Attachment?.Owner);
        Assert.Equal(new Vector3(42,0,0),node.Attachment!.SoundPosition);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActivationRefreshIsSeparateFromOngoingBoneOwnerMotion(bool track)
    {
        var world=new Net7WorldState();world.MaterializeCreate(new(23,1,1620,1,0,0,0));
        var builder=new EnbPresentationSnapshotBuilder();
        EnbSceneObjectSnapshot At(float x)
        {
            var payload=new byte[32];BinaryPrimitives.WriteUInt32LittleEndian(payload,23);
            BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(4),x);
            BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(28),1);
            world.Apply(Packet.ForOpcode(0x40,payload),0);
            return Assert.Single(builder.Build(world,null,0).Objects);
        }
        var initial=At(10);
        var gameplay=new Net7GameplayState();gameplay.AddSyntheticEffect(new(1,23,23,674,0,0,"",0,false));
        var root=Assert.Single(builder.Build(world,gameplay.Effects,null,0).Effects)
            with {InitialSourceObject=initial,EndpointReceipt=new(initial.WorldCreationOrdinal,null)};
        var catalog=new EnbRetailPresentationCatalog();
        catalog.LoadEffectsText("[EFFECT-674]\nEffectClass=AttachEffect\nDuration=INFINITE"+(track?"\nTrackSource=TRUE":""));
        var node=new EnbCurrentRetailEffectOwner(1,catalog.FindEffect(674)!,root,23,false,0);

        node.UpdateAttachment([initial]);Assert.Null(node.Attachment?.Owner);
        node.Active=true;node.ActualStartTick=500;
        var activation=At(20);node.UpdateAttachment([activation]);
        Assert.Equal(new Vector3(20,0,0),node.Attachment!.SoundPosition);
        var moved=At(30);node.UpdateAttachment([moved]);node.UpdateAttachment([moved]);
        Assert.Equal(moved,node.Attachment?.Owner);
        Assert.Equal(new Vector3(track?30:20,0,0),node.Attachment!.SoundPosition);
        var sound=new EnbCurrentRetailEffectSoundNode(1,node.Definition,root,23,EnbCurrentRetailEffectLinkKind.Root,true,500);
        Assert.False(EnbCurrentRetailEffectSoundRuntime.TryResolvePosition(sound,_=>new Vector3(999,0,0),out _,out _,out _));
        Assert.True(EnbCurrentRetailEffectSoundRuntime.TryResolvePosition(sound,_=>new Vector3(999,0,0),out var position,out var follows,out _,
            findEffectPosition:n=>node.Attachment!.SoundPosition));
        Assert.Equal(new Vector3(track?30:20,0,0),position);Assert.Equal(track,follows);
        node.UpdateAttachment([]);Assert.Null(node.Attachment?.Owner);
        node.UpdateAttachment([moved]);Assert.Null(node.Attachment?.Owner);
        Assert.Equal(new Vector3(track?30:20,0,0),node.Attachment!.SoundPosition);
        node.ClearAttachment();Assert.Null(node.Attachment);
    }
}
