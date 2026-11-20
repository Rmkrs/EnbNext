using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

[Xunit.Trait("Category", "Heavy")] // retail/hardware verification, >=5 s; run with --filter Category=Heavy
public sealed class EnbEffectModelAttachmentTests
{
    [Theory]
    [InlineData(130)] [InlineData(230)]
    public void RecursiveBoundsRefreshDoesNotRebuildConstructorChildCosts(int baseId)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var parent=EffectAggregateAnimationFixture.HmodelStreak(assets,baseId,true,modelSibling:true,modelPrototype:"missile",wrapModel:true);
        var child=parent.ModelAttachments[0].Asset.Resource!;
        var hlod=child.HierarchicalLods.Single();
        Assert.True(EnbW3dHlodBudgetCompiler.TryComputeW3dLevelRenderCosts(child,hlod,_=>true,out var costs,out var why),why);
        int nativeCost=costs[EnbW3dHlodBudgetCompiler.CurrentRetailInitialLevel(hlod)];
        foreach(var member in hlod.Aggregate!.Members)
        {
            Assert.True(EnbW3dHlodBudgetCompiler.TryResolveNamedRenderObjectCost(child,member.Name,out int cost,out why),why);
            nativeCost=unchecked(nativeCost+cost);
        }
        Assert.Equal(nativeCost,parent.Resource!.CompositeRenderChildren.Single().ConstructorIntegerCost);
        var current=new[]{parent.Resource!}.Concat(parent.ModelAttachments.Select(m=>m.Asset.Resource!)).ToArray();
        var expected=current.Select(r=>r.CompositeRenderChildren.Select(c=>c.ConstructorIntegerCost).ToArray()).ToArray();
        var history=new EnbEffectCompositeBoundsHistory(parent);
        history.Remove(0,current);
        var projected=history.Project(current);
        for(int i=0;i<projected.Length;i++)Assert.Equal(expected[i],projected[i].CompositeRenderChildren.Select(c=>c.ConstructorIntegerCost));
    }
    [Theory]
    [InlineData(130)] [InlineData(230)]
    public void LevelOwnedSubtreeRetainsMembershipAcrossFlatteningAndReconstruction(int baseId)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var parent=EffectAggregateAnimationFixture.HmodelStreak(assets,baseId,true,modelSibling:true,modelPrototype:"missile",wrapModel:true);
        var original=Assert.Single(EnbRObjModelAttachment.RebuildHierarchy(parent.ModelAttachments));
        var models=EnbRObjModelAttachment.Flatten([original with {LevelOrdinal=1}]);
        Assert.All(models,model=>Assert.False(model.PreparesDetail));
        Assert.All(models,model=>Assert.False(model.IsSelected(models,index=>index<0?0:1)));
        Assert.All(models,model=>Assert.True(model.IsSelected(models,index=>index<0?1:0)));
        var rebuilt=Assert.Single(EnbRObjModelAttachment.RebuildHierarchy(models));
        Assert.Equal(1,rebuilt.LevelOrdinal);
        Assert.Null(Assert.Single(rebuilt.Asset.ModelAttachments).LevelOrdinal);
        Assert.All(EnbRObjModelAttachment.Flatten([rebuilt]),model=>Assert.False(model.PreparesDetail));
    }
    [Theory]
    [InlineData(130)] [InlineData(230)]
    public void NestedModelEmitterAssemblyRetainsItsModelAndEmitterChildren(int baseId)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var parent=EffectAggregateAnimationFixture.HmodelStreak(assets,baseId,true,modelSibling:true,modelPrototype:"missile",wrapModel:true,wrappedChildPrototype:"WE_WAVE01_EM");
        Assert.Contains(parent.ModelAttachments,m=>m.Asset.CompositeEmitters.Count>0);
        Assert.Contains(parent.ModelAttachments,m=>m.ParentModelIndex>=0);
        var history=new EnbEffectCompositeBoundsHistory(parent);
        var current=new[]{parent.Resource!}.Concat(parent.ModelAttachments.Select(m=>m.Asset.Resource!)).ToArray();
        int model=Array.FindIndex(parent.ModelAttachments.ToArray(),m=>m.Asset.CompositeEmitters.Count>0);
        Assert.True(parent.ModelAttachments[model].ParentModelIndex>=0);
        var removedNames=parent.ModelAttachments[model].Asset.CompositeEmitters.Select(e=>e.Asset.Definition.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var before=history.Bounds(-1);
        current[model+1]=current[model+1].Copy().SetHierarchicalLods(current[model+1].HierarchicalLods.Select(h=>h with
        {
            Levels=h.Levels.Select(l=>l with {Members=l.Members.Where(m=>!removedNames.Contains(m.Name)).ToArray()}).ToArray(),
            Aggregate=h.Aggregate is {} a?a with {Members=a.Members.Where(m=>!removedNames.Contains(m.Name)).ToArray()}:null
        }).ToArray());
        history.Remove(model,current);
        Assert.NotSame(before,history.Bounds(-1));
        Assert.DoesNotContain(history.Bounds(model).HierarchicalLods.SelectMany(h=>h.Levels.SelectMany(l=>l.Members).Concat(h.Aggregate?.Members??[])),m=>removedNames.Contains(m.Name));
        var nonemptyParent=history.Bounds(-1);
        current[model+1]=current[model+1].Copy().SetHierarchicalLods(current[model+1].HierarchicalLods.Select(h=>h with
            {Levels=h.Levels.Select(l=>l with {Members=[]}).ToArray(),Aggregate=null}).ToArray());
        history.Remove(model,current);
        Assert.Same(nonemptyParent,history.Bounds(-1));
        var empty=history.Bounds(model);
        Assert.True(EnbW3dHlodCompositeBoundsCompiler.TryCompute(empty,empty.HierarchicalLods.Single(),Matrix4x4.Identity,null,0,out var bounds,out var error),error);
        Assert.Equal(0,bounds.LocalSphere.Radius);
        history.Remove(-1,current);
        Assert.NotSame(nonemptyParent,history.Bounds(-1));
    }
    [Theory]
    [InlineData(130,"missile")] [InlineData(230,"missile")]
    [InlineData(130,"RVC02B")] [InlineData(230,"RVC02B")]
    public void NestedCompositeAttachmentsRetainTheirConcreteMeshChildren(int baseId,string prototype)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var parent=EffectAggregateAnimationFixture.HmodelStreak(assets,baseId,true,modelSibling:true,modelPrototype:prototype,wrapModel:true);
        Assert.NotEmpty(parent.ModelAttachments);
        //The wrapper's SPH13 Mesh is retained by the HMODEL/HLOD insertion
        //virtual, unlike the concrete-leaf no-op insertion cases below.
        Assert.Contains(parent.ModelAttachments.SelectMany(m=>m.Asset.Resource?.Meshes??[]),m=>m.Name.Equals("SPH134",StringComparison.OrdinalIgnoreCase));
        var outer=Assert.Single(parent.ModelAttachments,m=>m.ParentModelIndex<0);
        var inner=Assert.Single(parent.ModelAttachments,m=>m.ParentModelIndex==0);
        var rebuilt=Assert.Single(EnbRObjModelAttachment.RebuildHierarchy(parent.ModelAttachments));
        Assert.Same(inner.Asset.Resource,Assert.Single(rebuilt.Asset.ModelAttachments).Asset.Resource);
        Assert.Empty(Assert.Single(rebuilt.Asset.ModelAttachments).Ancestors);
        var observations=parent.Sequence?.CreateObservations();
        var world=Matrix4x4.CreateScale(2)*Matrix4x4.CreateRotationZ(.7f)*Matrix4x4.CreateTranslation(17,29,43);
        foreach(uint tick in new uint[]{0,500,1000})
        {
            var sample=observations?.Record(tick);
            float frame=sample?.Frame??0;
            if(sample is null && parent.Animation is not null)
                Assert.True(EnbCurrentRetailRslAnimationRuntime.TryComputeFrame(parent.Animation,parent.AnimationFlags!,0,tick,out frame,out _));
            Assert.True(EnbW3dHierarchyPose.TryEvaluate(parent.Resource!.Hierarchies.Single(),world,
                sample?.BoundAnimation??parent.Animation,frame,null,out var rootPose,out var error),error);
            Assert.True(EnbW3dHierarchyPose.TryEvaluate(outer.Asset.Resource!.Hierarchies.Single(),rootPose.WorldTransforms[outer.PivotIndex],
                null,0,null,out var childPose,out error),error);
            var actual=inner.Pose(parent,world,0,tick,observed:sample);
            Assert.Equal(childPose.WorldTransforms[inner.PivotIndex],actual.World);
            Assert.Equal(rootPose.Visibility[outer.PivotIndex]&&childPose.Visibility[inner.PivotIndex],actual.Visible);
        }
    }
    [Theory]
    [InlineData(130,false)] [InlineData(230,false)] [InlineData(130,true)] [InlineData(230,true)]
    public void NamedNullAttachmentsRetainBoundsWithoutGeometry(int baseId,bool wrapped)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var parent=EffectAggregateAnimationFixture.HmodelStreak(assets,baseId,true,modelSibling:true,modelPrototype:"ATTACHVOID",wrapModel:wrapped);
        var child=Assert.Single(parent.ModelAttachments).Asset;
        Assert.True(child.IsNullObject);Assert.False(child.UsesDetailBudget);
        Assert.Empty(child.ModelAttachments);Assert.Empty(child.Resource!.Meshes);
        Assert.Equal(EnbW3dExternalRenderObjectKind.NullObject,Assert.Single(child.Resource.ExternalRenderObjects).Kind);
        var descriptor=Assert.Single(parent.Resource!.CompositeRenderChildren);
        Assert.Equal(0x16,descriptor.RetailClassId);Assert.Equal(0,descriptor.ConstructorIntegerCost);
        Assert.Equal(new Vector4(0,0,0,.1f),descriptor.ConstructorBounds.Sphere);
        Assert.Equal(Vector3.Zero,descriptor.ConstructorBounds.BoxCenter);
        Assert.Equal(new Vector3(.1f),descriptor.ConstructorBounds.BoxExtent);
    }
    [Theory]
    [InlineData(130,"missile")] [InlineData(230,"missile")]
    [InlineData(130,"RVC02B")] [InlineData(230,"RVC02B")]
    public void EmptyWrapperKeepsConcreteCompositeChild(int baseId,string prototype)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var direct=EffectAggregateAnimationFixture.HmodelStreak(assets,baseId,true,modelSibling:true,modelPrototype:prototype);
        var wrapped=EffectAggregateAnimationFixture.HmodelStreak(assets,baseId,true,modelSibling:true,modelPrototype:prototype,wrapModel:true,wrapperHasChildren:false);
        var expected=Assert.Single(direct.ModelAttachments).Asset;
        var actual=Assert.Single(wrapped.ModelAttachments).Asset;
        Assert.Empty(actual.ModelAttachments);Assert.Equal(expected.UsesDetailBudget,actual.UsesDetailBudget);
        Assert.Equal(expected.Resource!.HierarchicalLods.Single().Levels.Count,actual.Resource!.HierarchicalLods.Single().Levels.Count);
        Assert.Equal(Assert.Single(direct.Resource!.CompositeRenderChildren).ConstructorBounds,
            Assert.Single(wrapped.Resource!.CompositeRenderChildren).ConstructorBounds);
    }

    [Theory]
    [InlineData(130,"SPH13.SPH134")] [InlineData(230,"SPH13.SPH134")]
    [InlineData(130,"V_DECEPT01A.OBJECT015")] [InlineData(230,"V_DECEPT01A.OBJECT015")]
    [InlineData(130,"SK_AFBURN01A.DAZZLE")] [InlineData(230,"SK_AFBURN01A.DAZZLE")]
    public void WrappedLeafAttachmentsRetainConcreteBaseAndDiscardNoOpInsertions(int baseId,string prototype)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var direct=EffectAggregateAnimationFixture.HmodelStreak(assets,baseId,true,modelSibling:true,modelPrototype:prototype);
        var wrapped=EffectAggregateAnimationFixture.HmodelStreak(assets,baseId,true,modelSibling:true,modelPrototype:prototype,wrapModel:true);
        var expected=Assert.Single(direct.ModelAttachments).Asset;
        var actual=Assert.Single(wrapped.ModelAttachments).Asset;
        Assert.Empty(actual.ModelAttachments);Assert.Null(actual.Mixed);Assert.Null(actual.AggregateParticles);
        Assert.Equal(expected.Resource!.Meshes.Count,actual.Resource!.Meshes.Count);
        Assert.Equal(expected.StandaloneBox is not null,actual.StandaloneBox is not null);
        Assert.Equal(expected.StandaloneDazzle?.DazzleDefinition?.Type,actual.StandaloneDazzle?.DazzleDefinition?.Type);
        Assert.Equal(Assert.Single(direct.Resource!.CompositeRenderChildren).ConstructorBounds,
            Assert.Single(wrapped.Resource!.CompositeRenderChildren).ConstructorBounds);
        Assert.Equal(Assert.Single(direct.Resource.CompositeRenderChildren).ConstructorIntegerCost,
            Assert.Single(wrapped.Resource.CompositeRenderChildren).ConstructorIntegerCost);
    }

    [Theory]
    [InlineData(130,false)] [InlineData(130,true)] [InlineData(230,false)] [InlineData(230,true)]
    public void DirectBoxAndDazzleAttachmentsKeepConcreteLeafBounds(int baseId,bool dazzle)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        Assert.True(assets.FileResources.TryRead("sk_afburn01a.w3d",out var bytes,out _,out _));
        Assert.True(EnbW3dModel.TryDecode(bytes,out var model));
        string name=dazzle?Assert.Single(model.Dazzles).Name:"V_DECEPT01A.OBJECT015";
        var parent=EffectAggregateAnimationFixture.HmodelStreak(assets,baseId,true,modelSibling:true,modelPrototype:name);
        var child=Assert.Single(parent.ModelAttachments).Asset;
        var descriptor=Assert.Single(parent.Resource!.CompositeRenderChildren);
        Assert.Empty(child.Resource!.Meshes);Assert.Empty(child.Resource.HierarchicalLods);
        if(dazzle)
        {
            var external=Assert.Single(child.Resource.ExternalRenderObjects);
            float radius=external.DazzleDefinition!.Radius*external.DazzleInstanceScale;
            Assert.Equal(0x20,descriptor.RetailClassId);Assert.Equal(0,descriptor.ConstructorIntegerCost);
            Assert.Equal(new Vector4(0,0,0,radius),descriptor.ConstructorBounds.Sphere);
        }
        else
        {
            var box=child.StandaloneBox!;
            Assert.Equal(box.RetailClassId,descriptor.RetailClassId);Assert.Equal(12,descriptor.ConstructorIntegerCost);
            Assert.Equal(EnbCurrentRetailRenderBoundsStorage.BoxLocalBounds(box.Center,box.Extents),descriptor.ConstructorBounds);
        }
    }

    [Theory]
    [InlineData(130,false)] [InlineData(130,true)] [InlineData(230,false)] [InlineData(230,true)]
    public void DirectMeshAttachmentsKeepConcreteBoundsAndCost(int baseId,bool forceChild)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var parent=EffectAggregateAnimationFixture.HmodelStreak(assets,baseId,true,modelSibling:true,
            forceChild:forceChild,modelPrototype:"SPH13.SPH134");
        var child=Assert.Single(parent.ModelAttachments).Asset;
        Assert.Empty(child.Resource!.HierarchicalLods);Assert.Empty(child.Resource.HierarchyModels);
        var mesh=Assert.Single(child.Resource.Meshes);
        Assert.False(child.UsesDetailBudget);Assert.Null(child.Selection);
        var bounds=Assert.Single(parent.Resource!.CompositeRenderChildren,c=>c.RetailClassId==0);
        Assert.Equal(new Vector4(mesh.BoundingSphereCenter,mesh.BoundingRadius),bounds.ConstructorBounds.Sphere);
        Assert.Equal(mesh.BoundingBoxMin,bounds.MeshMinimum);Assert.Equal(mesh.BoundingBoxMax,bounds.MeshMaximum);
        Assert.True(EnbW3dHlodBudgetCompiler.TryResolveNamedRenderObjectCost(child.Resource,"SPH13.SPH134",out int cost,out var error),error);
        Assert.Equal(cost,bounds.ConstructorIntegerCost);
    }

    [Theory]
    [InlineData(130,false,"both")] [InlineData(130,true,"both")]
    [InlineData(230,false,"both")] [InlineData(230,true,"both")]
    [InlineData(47,false,"both")] [InlineData(47,true,"both")]
    [InlineData(47,false,"ring")] [InlineData(47,true,"ring")]
    public void IndependentModelsCoexistWithRingSphereAndParticleSiblings(int baseId,bool forceChild,string kind)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var parent=EffectAggregateAnimationFixture.HmodelStreak(assets,baseId,true,continuousOnly:true,
            modelSibling:true,primitiveSibling:kind,forceChild:forceChild,modelPrototype:"missile");
        Assert.Single(parent.ModelAttachments);Assert.Single(parent.Rings!.Rings);
        if(kind=="both")Assert.Single(parent.Spheres!.Spheres);else Assert.Null(parent.Spheres);
        Assert.Single(parent.Mixed!.Sprites);Assert.Empty(parent.Mixed.Streaks);
        Assert.Same(parent.Resource,parent.Rings.Resource);
        if(parent.Spheres is {} spheres)Assert.Same(parent.Resource,spheres.Resource);
        Assert.Same(parent.Resource,parent.Mixed.Resource);Assert.Equal(forceChild,parent.ForceSubObjectLod);
        Assert.NotNull(parent.Sequence);
        Assert.Single(parent.Resource!.CompositeRenderChildren,c=>c.HlodResource==parent.ModelAttachments.Single().Asset.Resource);
        if(baseId==47)
        {
            Assert.True(parent.UsesDetailBudget);
            if(kind=="both")Assert.Contains(parent.Resource.CompositeRenderChildren,c=>c.RetailClassId==0x1d);
            Assert.Contains(parent.Resource.CompositeRenderChildren,c=>c.RetailClassId==0x1e);
        }
    }

    [Theory]
    [InlineData(130,"missile")] [InlineData(230,"missile")]
    [InlineData(130,"EXPLSK01")] [InlineData(230,"EXPLSK01")]
    public void RigidHmodelChildrenKeepTheirOwnHierarchyAndSingleLevel(int baseId,string prototype)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var original=EnbCurrentRetailRObjEffectAsset.LoadPrototype(assets,prototype,Vector3.Zero);
        var parent=EffectAggregateAnimationFixture.HmodelStreak(assets,baseId,true,modelSibling:true,modelPrototype:prototype);
        var child=Assert.Single(parent.ModelAttachments).Asset;
        Assert.False(child.UsesDetailBudget);Assert.Null(child.Animation);Assert.Null(child.Sequence);
        var converted=Assert.Single(child.Resource!.HierarchicalLods);Assert.True(converted.IsConvertedHierarchyModel);
        var originalTree=original.Resource!.Hierarchies.Single();var childTree=child.Resource.Hierarchies.Single();
        Assert.Equal(originalTree.Name,childTree.Name);
        Assert.Equal(originalTree.Pivots.Select(p=>(p.Name,p.ParentIndex,p.LocalTransform)),childTree.Pivots.Select(p=>(p.Name,p.ParentIndex,p.LocalTransform)));
        Assert.Equal(original.Resource.HierarchyModels.Single().Nodes.Select(n=>((uint)n.PivotIndex,n.RenderObjectName)),
            Assert.Single(converted.Levels).Members.Select(m=>(m.ReferenceIndex,m.Name)));
        Assert.Equal(original.Resource.Meshes.Count,child.Resource.Meshes.Count);
        Assert.Single(parent.Resource!.CompositeRenderChildren);
        Assert.True(EnbW3dHlodBudgetCompiler.TryComputeW3dLevelRenderCosts(child.Resource,converted,_=>true,out var costs,out var error),error);
        Assert.Equal(prototype=="missile",costs.Single()>0);
    }

    [Theory]
    [InlineData(130,false)] [InlineData(130,true)] [InlineData(230,false)] [InlineData(230,true)]
    public void HmodelParentRetainsIndependentModelAndEmitterChildren(int baseId,bool next)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var parent=EffectAggregateAnimationFixture.HmodelStreak(assets,baseId,next,modelSibling:true);
        var child=Assert.Single(parent.ModelAttachments);
        Assert.Equal("RVC02B",child.Name);
        var converted=Assert.Single(parent.Resource!.HierarchicalLods);
        Assert.True(converted.IsConvertedHierarchyModel);Assert.Single(converted.Levels);
        Assert.Empty(parent.Resource.HierarchyModels);Assert.False(parent.UsesDetailBudget);
        Assert.True(child.Asset.UsesDetailBudget);Assert.True(parent.ForceSubObjectLod);
        Assert.Same(parent.Resource,parent.Mixed!.Resource);
        Assert.Single(parent.Mixed.Streaks);
        var observations=parent.Sequence?.CreateObservations();observations?.Record(0);
        var sample=observations?.Record(1000);
        if(next)Assert.Equal(2,sample!.Value.StateId);
        Assert.True(EnbW3dHierarchyPose.TryEvaluate(parent.Resource.Hierarchies.Single(),Matrix4x4.Identity,
            sample?.Animation??parent.Animation,sample?.Frame??1,null,out var pose,out var error),error);
        Assert.Equal(pose.WorldTransforms[child.PivotIndex],child.Pose(parent,Matrix4x4.Identity,0,1000,observed:sample).World);
        Assert.True(EnbW3dHierarchyPose.TryEvaluate(parent.Resource.Hierarchies.Single(),Matrix4x4.Identity,out var initial,out error),error);
        Assert.True(Vector3.Distance(initial[child.PivotIndex].Translation,pose.WorldTransforms[child.PivotIndex].Translation)>1);
        Assert.Contains(parent.Resource.CompositeRenderChildren,c=>c.HlodResource==child.Asset.Resource);
    }

    [Theory]
    [InlineData(false,false)] [InlineData(true,false)] [InlineData(false,true)] [InlineData(true,true)]
    public void MeshParentRetainsModelAndStreakSibling(bool forceChild,bool emptyParent)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        string streak=EnbCurrentRetailRObjEffectAsset.Load(assets,1856).Mixed!.Streaks.Single().Definition.Name;
        string parentName=emptyParent?assets.BaseAssetResolver!.Resolve(2038).Rsl!.ResourceGraph!.States.Single(s=>s.Definition.Type==EnbRslStateType.Object).Definition.Object!:"tss01";
        Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(assets.RetailMixResources,parentName,out var source,out var why),why);
        var aggregate=source.CurrentRetailAggregate!;
        var definition=aggregate.RenderDefinition with {Flags=forceChild?aggregate.RenderDefinition.Flags|1u:aggregate.RenderDefinition.Flags&~1u,
            Attachments=[new("RVC02B","ROOTTRANSFORM"),new(streak,"ROOTTRANSFORM")]};
        var asset=EnbCurrentRetailRObjEffectAsset.LoadPrototype(assets,source with {CurrentRetailAggregate=new(definition,
            aggregate.BaseLogicalFileName,aggregate.BaseRequestedObjectName,aggregate.BaseResourceLocation)},Vector3.Zero);
        Assert.Equal(forceChild,asset.ForceSubObjectLod);Assert.Single(asset.ModelAttachments);
        Assert.Null(asset.AggregateParticles);Assert.Single(asset.Mixed!.Streaks);
        Assert.Same(asset.Resource,asset.Mixed.Resource);
        Assert.Equal(2,asset.Resource!.HierarchicalLods.Single().Aggregate!.Members.Count);
        var history=new EnbCurrentRetailParticlePoseHistory();var runtime=new EnbCurrentRetailMixedParticleRuntime(asset.Mixed,1,3,randomStreams:new());
        history.Record(0,Matrix4x4.Identity);runtime.Advance(asset.Mixed,history,1);
        history.Record(500,Matrix4x4.CreateTranslation(10,20,30));runtime.Advance(asset.Mixed,history,1);
        Assert.True(Assert.Single(runtime.Buffers).EmittedParticleCount>0);
    }

    [Theory]
    [InlineData(47)] [InlineData(1038)]
    public void ModelAggregateRetainsForcedChildLevelFlag(int baseId)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var resolved=assets.BaseAssetResolver!.Resolve(baseId);
        var source=resolved.W3d;
        if(source is null)
        {
            string name=resolved.Rsl!.ResourceGraph!.States.Single(s=>s.Definition.Type==EnbRslStateType.Object).Definition.Object!;
            Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(assets.RetailMixResources,name,out source,out var why),why);
        }
        var aggregate=source!.CurrentRetailAggregate!;
        var definition=aggregate.RenderDefinition with {Flags=aggregate.RenderDefinition.Flags|1u};
        var parent=EnbCurrentRetailRObjEffectAsset.LoadPrototype(assets,source with {CurrentRetailAggregate=new(definition,
            aggregate.BaseLogicalFileName,aggregate.BaseRequestedObjectName,aggregate.BaseResourceLocation)},Vector3.Zero);
        Assert.NotEmpty(parent.ModelAttachments);
        Assert.NotNull(parent.Resource);
        Assert.True(parent.ForceSubObjectLod);
    }

    [Theory]
    [InlineData(2038)] [InlineData(2039)]
    public void EmptyModelParticleParentRetainsObservedNextForBothConsumers(int baseId)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var parent=EffectAggregateAnimationFixture.Load(assets,next:true,baseId:baseId);
        var observations=parent.Sequence!.CreateObservations();var poses=new EnbCurrentRetailParticlePoseHistory();
        var buffers=parent.CompositeEmitters.Select(p=>new EnbRetailParticleSceneLifetime(p.Asset.Definition,0)).ToArray();
        uint previous=0;
        foreach(uint age in new uint[]{0,500,1000})
        {
            var sample=observations.Record(age);poses.Record(age,Matrix4x4.CreateTranslation(10,20,30));
            Assert.True(EnbW3dHierarchyPose.TryEvaluate(parent.Resource!.Hierarchies.Single(),Matrix4x4.CreateTranslation(10,20,30),
                sample.BoundAnimation,sample.Frame,null,out var evaluated,out var why),why);
            for(int i=0;i<buffers.Length;i++)
            {
                int pivot=parent.CompositeEmitters[i].Asset.PivotIndex;
                var world=evaluated.WorldTransforms[pivot];
                buffers[i].Prepare(age,world,1,true,evaluated.Visibility[pivot]);
                buffers[i].Callback(age,age-previous,world,1);
            }
            if(age>=500)Assert.Equal(2,sample.StateId);
            for(int i=0;i<parent.ModelAttachments.Count;i++)
                Assert.Equal(parent.ModelAttachments[i].Pose(parent,Matrix4x4.CreateTranslation(10,20,30),0,age,observed:sample).World,buffers[i].Emitter.CurrentWorld);
            observations.RetainLatestSample();
            previous=age;
        }
        Assert.All(buffers,e=>Assert.True(e.Emitter.EmittedCount>0));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void MeshParentRetainsModelAndParticleSiblings(bool forceChild)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(assets.RetailMixResources,"tss01",out var source,out var why),why);
        var aggregate=source.CurrentRetailAggregate!;
        var definition=aggregate.RenderDefinition with {Flags=forceChild ? aggregate.RenderDefinition.Flags|1u : aggregate.RenderDefinition.Flags&~1u,
            Attachments=[new("RVC02B","ROOTTRANSFORM"),new("ol01p","ROOTTRANSFORM")]};
        var parent=EnbCurrentRetailRObjEffectAsset.LoadPrototype(assets,source with {CurrentRetailAggregate=new(definition,
            aggregate.BaseLogicalFileName,aggregate.BaseRequestedObjectName,aggregate.BaseResourceLocation)},Vector3.Zero);
        Assert.NotEmpty(parent.Resource!.Meshes);Assert.Equal(6,parent.Resource.HierarchicalLods.Single().Levels.Count);
        Assert.Equal("RVC02B",Assert.Single(parent.ModelAttachments).Name);
        var particle=Assert.Single(parent.CompositeEmitters).Asset;
        Assert.Equal("ol01p",particle.Definition.Name,StringComparer.OrdinalIgnoreCase);
        Assert.Null(parent.Mixed);
        Assert.Equal(forceChild,parent.ForceSubObjectLod);
        var buffer=new EnbRetailParticleSceneLifetime(particle.Definition,0);
        buffer.Prepare(0,Matrix4x4.Identity,1,true,true);buffer.Callback(0,0,Matrix4x4.Identity,1);
        var world=Matrix4x4.CreateTranslation(10,20,30);
        buffer.Prepare(500,world,1,true,true);buffer.Callback(500,500,world,1);
        var emitter=buffer.Emitter;Assert.True(emitter.EmittedCount>0);
        Assert.Equal(parent.ModelAttachments[0].Pose(parent,Matrix4x4.CreateTranslation(10,20,30),0,500).World,emitter.CurrentWorld);
        Assert.Equal(2,parent.Resource.HierarchicalLods.Single().Aggregate!.Members.Count);
    }

    [Theory]
    [InlineData(2037,1,false)] [InlineData(2038,2,false)] [InlineData(2039,3,false)]
    [InlineData(2037,1,true)] [InlineData(2038,2,true)] [InlineData(2039,3,true)]
    public void StaticPvpPrototypeRetainsIndependentModelsAndParticles(int baseId,int count,bool forceChild)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var resolved=assets.BaseAssetResolver!.Resolve(baseId);
        string name=resolved.Rsl!.ResourceGraph!.States.Single(s=>s.Definition.Type==EnbRslStateType.Object).Definition.Object!;
        Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(assets.RetailMixResources,name,out var source,out var why),why);
        var aggregate=source.CurrentRetailAggregate!;
        var definition=aggregate.RenderDefinition with {Flags=forceChild ? aggregate.RenderDefinition.Flags|1u : aggregate.RenderDefinition.Flags&~1u};
        var parent=EnbCurrentRetailRObjEffectAsset.LoadPrototype(assets,source with {CurrentRetailAggregate=new(definition,
            aggregate.BaseLogicalFileName,aggregate.BaseRequestedObjectName,aggregate.BaseResourceLocation)},Vector3.Zero);
        Assert.Equal(forceChild,parent.ForceSubObjectLod);
        Assert.Null(parent.Animation);Assert.Null(parent.Sequence);Assert.Empty(parent.Resource!.Meshes);
        Assert.Equal(count,parent.ModelAttachments.Count);Assert.Equal(count,parent.CompositeEmitters.Count);
        Assert.True(EnbW3dHierarchyPose.TryEvaluate(parent.Resource.Hierarchies.Single(),Matrix4x4.CreateTranslation(10,20,30),
            null,0,null,out var pose,out var poseError),poseError);
        foreach(var (model,particle) in parent.ModelAttachments.Zip(parent.CompositeEmitters.Select(p=>p.Asset)))
        {
            Assert.Equal(model.PivotIndex,particle.PivotIndex);
            Assert.Equal(4,model.Asset.Resource!.HierarchicalLods.Single().Levels.Count);
            Assert.Equal(pose.WorldTransforms[model.PivotIndex],model.Pose(parent,Matrix4x4.CreateTranslation(10,20,30),0,1000).World);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void EmptyModelAssemblyRetainsObservedAnimationForAllChildren(bool next)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var parent=EffectAggregateAnimationFixture.Load(assets,next,1038);
        Assert.Empty(parent.Resource!.Meshes);Assert.Equal(9,parent.ModelAttachments.Count);
        Assert.Null(parent.Mixed);Assert.NotNull(parent.Animation);
        var observations=parent.Sequence?.CreateObservations();observations?.Record(0);
        var observed=observations?.Record(500);
        if(next){Assert.NotNull(observed);Assert.Equal(2,observed.Value.StateId);}
        Assert.True(EnbW3dHierarchyPose.TryEvaluate(parent.Resource.Hierarchies.Single(),Matrix4x4.Identity,
            observed?.Animation??parent.Animation,observed?.Frame??1,null,out var pose,out var error),error);
        foreach(var child in parent.ModelAttachments)
            Assert.Equal(pose.WorldTransforms[child.PivotIndex],child.Pose(parent,Matrix4x4.Identity,0,500,observed:observed).World);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void AnimatedStationRetainsItsMeshRootAndIndependentDock(bool next)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var parent=EffectAggregateAnimationFixture.Load(assets,next,47);
        Assert.NotNull(parent.Animation);Assert.Null(parent.Mixed);
        Assert.Equal(6,parent.Resource!.HierarchicalLods.Single().Levels.Count);
        var child=Assert.Single(parent.ModelAttachments);
        Assert.Equal("TSS01_DOCK",child.Name);Assert.Empty(child.Asset.Resource!.Meshes);
        var observations=parent.Sequence?.CreateObservations();observations?.Record(0);
        var observed=observations?.Record(500);
        if(next){Assert.NotNull(observed);Assert.Equal(2,observed.Value.StateId);}
        Assert.Equal(next?0:1,Assert.Single(parent.Sample(0,500,observed)).Frame);
        var tree=parent.Resource.Hierarchies.Single();
        Assert.True(EnbW3dHierarchyPose.TryEvaluate(tree,Matrix4x4.Identity,parent.Animation,0,null,out var initial,out var error),error);
        Assert.True(EnbW3dHierarchyPose.TryEvaluate(tree,Matrix4x4.Identity,observed?.Animation??parent.Animation,observed?.Frame??1,null,out var final,out error),error);
        Assert.InRange(Vector3.Distance(initial.WorldTransforms[1].Translation,final.WorldTransforms[1].Translation),next?39.99f:79.99f,next?40.01f:80.01f);
        Assert.Equal(Matrix4x4.Identity,child.Pose(parent,Matrix4x4.Identity,0,500,observed:observed).World);
        Assert.Equal(final.WorldTransforms[1],(child with {PivotIndex=1}).Pose(parent,Matrix4x4.Identity,0,500,observed:observed).World);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StationRetainsItsMeshRootAndChildHierarchy(bool visibleChild)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var parent=EnbCurrentRetailRObjEffectAsset.Load(assets,47);
        if(visibleChild)
        {
            Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(assets.RetailMixResources,"tss01",out var source,out var why),why);
            var aggregate=source.CurrentRetailAggregate!;
            var definition=aggregate.RenderDefinition with {Attachments=[new("RVC02B","ROOTTRANSFORM")]};
            parent=EnbCurrentRetailRObjEffectAsset.LoadPrototype(assets,source with {CurrentRetailAggregate=new(definition,
                aggregate.BaseLogicalFileName,aggregate.BaseRequestedObjectName,aggregate.BaseResourceLocation)},Vector3.Zero);
        }
        Assert.NotEmpty(parent.Resource!.Meshes);Assert.Equal(6,parent.Resource.HierarchicalLods.Single().Levels.Count);
        var dock=Assert.Single(parent.ModelAttachments);
        Assert.Equal(visibleChild?"RVC02B":"TSS01_DOCK",dock.Name);Assert.Equal(0,dock.PivotIndex);
        if(visibleChild)Assert.NotEmpty(dock.Asset.Resource!.Meshes);else Assert.Empty(dock.Asset.Resource!.Meshes);
        Assert.Single(dock.Asset.Resource.HierarchicalLods);
        Assert.Null(parent.Mixed);Assert.Null(parent.Animation);
        Assert.Single(parent.Resource.HierarchicalLods.Single().Aggregate!.Members);
        Assert.Single(parent.Resource.CompositeRenderChildren);
        Assert.True(dock.Pose(parent,Matrix4x4.Identity,0,0).Visible);
    }

    [Fact]
    public void ModelAttachmentsKeepDazzlesAtIndependentScaledParentPivots()
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var resolved=assets.BaseAssetResolver!.Resolve(1038);var source=resolved.W3d!;
        var aggregate=source.CurrentRetailAggregate!;
        var definition=aggregate.RenderDefinition with {Attachments=new[]{3,6}.Select(i=>
            aggregate.RenderDefinition.Attachments[i] with {SubobjectName="SK_AFBURN01A"}).ToArray()};
        var changed=new EnbCurrentRetailW3dAggregateResolution(definition,aggregate.BaseLogicalFileName,
            aggregate.BaseRequestedObjectName,aggregate.BaseResourceLocation);
        var parent=EnbCurrentRetailRObjEffectAsset.Load(assets,resolved with {W3d=source with {CurrentRetailAggregate=changed}});
        Assert.Equal(2,parent.ModelAttachments.Count);
        Assert.All(parent.ModelAttachments,child=>Assert.NotNull(Assert.Single(child.Asset.Resource!.ExternalRenderObjects).DazzleDefinition));
        Assert.NotEqual(parent.ModelAttachments[0].Pose(parent,Matrix4x4.Identity,0,100).World,
            parent.ModelAttachments[1].Pose(parent,Matrix4x4.Identity,0,100).World);
        foreach(var child in parent.ModelAttachments)
        {
            var unit=child.Pose(parent,Matrix4x4.Identity,0,100,1).World;
            var scaled=child.Pose(parent,Matrix4x4.Identity,0,100,4).World;
            foreach(var axis in new[]{Vector3.UnitX,Vector3.UnitY,Vector3.UnitZ})
                Assert.Equal(Vector3.TransformNormal(axis,unit),Vector3.TransformNormal(axis,scaled));
            Assert.InRange(Vector3.Distance(4*unit.Translation,scaled.Translation),0,.0001f);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(33)]
    [InlineData(65)]
    public void StaticModelOccurrencesAreNotLimitedByFixtureCounts(int count)
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var assets=EnbRetailAssetBundle.Load(paths);
        var resolved=assets.BaseAssetResolver!.Resolve(1038);
        var source=resolved.W3d!;
        var aggregate=source.CurrentRetailAggregate!;
        var entries=Enumerable.Range(0,count).Select(i=>aggregate.RenderDefinition.Attachments[i%9]).ToArray();
        var expanded=new EnbCurrentRetailW3dAggregateResolution(aggregate.RenderDefinition with {Attachments=entries},
            aggregate.BaseLogicalFileName,aggregate.BaseRequestedObjectName,aggregate.BaseResourceLocation);
        var parent=EnbCurrentRetailRObjEffectAsset.Load(assets,resolved with {W3d=source with {CurrentRetailAggregate=expanded}});
        Assert.Equal(count,parent.ModelAttachments.Count);
        Assert.Equal(entries.Select(e=>e.SubobjectName),parent.ModelAttachments.Select(c=>c.Name));
        Assert.All(parent.ModelAttachments,c=>Assert.True(c.Pose(parent,Matrix4x4.Identity,0,0).Visible));
        Assert.Null(parent.AggregateParticles);
    }

    [Theory]
    [InlineData(1156)]
    [InlineData(1193)]
    [InlineData(1979)]
    public void NineParticleAttachmentsRetainTheirExistingPath(int baseId)
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var asset=EnbCurrentRetailRObjEffectAsset.Load(EnbRetailAssetBundle.Load(paths),baseId);
        Assert.Empty(asset.ModelAttachments);
    }
    [Fact]
    public void StaticNineModelAssemblyPreservesOrderedIndependentChildren()
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var assets=EnbRetailAssetBundle.Load(paths);
        var parent=EnbCurrentRetailRObjEffectAsset.Load(assets,1038);
        Assert.Null(parent.Animation);Assert.Null(parent.AggregateParticles);
        Assert.Empty(parent.Resource!.Meshes);Assert.Equal(9,parent.ModelAttachments.Count);
        Assert.Equal(new[]{0,0,0,5,6,1,2,3,4},parent.ModelAttachments.Select(m=>m.PivotIndex));
        Assert.Equal(new[]{"RVC02B","RVC02C","RVC02D","RVC02E","RVC02F","RVC02G","RVC02E","RVC02F","RVC02G"},parent.ModelAttachments.Select(m=>m.Name));
        var shifted=EnbCurrentRetailRObjEffectAsset.Load(assets,1038,new(-60,0,0));
        foreach(var (a,b) in parent.ModelAttachments.Zip(shifted.ModelAttachments))
        {
            Assert.Equal(10,a.Asset.Resource!.HierarchicalLods.Single().Levels.Count);
            Assert.NotEqual(a.Asset.Resource.Key,b.Asset.Resource!.Key);
            Assert.Equal(a.PivotIndex,b.PivotIndex);
            var pose=a.Pose(parent,Matrix4x4.Identity,0,100);
            Assert.Equal(pose,a.Pose(parent,Matrix4x4.Identity,0,2000));Assert.True(pose.Visible);
        }
        Assert.NotEqual(parent.ModelAttachments[3].Pose(parent,Matrix4x4.Identity,0,0).World,parent.ModelAttachments[6].Pose(parent,Matrix4x4.Identity,0,0).World);
    }
    [Theory]
    [InlineData("RVC02C")]
    [InlineData("RVC02D")]
    public void StaticPrototypePreservesExplicitUnboundTexturesThroughHsv(string name)
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var assets=EnbRetailAssetBundle.Load(paths);
        var original=EnbCurrentRetailRObjEffectAsset.LoadPrototype(assets,name,Vector3.Zero).Resource!;
        var shifted=EnbCurrentRetailEffectMeshHsvRuntime.Apply(original,assets.Textures,new(-60,0,0));
        var loaded=EnbCurrentRetailRObjEffectAsset.LoadPrototype(assets,name,new(-60,0,0)).Resource!;
        static EnbW3dRenderTextureBinding[] Bindings(EnbW3dRenderResource resource) => resource.Meshes
            .SelectMany(m=>m.MaterialPasses).SelectMany(p=>p.TextureStages.SelectMany(s=>s.Textures)
                .Concat(p.TriangleBindings.SelectMany(t=>t.Textures).OfType<EnbW3dRenderTextureBinding>())).ToArray();
        var before=Bindings(original);
        Assert.Contains(before,b=>b.IsUnbound);
        foreach(var resource in new[]{shifted,loaded})
        {
            var after=Bindings(resource);
            Assert.Equal(before.Length,after.Length);
            foreach(var (a,b) in before.Zip(after))
            {
                Assert.Equal(a.SourceId,b.SourceId);
                if(a.IsUnbound) { Assert.Equal(a,b); Assert.False(b.IsMissing); }
            }
        }
        Assert.NotEqual(original.Key,loaded.Key);
        var mesh=original.Meshes.First(m=>m.MaterialPasses.Any(p=>p.TextureStages.Any(s=>s.Textures.Any(t=>t.IsUnbound))));
        var pass=mesh.MaterialPasses.First(p=>p.TextureStages.Any(s=>s.Textures.Any(t=>t.IsUnbound)));
        var stage=pass.TextureStages.First(s=>s.Textures.Any(t=>t.IsUnbound));
        //009587E0 leaves a descriptor outside the loaded table unchanged.
        var invalid=original.Copy().SetMeshes([(mesh).Copy().SetMaterialPasses([(pass).Copy().SetTextureStages([(stage).Copy().SetTextures([new(0,"",null)])])])]);
        var unchanged=EnbCurrentRetailEffectMeshHsvRuntime.Apply(invalid,assets.Textures,new(-60,0,0));
        Assert.Same(invalid.Meshes[0].MaterialPasses[0].TextureStages[0].Textures[0],
            unchanged.Meshes[0].MaterialPasses[0].TextureStages[0].Textures[0]);
    }

    [Theory]
    [InlineData(2038,2)]
    [InlineData(2039,3)]
    public void RepeatedPvpPrototypesKeepOrderedIndependentAttachments(int baseId,int count)
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var assets=EnbRetailAssetBundle.Load(paths);
        var parent=EnbCurrentRetailRObjEffectAsset.Load(assets,baseId);
        Assert.Equal(count,parent.ModelAttachments.Count);
        Assert.Equal(count,parent.CompositeEmitters.Count);
        Assert.Equal(Enumerable.Range(1,count),parent.ModelAttachments.Select(m=>m.PivotIndex));
        Assert.Equal(parent.ModelAttachments.Select(m=>m.PivotIndex),parent.CompositeEmitters.Select(p=>p.Asset.PivotIndex));
        Assert.Single(parent.ModelAttachments.Select(m=>m.Name).Distinct(StringComparer.OrdinalIgnoreCase));
        Assert.All(parent.ModelAttachments,m=>Assert.Equal(4,m.Asset.Resource!.HierarchicalLods.Single().Levels.Count));
        var shifted=EnbCurrentRetailRObjEffectAsset.Load(assets,baseId,new(-60,0,0));
        Assert.Equal(count,shifted.ModelAttachments.Count);
        for(int i=0;i<count;i++)
        {
            Assert.NotEqual(parent.ModelAttachments[i].Asset.Resource!.Key,shifted.ModelAttachments[i].Asset.Resource!.Key);
            Assert.Equal(parent.ModelAttachments[i].PivotIndex,shifted.ModelAttachments[i].PivotIndex);
        }
    }

    [Fact]
    public void AnimatedParentKeepsStaticChildLevelsAndFactoryHsvSeparate()
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var assets=EnbRetailAssetBundle.Load(paths);
        var parent=EnbCurrentRetailRObjEffectAsset.Load(assets,1544);
        var child=Assert.Single(parent.ModelAttachments);
        Assert.Empty(parent.Resource!.Meshes);
        Assert.NotNull(parent.Animation);
        Assert.True(child.Asset.UsesDetailBudget);
        Assert.Equal(10,child.Asset.Resource!.HierarchicalLods.Single().Levels.Count);
        Assert.True(EnbW3dHlodBudgetCompiler.TryComputeW3dLevelRenderCosts(child.Asset.Resource,child.Asset.Resource.HierarchicalLods.Single(),_=>true,out var costs,out _));
        Assert.Equal(new[]{0,32,50,70,88,106,124,152,172,190},costs);
        var first=child.Pose(parent,Matrix4x4.Identity,0,1000);
        var second=child.Pose(parent,Matrix4x4.Identity,0,2000);
        Assert.True(first.Visible);Assert.NotEqual(first.World,second.World);
        var hidden=parent with {Animation=parent.Animation!.Copy().SetNumFrames(2).SetChannels([]).SetBitChannels([
            new EnbW3dRenderAnimationBitChannel(0,1,(ushort)EnbW3dAnimationBitChannelKind.Visibility,
                EnbW3dAnimationBitChannelKind.Visibility,(ushort)child.PivotIndex,child.PivotIndex,0,new byte[]{0})])};
        Assert.False(child.Pose(hidden,Matrix4x4.Identity,0,0).Visible);
        var translated=child.Pose(parent,Matrix4x4.CreateTranslation(23,45,67),0,1000);
        Assert.True(Vector3.Distance(translated.World.Translation-first.World.Translation,new(23,45,67))<.001f);
        var shifted=EnbCurrentRetailRObjEffectAsset.Load(assets,1544,new(-60,0,0));
        Assert.NotEqual(child.Asset.Resource.Key,shifted.ModelAttachments.Single().Asset.Resource!.Key);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(child.Asset.Resource.HierarchicalLods),System.Text.Json.JsonSerializer.Serialize(shifted.ModelAttachments.Single().Asset.Resource!.HierarchicalLods));
        var companion=EnbCurrentRetailRObjEffectAsset.Load(assets,1545);
        Assert.Single(companion.ModelAttachments);
        Assert.Equal("ol01p",Assert.Single(companion.CompositeEmitters).Asset.Definition.Name,StringComparer.OrdinalIgnoreCase);
    }
}
