using System.Numerics;
using EnbNext.Assets;
using EnbNext.Net7.World;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbBloodSoundTests
{
    [Fact]
    public void AuthoredDamageSoundsHaveIndependentKeysAndReleasedPosition()
    {
        var catalog=new EnbBloodCatalog(EnbRetailIniDocument.Parse("[Blood0]\nSound=impact.wav\nLoopingSound=hum.wav\n[Blood1]\n"));
        Assert.Equal("impact.wav",catalog.Resolve(393,false).Sound);
        Assert.Equal("hum.wav",catalog.Resolve(393,false).LoopingSound);
        Assert.Empty(catalog.Resolve(393,true).Sound);
        var world=new Net7WorldState();
        world.MaterializeCreate(new Net7CreateUpdate(23,1,393,0,0,0,0));
        var obj=new EnbPresentationSnapshotBuilder().Build(world,null,0).Objects[0];
        var origin=new Vector3(7,8,9);
        var node=EnbBloodSoundRuntime.CreateReleased(obj,"impact.wav",1,200,origin);
        Assert.Null(node.BoundGameId);
        Assert.False(node.Definition.TrackSource);
        Assert.False(node.Definition.GoAwayWhenSourceDoes);
        Assert.False(node.EndedAtActivation);
        Assert.True(EnbCurrentRetailEffectSoundRuntime.TryResolvePosition(node,_=>null,out var actual,out bool follows,out _,
            findEffectPosition:_=>new Vector3(999)));
        Assert.Equal(origin,actual);
        Assert.False(follows);
    }

    [Fact]
    public void InstalledMechanicalLoopIsAuthoredAndOrganicDefaultHasNone()
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var assets=EnbRetailAssetBundle.Load(paths);
        string sound=assets.Blood!.Resolve(393,false).LoopingSound;
        Assert.Equal("Electrical_Ship_Damage",sound);
        Assert.Equal(0,assets.Sounds!.GetDefinitionOrFile(sound).EffectiveLoopCount);
        Assert.Empty(assets.Blood.Resolve(393,true).LoopingSound);
        Assert.Empty(assets.Blood.Resolve(400,false).LoopingSound); // authored crystal override
    }

    [Fact]
    public void DamageLoopUsesCurrentOriginalSourceAndCannotBorrowEffectPoseOrReusedId()
    {
        var world=new Net7WorldState();
        world.MaterializeCreate(new Net7CreateUpdate(23,1,393,0,0,0,0));
        var obj=new EnbPresentationSnapshotBuilder().Build(world,null,0).Objects[0] with {RenderIdentity=456};
        var node=EnbBloodSoundRuntime.CreateLoop(obj,"Electrical_Ship_Damage",19,200);
        Vector3 current=new(30,40,50);
        Assert.True(EnbCurrentRetailEffectSoundRuntime.TryResolvePosition(node,_=>current,out var position,out bool follows,out _,
            findEffectPosition:_=>new Vector3(999)));
        Assert.Equal(current,position);
        Assert.True(follows);
        Assert.False(EnbCurrentRetailEffectSoundRuntime.IsOriginalOwner(node,23,_=>457));
        Assert.True(EnbCurrentRetailEffectSoundRuntime.IsOriginalOwner(node,23,_=>456));
        Assert.False(EnbCurrentRetailEffectSoundRuntime.TryResolvePosition(node,_=>null,out _,out _,out _));
    }
}
