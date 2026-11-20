using EnbNext.Assets;
using EnbNext.Net7.World;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbWarpPlumeTests
{
    private static EnbSceneObjectSnapshot Ship()
    {
        var world=new Net7WorldState();world.MaterializeCreate(new(23,1,1620,1,0,0,0));
        return new EnbPresentationSnapshotBuilder().Build(world,null,0).Objects[0] with {ShipPresentation=new(false,0,0,0,false,null,0,[],[])};
    }
    [Theory]
    [InlineData(3)][InlineData(7)][InlineData(8)][InlineData(9)][InlineData(11)]
    public void ChargeEnterExitAndRepeatedSnapshotsOwnOnePlume(int exit)
    {
        var ship=Ship();var runtime=new EnbWarpPlumeRuntime();runtime.Update([ship]);
        ship=ship with {ShipPresentation=ship.ShipPresentation! with {WarpChanges=[new(1,1,5,10)]}};
        runtime.Update([ship]);Assert.Null(runtime.Get(ship.RenderIdentity));
        ship=ship with {ShipPresentation=ship.ShipPresentation with {WarpChanges=[new(1,1,5,10),new(2,2,5,10)]}};
        runtime.Update([ship]);Assert.Equal(2ul,runtime.Get(ship.RenderIdentity));
        runtime.Update([ship]);Assert.Equal(2ul,runtime.Get(ship.RenderIdentity));
        ship=ship with {ShipPresentation=ship.ShipPresentation with {PrivateWarpState=6}};
        runtime.Update([ship]);Assert.Equal(2ul,runtime.Get(ship.RenderIdentity));
        ship=ship with {ShipPresentation=ship.ShipPresentation with {WarpChanges=[new(1,1,5,10),new(2,2,5,10),new(3,exit,5,10)]}};
        runtime.Update([ship]);Assert.Null(runtime.Get(ship.RenderIdentity));
    }
    [Fact]
    public void NewOwnerDoesNotReplayAnOldChargeAndRemovalDropsOwnership()
    {
        var ship=Ship();ship=ship with {ShipPresentation=ship.ShipPresentation! with {WarpChanges=[new(1,1,5,10),new(2,2,5,10)]}};
        var runtime=new EnbWarpPlumeRuntime();runtime.Update([ship]);Assert.Null(runtime.Get(ship.RenderIdentity));
        ship=ship with {ShipPresentation=ship.ShipPresentation with {WarpChanges=[new(3,1,5,10)]}};runtime.Update([ship]);
        ship=ship with {ShipPresentation=ship.ShipPresentation with {WarpChanges=[new(3,1,5,10),new(4,2,5,10)]}};runtime.Update([ship]);
        Assert.Equal(4ul,runtime.Get(ship.RenderIdentity));runtime.Update([]);Assert.Null(runtime.Get(ship.RenderIdentity));
        runtime.Clear();runtime.Update([ship]);Assert.Null(runtime.Get(ship.RenderIdentity));
    }
    [Fact]
    public void InstalledAfterburnPreparesBothParticleChildrenWithoutInventingMeshMembers()
    {
        var paths=EnbInstalledAssetLocator.TryLocate();if(paths is null)return;
        var assets=EnbRetailAssetBundle.Load(paths);
        var afterburn=EnbWarpPlumeAsset.LoadAttachEffect(assets,674);
        Assert.Empty(afterburn.Resource.Meshes);
        Assert.True(afterburn.HideAsOtherObjects);
        Assert.Equal(2,afterburn.Particles.Count);
        var tree=Assert.Single(afterburn.Resource.Hierarchies);
        foreach(var child in afterburn.Particles)
        {
            Assert.InRange(child.Pivot,0,tree.Pivots.Count-1);
            Assert.NotNull(child.Asset.Texture);
            Assert.Null(child.Asset.Error);Assert.Null(child.Asset.DeferredReason);
            var runtime=new EnbCurrentRetailShipParticleRuntime(child.Asset.Definition);
            runtime.Advance(0,System.Numerics.Matrix4x4.Identity,true);
            runtime.Advance(1000,System.Numerics.Matrix4x4.CreateTranslation(0,100,0),true);
            Assert.NotEmpty(runtime.Births);
            runtime.Advance(1001,System.Numerics.Matrix4x4.Identity,false);
            runtime.Advance(6000,System.Numerics.Matrix4x4.Identity,false);
            Assert.Empty(runtime.Births);
        }
    }

    [Fact]
    public void InstalledPlumeAndDefaultExtensionDecodeWithoutDiscardingUnknownState()
    {
        var paths=EnbInstalledAssetLocator.TryLocate();if(paths is null)return;
        var assets=EnbRetailAssetBundle.Load(paths);var plume=EnbWarpPlumeAsset.Load(assets);
        Assert.Equal("exhaust",plume.BonePrefix,true);Assert.Equal(3,plume.Resource.Meshes.Count);
        var particle=Assert.Single(plume.Particles).Asset;Assert.Equal(3.5f,particle.Definition.LifetimeSeconds);
        Assert.True(EnbCurrentRetailParticleEmitterRuntime.SupportsEngineTrail(particle.Definition));
        Assert.True(assets.RetailMixResources!.TryReadResource(particle.Definition.Name+".w3d",out var bytes,out _));
        for(int off=8;off+8<=bytes.Length;)
        {
            uint type=System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(off));
            int length=(int)(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(off+4))&0x7fffffff);
            if(type==0x50d)
            {
                var changed=bytes.ToArray();changed[off+16]=1; // Reserved extension word, not the now-decoded initial time.
                Assert.False(EnbCurrentRetailParticleEmitterDecoder.TryDecode(changed,out _,out var error));Assert.Contains("0x050D",error);return;
            }
            off+=8+length;
        }
        Assert.Fail("Installed warp trail omitted expected extension fixture");
    }
}
