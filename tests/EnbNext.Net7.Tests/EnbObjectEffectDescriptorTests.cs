using EnbNext.Assets;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.World;
using EnbNext.Presentation;
using EnbNext.Protocol.Net;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbObjectEffectDescriptorTests
{
    private static EnbRetailPresentationCatalog Catalog(string rootClass)
    {
        var catalog=new EnbRetailPresentationCatalog();
        catalog.LoadEffectsText($"[EFFECT-566]\nEffectClass={rootClass}\nDuration=.92\nStartLinkID=567\nSFX=damage_add_app.wav\n"+
            "[EFFECT-567]\nEffectClass=LineEffect\nDuration=1.42\nStartLinkID=568\n"+
            "[EFFECT-568]\nEffectClass=RObjLookAtEffect\nDuration=1.42\n");
        return catalog;
    }

    [Theory]
    [InlineData("RObjLookAtEffect")]
    [InlineData("LineEffect")]
    public void ObjectPacketRegistersUnboundBinaryTreeAndRetainsLinksAndSound(string rootClass)
    {
        var game=new Net7GameplayState(convertServerTimestamp:_=>0);
        // Exact E566 packet from the user's passive Jupiter capture.
        game.Apply(Packet.ForOpcode(0x0009,Convert.FromHexString("07B387010036028BA814006415A13BE803")));
        var root=Assert.Single(new EnbPresentationSnapshotPublisher().Publish(new Net7WorldState(),game.Effects,null,0).Effects);
        Assert.Equal(EnbSceneEffectGeometryKind.Object,root.GeometryKind);
        Assert.Equal(0x187B3u,root.SourceGameId);
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        var catalog=Catalog(rootClass);
        var first=scheduler.Update([root],catalog,0);
        Assert.Equal(0,first.Receipt.RejectedRoots);
        Assert.NotEmpty(first.VisualNodes);
        Assert.All(first.VisualNodes,n=>{Assert.True(n.HasUnboundEndpoints);Assert.Null(n.BoundGameId);});
        var sound=Assert.Single(first.SoundNodes);
        Assert.True(EnbCurrentRetailEffectSoundRuntime.TryResolvePosition(sound,_=>new System.Numerics.Vector3(100,200,300),out var position,out _,out var error));
        Assert.Null(error);Assert.Equal(System.Numerics.Vector3.Zero,position);
        var lines=new EnbCurrentRetailLineBindingRuntime();
        lines.Update(first.VisualNodes,[],(_,_)=>throw new Exception("Unbound effects must not resolve a world pose"));
        foreach(var node in first.VisualNodes.Where(n=>n.Definition.EffectClassKind==EnbEffectClassKind.LineEffect))
        {
            var binding=lines.Resolve(node,out error);
            Assert.Null(error);Assert.NotNull(binding);
            Assert.Equal(System.Numerics.Vector3.Zero,binding.Start);Assert.Equal(System.Numerics.Vector3.Zero,binding.End);
            Assert.True(binding.SourceDetached);Assert.True(binding.TargetDetached);
        }
        Assert.NotEmpty(scheduler.Update([root],catalog,500).VisualNodes);
        Assert.Single(game.Effects); // Presentation policy does not reject/alter the protocol event.
        scheduler.Update([],catalog,2000);
        Assert.Equal(0,scheduler.TrackedRootCount);
    }

    [Theory]
    [InlineData("LineEffect")]
    [InlineData("RObjLookAtEffect")]
    public void UnboundTreeDoesNotObservePacketOwnerRemoval(string rootClass)
    {
        var owner=new EnbSceneObjectSnapshot(10,1,123,false,false,1,48,1,0,0,0,0,false,"fixture",new(false,0,null),
            new(EnbSceneSpatialProviderKind.Fixed,new(0,System.Numerics.Vector3.One,System.Numerics.Quaternion.Identity,default,0,default,System.Numerics.Vector3.UnitX),null),
            null,null,[],[],[],[],[],[],null,[],null,0,0);
        var root=new EnbSceneEffectSnapshot(10,1,123,0,1,0,-1,"",0,false,1,default,0,0,false,default,1,EnbSceneEffectGeometryKind.Object,default,-1,default,0,0,null)
            with {InitialSourceObject=owner,LastSourceObject=owner};
        var catalog=new EnbRetailPresentationCatalog();
        catalog.LoadEffectsText($"[EFFECT-1]\nEffectClass={rootClass}\nDuration=-1\nGoAwayWhenSourceDoes=1\nStartLinkID=2\n[EFFECT-2]\nEffectClass=RObjEffect\nDuration=-1\nGoAwayWhenSourceDoes=1");
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        var first=scheduler.Update([root],catalog,0,worldObjects:[owner]);
        Assert.NotEmpty(first.VisualNodes);
        var removed=scheduler.Update([root],catalog,500,worldObjects:[]);
        Assert.Equal(0,removed.Receipt.RemovalMarks);
        Assert.Equal(first.VisualNodes.Select(n=>n.Sequence),removed.VisualNodes.Select(n=>n.Sequence));
    }

    [Fact]
    public void SameBinaryClassStillInstantiatesFromBinaryPacket()
    {
        var game=new Net7GameplayState(convertServerTimestamp:_=>0);
        game.Apply(Packet.ForOpcode(0x0009,Convert.FromHexString("07B387010036028BA814006415A13BE803")));
        var root=Assert.Single(new EnbPresentationSnapshotPublisher().Publish(new Net7WorldState(),game.Effects,null,0).Effects)
            with {GeometryKind=EnbSceneEffectGeometryKind.ObjectToObject,IsObjectToObject=true,TargetGameId=2};
        var update=new EnbCurrentRetailEffectRslSchedulerRuntime().Update([root],Catalog("RObjLookAtEffect"),0);
        Assert.NotEmpty(update.VisualNodes);
    }

    [Fact]
    public void UnaryObjectEffectRemainsAdmitted()
    {
        var game=new Net7GameplayState(convertServerTimestamp:_=>0);
        game.Apply(Packet.ForOpcode(0x0009,Convert.FromHexString("07B387010036028BA814006415A13BE803")));
        var root=Assert.Single(new EnbPresentationSnapshotPublisher().Publish(new Net7WorldState(),game.Effects,null,0).Effects);
        var update=new EnbCurrentRetailEffectRslSchedulerRuntime().Update([root],Catalog("RObjEffect"),0);
        Assert.Contains(update.VisualNodes,n=>n.Definition.EffectClassKind==EnbEffectClassKind.RObjEffect);
    }
}
