using System.Buffers.Binary;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbSphereAttachmentTests
{
    [InstalledRetailTheory]
    [InlineData(false,false)] [InlineData(true,false)] [InlineData(false,true)] [InlineData(true,true)]
    public void MultilevelMeshParentRetainsUnscaledPrimitiveConstructionBounds(bool includeRings,bool forceChild)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var loaded=SphereMeshParentFixture.Load(assets,true,includeRings,forceChild);
        Assert.Equal(forceChild,loaded.ForceSubObjectLod);
        Assert.True(loaded.UsesDetailBudget);Assert.Null(loaded.Selection);
        var resource=loaded.Resource!;var hlod=resource.HierarchicalLods.Single();
        var sphere=Assert.Single(loaded.Spheres!.Spheres);
        Assert.Equal(1+(loaded.Rings?.Rings.Count??0),resource.CompositeRenderChildren.Count);
        var sphereChild=Assert.Single(resource.CompositeRenderChildren,c=>c.RetailClassId==0x1d);
        var onlySphere=hlod with {Levels=hlod.Levels.Select(l=>l with {Members=[]}).ToArray(),
            Aggregate=new(0,[new((uint)sphere.PivotIndex,sphereChild.Name)])};
        var isolated=resource.Copy().SetMeshes([]).SetHierarchicalLods([onlySphere]).SetCompositeRenderChildren([sphereChild]);
        var post=new EnbW3dHierarchyState(hlod.HierarchyName,System.Numerics.Matrix4x4.Identity,2,new Dictionary<int,System.Numerics.Matrix4x4>());
        Assert.True(EnbW3dHlodCompositeBoundsCompiler.TryCompute(isolated,onlySphere,System.Numerics.Matrix4x4.Identity,
            Array.Empty<EnbW3dResolvedWeightedAnimationSample>(),post,out var bounds,out var error),error);
        Assert.True(EnbW3dHierarchyPose.TryEvaluate(resource.Hierarchies.Single(),System.Numerics.Matrix4x4.Identity,null,0,null,
            out var pose,out error,storedTranslationScale:2),error);
        var corners=new List<System.Numerics.Vector3>();
        foreach(int x in new[]{-1,1})foreach(int y in new[]{-1,1})foreach(int z in new[]{-1,1})
            corners.Add(System.Numerics.Vector3.Transform(sphere.Asset.Header.Center+sphere.Asset.Header.AuthoredExtent*new System.Numerics.Vector3(x,y,z),pose.WorldTransforms[sphere.PivotIndex]));
        var minimum=corners.Aggregate(System.Numerics.Vector3.Min);var maximum=corners.Aggregate(System.Numerics.Vector3.Max);
        Assert.InRange(System.Numerics.Vector3.Distance(minimum,bounds.LocalBox.Center-bounds.LocalBox.Extents),0,.001f);
        Assert.InRange(System.Numerics.Vector3.Distance(maximum,bounds.LocalBox.Center+bounds.LocalBox.Extents),0,.001f);
    }

    [InstalledRetailFact]
    public void MeshParentRetainsSphereAndEveryRingSibling()
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var original=EnbCurrentRetailRObjEffectAsset.Load(assets,1877);
        var loaded=SphereMeshParentFixture.Load(assets);
        Assert.NotEmpty(loaded.Resource!.Meshes);Assert.False(loaded.UsesDetailBudget);
        Assert.Equal(original.Selection,loaded.Selection);
        Assert.Single(loaded.Spheres!.Spheres);
        Assert.Equal(original.Rings!.Rings.Count,loaded.Rings!.Rings.Count);
        Assert.Equal(original.Resource!.Meshes.Count,loaded.Resource.Meshes.Count);
        Assert.Null(loaded.Mixed);
    }

    [InstalledRetailFact]
    public void SphereAndRingAssemblyRetainsPreparedNextAnimation()
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var asset=EffectAggregateAnimationFixture.Load(assets,next:true,baseId:1857);
        Assert.NotNull(asset.Sequence);Assert.Single(asset.Spheres!.Spheres);Assert.Equal(8,asset.Rings!.Rings.Count);
        // Authored sphere uses pivot0. This synthetic binding exercises motion.
        asset=asset with {Spheres=asset.Spheres with {Spheres=asset.Spheres.Spheres.Select(s=>s with
            {PivotIndex=1,BoneName=asset.Resource!.Hierarchies.Single().Pivots[1].Name}).ToArray()}};
        var observations=asset.Sequence.CreateObservations();var initial=observations.Record(0);
        var current=observations.Record(500);Assert.Equal(2,current.StateId);
        var runtime=new EnbRetailSphereAssemblyRuntime(asset.Spheres);
        var rigid=System.Numerics.Matrix4x4.CreateTranslation(100,200,300);
        var before=runtime.BeforeAllocation(1,500,rigid,2,true,current);
        var sphere=Assert.Single(before.Spheres);
        var expected=asset.Spheres.Pose(rigid,2,500,current);
        Assert.Equal(expected.WorldTransforms[sphere.Binding.PivotIndex].Translation,sphere.World.Translation);
        Assert.NotEqual(asset.Spheres.Pose(rigid,2,0,initial).WorldTransforms[sphere.Binding.PivotIndex].Translation,sphere.World.Translation);
        var draw=runtime.Prepare(1,500,50,rigid,2,true,new HashSet<int>{sphere.Binding.Ordinal},current);
        Assert.Equal(sphere.World,Assert.Single(draw.Spheres).World);
    }

    [Fact]
    public void LargerSphereAndRingPlansRetainEveryOccurrence()
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var assets=EnbRetailAssetBundle.Load(paths);
        var source=assets.BaseAssetResolver!.Resolve(1857).W3d!;
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(source,out var resource,out var error),error);
        var authored=source.CurrentRetailAggregate!.RenderDefinition;
        var expanded=EnbRetailSphereAssembly.Load(assets,resource,authored with
            {Attachments=Enumerable.Range(0,65).SelectMany(_=>authored.Attachments).ToArray()});
        Assert.Equal(65,expanded.Spheres.Count);Assert.Equal(520,expanded.Rings.Count);
        Assert.Equal(585,expanded.Spheres.Select(s=>s.Ordinal).Concat(expanded.Rings.Select(r=>r.Ordinal)).Distinct().Count());
        var ringEntries=authored.Attachments.Where(a=>
        {
            assets.RetailMixResources!.TryReadResource(a.SubobjectName+".w3d",out var bytes,out _);
            return BinaryPrimitives.ReadUInt32LittleEndian(bytes)==0x742;
        }).ToArray();
        var rings=EnbRetailRingAssembly.Load(assets,resource,authored with {Attachments=Enumerable.Repeat(ringEntries[0],65).ToArray()});
        Assert.Equal(65,rings.Rings.Count);Assert.Null(rings.Emitters);
    }

    [Theory]
    [InlineData(1857)] [InlineData(1863)] [InlineData(1865)] [InlineData(1871)]
    [InlineData(2022)] [InlineData(2023)] [InlineData(2024)] [InlineData(2025)]
    [InlineData(2160)] [InlineData(2161)] [InlineData(2203)]
    public void InstalledAggregateChildrenResolveTheirOwnPivotAndTexture(int baseAsset)
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var assets=EnbRetailAssetBundle.Load(paths);
        var resolved=assets.BaseAssetResolver!.Resolve(baseAsset);
        var source=resolved.W3d;
        if(resolved.Rsl is {ResourceGraph:{} graph})
        {
            string objectName=graph.States.First(s=>s.Definition.Type==EnbRslStateType.Object).Definition.Object!;
            Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(assets.RetailMixResources!,objectName,out source,out _));
        }
        Assert.NotNull(source);
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(source,out var resource,out var error),error);
        var tree=resource.Hierarchies.Single();
        var descriptors=source.CurrentRetailAggregate!.Definition.Attachments;
        EnbW3dRenderHierarchyAnimation? animation=null;
        EnbRslStateFlags? flags=null;
        var animationState=resolved.Rsl?.ResourceGraph?.States.FirstOrDefault(s=>s.Definition.Type==EnbRslStateType.Animation);
        if(animationState is not null)
        {
            string name=EnbCurrentRetailRslAnimationRuntime.BuildHAnimName(tree.Name,animationState.Definition.Animation,animationState.Definition.Suffix);
            Assert.True(EnbCurrentRetailRslHAnimResolver.TryResolve(resource,animationState.FileDependency,assets.Paths,assets.MixResources,
                assets.RetailMixResources!,name,out var loaded,out error),error);
            resource=loaded.DrawResource;animation=loaded.Animation;flags=animationState.Definition.Flags;
        }
        int count=0;
        for(int ordinal=0;ordinal<descriptors.Count;ordinal++)
        {
            var descriptor=descriptors[ordinal];
            Assert.True(assets.RetailMixResources!.TryReadResource(descriptor.SubobjectName+".w3d",out var bytes,out _));
            if(BinaryPrimitives.ReadUInt32LittleEndian(bytes)!=0x741)continue;
            var binding=EnbRetailSphereAttachment.Load(assets,tree,descriptor,ordinal);
            Assert.Equal(ordinal,binding.Ordinal);
            Assert.Equal(descriptor.BoneName,tree.Pivots[binding.PivotIndex].Name,ignoreCase:true);
            Assert.Equal(binding.Asset.Header.Texture.Length==0,binding.Texture is null);
            Assert.True(binding.Asset.HasPropertyKeys);Assert.True(binding.Asset.HasScaleKeys);
            Assert.Equal(0,EnbRetailSphereAttachment.Load(assets,tree,descriptor with {BoneName="missing-pivot"},ordinal).PivotIndex);
            var repeated=tree.Copy().SetPivots(tree.Pivots.Select(p=>p with {Name="Repeated"}).ToArray());
            Assert.Equal(0,EnbRetailSphereAttachment.Load(assets,repeated,descriptor with {BoneName="repeated"},ordinal).PivotIndex);
            Assert.Throws<InvalidDataException>(()=>EnbRetailSphereAttachment.Load(assets,tree.Copy().SetPivots([]),descriptor,ordinal));
            count++;
        }
        Assert.True(count>0);
        Assert.True(descriptors.Count>count); // This loader does not represent the entire assembly.
        var assembly=EnbRetailSphereAssembly.Load(assets,resource,source.CurrentRetailAggregate.Definition,animation,flags);
        Assert.Equal(count,assembly.Spheres.Count);
        Assert.Equal(descriptors.Count,assembly.Spheres.Count+assembly.Rings.Count+
            (assembly.Emitters?.Streaks.Count??0)+(assembly.Emitters?.Sprites.Count??0));
        Assert.Equal(assembly.Spheres.Count+assembly.Rings.Count,assembly.Spheres.Select(s=>s.Ordinal).Concat(assembly.Rings.Select(r=>r.Ordinal)).Distinct().Count());
        Assert.Same(animation,assembly.Animation);Assert.Equal(flags,assembly.AnimationFlags);
        var production=EnbCurrentRetailRObjEffectAsset.Load(assets,baseAsset);
        Assert.NotNull(production.Spheres);
        Assert.Equal(assembly.Spheres.Count,production.Spheres.Spheres.Count);
        Assert.Equal(assembly.Rings.Count,production.Rings?.Rings.Count??0);
        Assert.Equal(animation?.Name,production.Animation?.Name);
        Assert.Equal(flags,production.AnimationFlags);
        Assert.Equal(assembly.Emitters is null,production.Mixed is null);
        var shifted=EnbCurrentRetailRObjEffectAsset.Load(assets,baseAsset,new System.Numerics.Vector3(120,.1f,0));
        Assert.Equal(production.Animation?.Name,shifted.Spheres!.Animation?.Name);
        Assert.Equal(production.AnimationFlags,shifted.Spheres.AnimationFlags);
        Assert.Same(shifted.Mixed,shifted.Spheres.Emitters);
        Assert.Equal(shifted.Rings?.Rings.Count??0,shifted.Spheres.Rings.Count);
        foreach(var sphere in shifted.Spheres.Spheres)
        {
            Assert.StartsWith("sphere-hsv:",sphere.Key.ResourceName);
            if(sphere.Texture is not null)Assert.Equal(EnbCurrentRetailAssetVariantName.CreateTexture(sphere.Asset.Header.Texture,new(120,.1f,0)),sphere.Texture.LogicalName);
        }
        Assert.All(production.Spheres.Spheres,s=>Assert.DoesNotContain("hsv:",s.Key.ResourceName));
        if(baseAsset==1857)CheckFramePhases(assembly);
        var forced=EnbRetailSphereAssembly.Load(assets,resource,
            source.CurrentRetailAggregate.Definition with {Flags=1},animation,flags);
        Assert.Equal(assembly.Spheres.Select(s=>s.PivotIndex),forced.Spheres.Select(s=>s.PivotIndex));
        Assert.Equal(assembly.Rings.Select(r=>r.PivotIndex),forced.Rings.Select(r=>r.PivotIndex));
    }

    private static void CheckFramePhases(EnbRetailSphereAssembly assembly)
    {
        var world=System.Numerics.Matrix4x4.Identity;
        var runtime=new EnbRetailSphereAssemblyRuntime(assembly);
        var before=runtime.BeforeAllocation(1,0,world,1,true);Assert.NotEmpty(before.Spheres);
        var drawn=assembly.Spheres.Select(s=>s.Ordinal).ToHashSet();
        var after=runtime.Prepare(1,0,250,world,1,true,drawn);
        Assert.Same(before,runtime.BeforeAllocation(1,999,world,9,false));
        Assert.Same(after,runtime.Prepare(1,999,999,world,9,false,drawn));
        Assert.All(before.Spheres,s=>Assert.Equal(0,s.Progress));
        Assert.All(after.Spheres,s=>Assert.Equal(.25f,s.Progress));
        Assert.True(after.Spheres[0].Bounds.Radius>before.Spheres[0].Bounds.Radius);
        var next=runtime.BeforeAllocation(2,0,world,1,true);
        Assert.Equal(after.Spheres.Select(s=>s.Bounds),next.Spheres.Select(s=>s.Bounds));
        var skipped=runtime.Prepare(2,0,500000,world,1,true,new HashSet<int>());
        Assert.Equal(after.Spheres.Select(s=>s.Values),skipped.Spheres.Select(s=>s.Values));
        Assert.Empty(runtime.Prepare(3,0,500000,world,1,false,drawn).Spheres);
        var resumedBudget=runtime.BeforeAllocation(4,0,world,1,true);
        Assert.All(resumedBudget.Spheres,s=>Assert.Equal(0,s.Progress));
        Assert.Equal(after.Spheres.Select(s=>s.Values),resumedBudget.Spheres.Select(s=>s.Values));
        Assert.Throws<ArgumentOutOfRangeException>(()=>runtime.Prepare(2,0,1,world,1,true,drawn));
        var resumed=runtime.Prepare(4,0,250,world,1,true,drawn);
        Assert.Equal(after.Spheres.Select(s=>s.Values),resumed.Spheres.Select(s=>s.Values));
        var scaled=new EnbRetailSphereAssemblyRuntime(assembly).Prepare(1,0,250,world,2,true,drawn);
        Assert.Equal(after.Spheres[0].Bounds.Radius*2,scaled.Spheres[0].Bounds.Radius);
        Assert.True(EnbResolvedRenderCamera.TryCreate(new(-10,0,0),System.Numerics.Quaternion.Identity,new(800,600,1),
            Math.PI/3,new(.01f,1000),out var camera));
        var originalDraw=after.Spheres[0].DrawWorld(camera);
        var scaledDraw=scaled.Spheres[0].DrawWorld(camera);
        float originalAxis=System.Numerics.Vector3.TransformNormal(System.Numerics.Vector3.UnitX,originalDraw).Length();
        Assert.Equal(originalAxis*2,System.Numerics.Vector3.TransformNormal(System.Numerics.Vector3.UnitX,scaledDraw).Length());
        var candidates=after.Spheres.Select(s=>s.Candidate("sphere/"+s.Binding.Ordinal,camera,10)).ToArray();
        var allocation=EnbRenderDetailBudgetAllocator.AllocateShared(100,candidates);
        Assert.InRange(allocation.FinalRenderCost,0,100);
        Assert.Equal(.25f,after.Spheres[0].Progress);
    }
}
