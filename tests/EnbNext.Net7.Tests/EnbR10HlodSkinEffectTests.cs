using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR10HlodSkinEffectTests
{
    [InstalledRetailTheory]
    [InlineData(1191)] [InlineData(1155)]
    public void DirectSkinAttachmentUsesParentHierarchyInsteadOfMountTransform(int id)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var original=EnbCurrentRetailRObjEffectAsset.Load(assets,id);
        var attached=EffectAggregateAnimationFixture.DirectSkinAttachment(assets,id);
        var child=Assert.Single(attached.ModelAttachments);Assert.Empty(child.Asset.Resource!.HierarchicalLods);
        var owner=new EnbSceneObjectSnapshot(1,1,1,false,false,0,id,1,0,0,0,0,false,"direct skin",new(false,0,null),null,null,null,[],[],[],[],[],[],null,[],null,0,0);
        foreach(var world in new[]{Matrix4x4.Identity,Matrix4x4.CreateScale(2)*Matrix4x4.CreateRotationZ(.7f)*Matrix4x4.CreateTranslation(100,200,300)})
        foreach(uint tick in new uint[]{250,500})
        {
            Assert.True(EnbW3dDrawPacketCompiler.TryBuildAtWorldTransform(owner,original.Resource!,world,tick,EnbRenderPassKind.OrdinaryWorld,
                original.Selection,EnbW3dLightEnvironment.BlackNoLights,original.Sample(0,tick),out var control,out var error),error);
            var mount=child.Pose(attached,world,0,tick);
            Assert.True(EnbW3dDrawPacketCompiler.TryBuildAtWorldTransform(owner,child.Asset.Resource!,mount.World,tick,EnbRenderPassKind.OrdinaryWorld,
                null,EnbW3dLightEnvironment.BlackNoLights,[],out var actual,out error,skinParentPose:child.ParentPose(attached,world,0,tick)),error);
            var expected=Assert.Single(control.Packets).DeformedGeometry!;var packet=Assert.Single(actual.Packets);
            Assert.Equal(Matrix4x4.Identity,packet.WorldTransform);Assert.NotNull(packet.DeformedGeometry);
            Assert.Equal(expected.Positions.ToArray(),packet.DeformedGeometry.Positions.ToArray());
            Assert.Equal(expected.Normals.ToArray(),packet.DeformedGeometry.Normals.ToArray());
        }
    }

    [InstalledRetailTheory]
    [InlineData(1191,false)] [InlineData(1191,true)]
    [InlineData(1155,false)] [InlineData(1155,true)]
    public void HmodelOwnedInfluencesUseTheSameBoneTransformsAsHlod(int id,bool skinConnection)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var original=EnbCurrentRetailRObjEffectAsset.Load(assets,id);
        var converted=EffectAggregateAnimationFixture.HmodelSkin(assets,id,skinConnection);
        Assert.Single(converted.Resource!.HierarchyModels);Assert.Empty(converted.Resource.HierarchicalLods);
        var owner=new EnbSceneObjectSnapshot(1,1,1,false,false,0,id,1,0,0,0,0,false,"owned skin fixture",new(false,0,null),null,null,null,[],[],[],[],[],[],null,[],null,0,0);
        foreach(var world in new[]{Matrix4x4.Identity,Matrix4x4.CreateScale(2)*Matrix4x4.CreateRotationZ(.7f)*Matrix4x4.CreateTranslation(100,200,300)})
        {
            EnbW3dDrawTask Draw(EnbCurrentRetailRObjEffectAsset asset)
            {
                Assert.True(EnbW3dDrawPacketCompiler.TryBuildAtWorldTransform(owner,asset.Resource!,world,500,EnbRenderPassKind.OrdinaryWorld,
                    asset.Selection,EnbW3dLightEnvironment.BlackNoLights,asset.Sample(0,500),out var list,out var error),error);
                return Assert.Single(list.Packets);
            }
            var expected=Draw(original);var actual=Draw(converted);
            Assert.Equal(Matrix4x4.Identity,actual.WorldTransform);Assert.NotNull(actual.DeformedGeometry);
            Assert.Equal(expected.DeformedGeometry!.Positions.ToArray(),actual.DeformedGeometry.Positions.ToArray());
            Assert.Equal(expected.DeformedGeometry.Normals.ToArray(),actual.DeformedGeometry.Normals.ToArray());
        }
    }

    [Theory]
    [InlineData(1191)]
    [InlineData(1155)]
    public void InstalledHlodSkinUsesBoneTransformsOnceAndSubmitsIdentityWorld(int id)
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var asset=EnbCurrentRetailRObjEffectAsset.Load(EnbRetailAssetBundle.Load(paths),id);
        var resource=asset.Resource!;
        Assert.Empty(resource.HierarchyModels);
        Assert.Single(resource.HierarchicalLods);
        var mesh=Assert.Single(resource.Meshes);
        Assert.Equal(mesh.Positions.Count,mesh.BoneIndices.Count);
        var owner=new EnbSceneObjectSnapshot(1,1,1,false,false,0,id,1,0,0,0,0,false,"skin fixture",new(false,0,null),null,null,null,[],[],[],[],[],[],null,[],null,0,0);
        var world=Matrix4x4.CreateScale(2)*Matrix4x4.CreateRotationZ(.7f)*Matrix4x4.CreateTranslation(100,200,300);
        EnbW3dDrawTask Build(Matrix4x4 transform)
        {
            Assert.True(EnbW3dDrawPacketCompiler.TryBuildAtWorldTransform(owner,resource,transform,500,EnbRenderPassKind.OrdinaryWorld,
                asset.Selection,EnbW3dLightEnvironment.BlackNoLights,asset.Sample(0,500),out var list,out var error),error);
            return Assert.Single(list.Packets);
        }
        var packet=Build(world);
        Assert.Equal(Matrix4x4.Identity,packet.WorldTransform);
        Assert.NotNull(packet.DeformedGeometry);
        Assert.True(Vector3.Distance(packet.DeformedGeometry.Positions[0],packet.DeformedGeometry.Positions[2])>1,
            "Animated corners must expand beyond the collapsed bind pose");
        float frame=asset.Sample(0,500).Single().Frame;
        Assert.True(EnbW3dHierarchyPose.TryEvaluate(resource.Hierarchies.Single(),world,asset.Animation,frame,out var pose,out var diagnostic),diagnostic);
        for(int i=0;i<mesh.Positions.Count;i++)
            Assert.InRange(Vector3.Distance(packet.DeformedGeometry.Positions[i],Vector3.Transform(mesh.Positions[i],pose.WorldTransforms[mesh.BoneIndices[i]])),0,.0001f);
        var movedWorld=world;movedWorld.Translation+=new Vector3(40,50,60);
        var moved=Build(movedWorld);
        for(int i=0;i<mesh.Positions.Count;i++)
            Assert.InRange(Vector3.Distance(moved.DeformedGeometry!.Positions[i]-packet.DeformedGeometry.Positions[i],new(40,50,60)),0,.0001f);
    }
}
