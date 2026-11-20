using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR10MixedEffectTests
{
    [Fact]
    public void DazzlePlacementConsumesTheObservedNextAnimation()
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var assets=EnbRetailAssetBundle.Load(paths);
        var asset=EnbCurrentRetailRObjEffectAsset.Load(assets,1154);
        var resource=asset.Resource!;var tree=resource.Hierarchies.Single();
        Assert.True(tree.Pivots.Count>1);
        EnbW3dRenderHierarchyAnimation Clip(string name,float first,float last)=>new(0,0x30000,name,tree.Name,2,1,false,
            [new(0,1,1,0,EnbW3dAnimationChannelKind.TranslationX,1,1,[first,last])],[]);
        var initial=Clip("INITIAL",5,5);var next=Clip("NEXT",100,200);
        resource=resource.Copy().SetAnimations([initial,next]);
        var catalog=EnbRetailRenderStateCatalog.ParseText("[0\\STATE0]\nID=1\nTYPE=anim\nANIM=INITIAL\nNEXT=2\nRS_ACTIVATE_NEXT=true\n[0\\STATE1]\nID=2\nTYPE=anim\nANIM=NEXT\n");
        Assert.True(catalog.TryGetLibrary(0,out var library));
        var graph=new EnbRetailRslResourceResolver(null).Resolve(library);
        var sequence=EnbPreparedRslAnimationSequence.Load(assets,graph,1,ref resource);
        var h=resource.HierarchicalLods.Single();
        resource=resource.Copy().SetHierarchicalLods([h with {Levels=[h.Levels[0] with {Members=[new(1,"TEST.DAZZLE")]}]}]);
        asset=asset with {Resource=resource,Animation=initial,Sequence=sequence};
        var definition=EnbDazzleDefinition.Resolve(EnbRetailIniDocument.Load(Path.Combine(paths.ClientIniDirectory,"Dazzle.ini")),"AFTERBURN");
        var draw=new EnbW3dDazzleDrawInstance(definition,Matrix4x4.Identity){HlodName=h.Name,LevelOrdinal=0,MemberOrdinal=0};
        Assert.Throws<InvalidDataException>(()=>asset.PlaceDazzle(draw,Matrix4x4.Identity,4,0,1000));
        var observed=asset.PlaceDazzle(draw,Matrix4x4.Identity,4,0,1000,new(2,next,1,900));
        var previous=asset.PlaceDazzle(draw,Matrix4x4.Identity,4,0,1000,new(1,initial,1,0));
        Assert.InRange(Vector3.Distance(observed.World.Translation,previous.World.Translation),779.99f,780.01f);
        Assert.Equal(4,observed.CurrentRetailDazzleScale);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedAssemblyBindsItsBaseHlodDazzlesWithoutLosingEmitters(bool meshless)
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var assets=EnbRetailAssetBundle.Load(paths);
        var original=EnbCurrentRetailRObjEffectAsset.Load(assets,518).Mixed!;
        Assert.True(assets.FileResources.TryRead("sk_afburn01a.w3d",out var bytes,out var location,out _));
        Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveAuthoredResource(assets.RetailMixResources,
            "sk_afburn01a.w3d","SK_AFBURN01A",bytes,location!,out var source,out var error),error);
        var dazzle=EnbCurrentRetailRObjEffectAsset.LoadPrototype(assets,source,Vector3.Zero).Resource!.ExternalRenderObjects.Single() with {DazzleDefinition=null};
        var h=original.Resource.HierarchicalLods.Single();
        var resource=original.Resource.Copy().SetHierarchicalLods([h with {Aggregate=null,Levels=h.Levels.Select(l=>l with
                {Members=l.Members.Append(new EnbW3dRenderHlodMember(0,dazzle.Name)).ToArray()}).ToArray()}]).SetExternalRenderObjects([dazzle]);
        var aggregate=assets.BaseAssetResolver!.Resolve(518).W3d!.CurrentRetailAggregate!.RenderDefinition;
        if(meshless)resource=resource.Copy().SetMeshes([]).SetHierarchicalLods(resource.HierarchicalLods.Select(l=>l with
            {Levels=l.Levels.Select(level=>level with {Members=level.Members.Where(m=>m.Name==dazzle.Name).ToArray()}).ToArray()}).ToArray());
        var mixed=EnbCurrentRetailMixedEffectAsset.Load(assets,resource,aggregate);
        Assert.NotNull(mixed.Resource.ExternalRenderObjects.Single(e=>e.Name==dazzle.Name).DazzleDefinition);
        Assert.Equal(original.Streaks.Count,mixed.Streaks.Count);
        Assert.Equal(original.Sprites.Count,mixed.Sprites.Count);
        Assert.Equal(meshless?0:original.Resource.Meshes.Count,mixed.Resource.Meshes.Count);
    }

    [Theory]
    [InlineData(1592)]
    [InlineData(2308)]
    public void AcceleratedAssembliesRetainBoundedHistoryAcrossDelayedLoading(int id)
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var mixed=EnbCurrentRetailRObjEffectAsset.Load(EnbRetailAssetBundle.Load(paths),id).Mixed!;
        Assert.Contains(mixed.Streaks,s=>s.Definition.AccelerationPerSecondSquared!=Vector3.Zero);
        var history=new EnbCurrentRetailParticlePoseHistory();var delayedHistory=new EnbCurrentRetailParticlePoseHistory();
        var immediate=new EnbCurrentRetailMixedParticleRuntime(mixed,10,123,randomStreams:new());var delayed=new EnbCurrentRetailMixedParticleRuntime(mixed,10,123,randomStreams:new());
        for(uint age=0;age<=1500;age+=20)
        {
            var pose=Matrix4x4.CreateRotationZ(age*.001f)*Matrix4x4.CreateTranslation(age*.01f,0,0);
            history.Record(age,pose);delayedHistory.Record(age,pose);immediate.Advance(mixed,history,10);
            if(age>=400)delayed.Advance(mixed,delayedHistory,10);
        }
        Assert.Single(history.Samples);Assert.Single(delayedHistory.Samples);
        for(int i=0;i<mixed.Streaks.Count;i++)
        {
            Assert.Equal(immediate.Buffers[i].EmittedParticleCount,delayed.Buffers[i].EmittedParticleCount);
            Assert.Equal(immediate.Buffers[i].Sample(1500),delayed.Buffers[i].Sample(1500));
            Assert.InRange(immediate.Buffers[i].RetainedParticleCount,0,10000);
        }
    }

    [Theory]
    [InlineData(1590)]
    [InlineData(1591)]
    public void FiniteSkillStreaksIgnoreSpriteOrientationAndDrainTheirAuthoredAllowance(int id)
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var asset=EnbCurrentRetailRObjEffectAsset.Load(EnbRetailAssetBundle.Load(paths),id);
        var mixed=Assert.IsType<EnbCurrentRetailMixedEffectAsset>(asset.Mixed);
        Assert.Contains(mixed.Streaks,s=>s.Definition.FrameMode==1 && s.Definition.Rotation.Random!=0);
        var history=new EnbCurrentRetailParticlePoseHistory();var runtime=new EnbCurrentRetailMixedParticleRuntime(mixed,10,123,randomStreams:new());
        for(uint age=0;age<=4000;age+=20)
        { history.Record(age,Matrix4x4.CreateTranslation(age*.01f,0,0));runtime.Advance(mixed,history,10); }
        for(int i=0;i<mixed.Streaks.Count;i++)
        {
            Assert.Equal(mixed.Streaks[i].Definition.MaximumEmissions,runtime.Buffers[i].EmittedParticleCount);
            Assert.False(runtime.Buffers[i].IsEmitting);Assert.Empty(runtime.Buffers[i].Sample(4000));
        }
    }

    [Theory]
    [InlineData(1154)]
    [InlineData(1712)]
    [InlineData(1716)]
    [InlineData(1597)]
    [InlineData(1984)]
    [InlineData(1986)]
    public void AnimatedMeshAssembliesShareTheAuthoredClockAndRetainParticleBirths(int id)
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var asset=EnbCurrentRetailRObjEffectAsset.Load(EnbRetailAssetBundle.Load(paths),id);
        var mixed=Assert.IsType<EnbCurrentRetailMixedEffectAsset>(asset.Mixed);
        Assert.NotEmpty(mixed.Resource.Meshes);Assert.Same(asset.Animation,mixed.Animation);Assert.NotNull(mixed.Animation);
        var history=new EnbCurrentRetailParticlePoseHistory();var delayedHistory=new EnbCurrentRetailParticlePoseHistory();
        var immediate=new EnbCurrentRetailMixedParticleRuntime(mixed,10,123,randomStreams:new());
        var delayed=new EnbCurrentRetailMixedParticleRuntime(mixed,10,123,randomStreams:new());
        for(uint age=0;age<=1500;age+=20)
        {
            var world=Matrix4x4.CreateRotationZ(age*.001f)*Matrix4x4.CreateTranslation(age*.01f,0,0);
            history.Record(age,world);delayedHistory.Record(age,world);immediate.Advance(mixed,history,10);
            if(age>=400)delayed.Advance(mixed,delayedHistory,10);
        }
        Assert.Single(history.Samples);Assert.Single(delayedHistory.Samples);
        for(int i=0;i<mixed.Streaks.Count;i++)
        { Assert.Equal(immediate.Buffers[i].EmittedParticleCount,delayed.Buffers[i].EmittedParticleCount);Assert.Equal(immediate.Buffers[i].Sample(1500),delayed.Buffers[i].Sample(1500)); }
        for(int i=0;i<mixed.Sprites.Count;i++)
        {
            var a=immediate.AnimatedSprites!.Emitters[i];var b=delayed.AnimatedSprites!.Emitters[i];
            Assert.Equal(a.EmittedCount,b.EmittedCount);Assert.Equal(a.WorldBirths,b.WorldBirths);Assert.Equal(a.CurrentWorld,b.CurrentWorld);
        }
        Assert.True(immediate.Buffers.Sum(b=>b.EmittedParticleCount)+(immediate.AnimatedSprites?.Emitters.Sum(e=>e.EmittedCount)??0)>0);
    }

    [Fact]
    public void SingleLodSparkyAssemblyDrawsItsMeshAndStopsBothFiniteStreakEmitters()
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var loaded=EnbCurrentRetailRObjEffectAsset.Load(EnbRetailAssetBundle.Load(paths),1888);
        var mixed=Assert.IsType<EnbCurrentRetailMixedEffectAsset>(loaded.Mixed);
        Assert.Single(mixed.Resource.Meshes);Assert.Single(mixed.Resource.HierarchicalLods.Single().Levels);
        Assert.False(loaded.UsesDetailBudget);Assert.Equal(0,loaded.Selection!.Value.LevelOrdinal);Assert.Equal(2,mixed.Streaks.Count);
        Assert.True(EnbW3dHlodBudgetCompiler.TryComputeW3dLevelRenderCosts(mixed.Resource,mixed.Resource.HierarchicalLods.Single(),_=>true,out var costs,out var error),error);
        Assert.Single(costs);Assert.True(costs[0]>0);
        var history=new EnbCurrentRetailParticlePoseHistory();var runtime=new EnbCurrentRetailMixedParticleRuntime(mixed,1,123,randomStreams:new());
        history.Record(0,Matrix4x4.Identity);runtime.Advance(mixed,history,1);
        history.Record(201,Matrix4x4.Identity);runtime.Advance(mixed,history,1);
        Assert.All(runtime.Buffers,b=>{Assert.Equal(10,b.EmittedParticleCount);Assert.False(b.IsEmitting);Assert.NotEmpty(b.Sample(201));});
        history.Record(2000,Matrix4x4.Identity);runtime.Advance(mixed,history,1);
        Assert.All(runtime.Buffers,b=>{Assert.Equal(10,b.EmittedParticleCount);Assert.Empty(b.Sample(2000));});
    }

    [Theory]
    [InlineData(1508)]
    [InlineData(1509)]
    public void AnimatedEnrageStreaksReplayDelayedPosesWithoutResettingBirths(int id)
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var loaded=EnbCurrentRetailRObjEffectAsset.Load(EnbRetailAssetBundle.Load(paths),id);
        var mixed=Assert.IsType<EnbCurrentRetailMixedEffectAsset>(loaded.Mixed);
        Assert.NotNull(mixed.Animation);Assert.Same(loaded.Animation,mixed.Animation);
        Assert.False(loaded.UsesDetailBudget);
        var history=new EnbCurrentRetailParticlePoseHistory();var delayedHistory=new EnbCurrentRetailParticlePoseHistory();
        var immediate=new EnbCurrentRetailMixedParticleRuntime(mixed,100,123,randomStreams:new());
        var delayed=new EnbCurrentRetailMixedParticleRuntime(mixed,100,123,randomStreams:new());
        for(uint age=0;age<=400;age+=20)
        {
            var world=Matrix4x4.CreateRotationZ(age*.001f)*Matrix4x4.CreateTranslation(age*.1f,0,0);
            history.Record(age,world);delayedHistory.Record(age,world);immediate.Advance(mixed,history,100);
            if(age>=200) delayed.Advance(mixed,delayedHistory,100);
        }
        Assert.Single(history.Samples);Assert.Single(delayedHistory.Samples);
        Assert.NotEmpty(immediate.Buffers[0].Sample(400));
        Assert.Equal(immediate.Buffers[0].Sample(400),delayed.Buffers[0].Sample(400));
        var snapshot=immediate.Buffers[0].Sample(400).ToArray();
        immediate.Advance(mixed,history,100);
        Assert.Equal(snapshot,immediate.Buffers[0].Sample(400));
        Assert.Empty(immediate.Buffers[0].Sample(3000));
    }

    [Fact]
    public void AnimatedStreakVisibilityPausesEmissionWithoutDeletingLiveParticles()
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var original=EnbCurrentRetailRObjEffectAsset.Load(EnbRetailAssetBundle.Load(paths),1508).Mixed!;
        int pivot=original.Streaks[0].PivotIndex;
        var animation=new EnbW3dRenderHierarchyAnimation(0,0x30000,"VISIBILITY",original.Resource.Hierarchies.Single().Name,4,10,false,[],
            [new(0,3,0,EnbW3dAnimationBitChannelKind.Visibility,(ushort)pivot,pivot,0,[0b1101])]);
        var mixed=original with {Animation=animation};
        var history=new EnbCurrentRetailParticlePoseHistory();var runtime=new EnbCurrentRetailMixedParticleRuntime(mixed,100,123,randomStreams:new());
        void Advance(uint age){history.Record(age,Matrix4x4.CreateTranslation(age,0,0));runtime.Advance(mixed,history,100);}
        Advance(0);Advance(60);
        var buffer=runtime.Buffers[0];long emitted=buffer.EmittedParticleCount;
        Assert.True(emitted>0);Advance(100);Advance(150);
        Assert.Equal(emitted,buffer.EmittedParticleCount);Assert.NotEmpty(buffer.Sample(150));
        Advance(200);Assert.True(buffer.EmittedParticleCount>emitted);
    }

    [Fact]
    public void InstalledEnergyAggregateKeepsMeshLevelsBonesAndTexturelessStreaks()
    {
        var paths = EnbInstalledAssetLocator.TryLocate(); if (paths is null) return;
        var assets = EnbRetailAssetBundle.Load(paths);
        var loaded = EnbCurrentRetailRObjEffectAsset.Load(assets, 1761);
        var mixed = Assert.IsType<EnbCurrentRetailMixedEffectAsset>(loaded.Mixed);
        Assert.Null(loaded.Particle); Assert.Null(loaded.AggregateParticles);
        Assert.Equal(3, mixed.Resource.Meshes.Count); Assert.Equal(2, mixed.Streaks.Count);
        var h = Assert.Single(mixed.Resource.HierarchicalLods);
        Assert.Equal(4, h.Levels.Count); Assert.Equal(2, h.Aggregate!.Members.Count);
        Assert.Equal(["CYLINDER02", "_BONE"], mixed.Streaks.Select(s => mixed.Resource.Hierarchies.Single().Pivots[s.PivotIndex].Name));
        Assert.All(mixed.Resource.ExternalRenderObjects, e => Assert.Null(e.ParticleTextureLocation));
        Assert.True(EnbW3dHlodBudgetCompiler.TryComputeW3dLevelRenderCosts(mixed.Resource, h, _ => true, out var costs, out var error), error);
        Assert.Equal(new[] { 0, 15, 20, 40 }, costs.ToArray());
        Assert.True(EnbW3dHlodCompositeBoundsCompiler.TryCompute(mixed.Resource, h, Matrix4x4.Identity, null, 0, out var bounds, out error), error);
        //0098F1D0 skips later all-zero extents. These emitters follow the
        //ordinary mesh members, so their bone positions do not grow the box.
        Assert.True(EnbW3dHlodCompositeBoundsCompiler.TryCompute(mixed.Resource,h with {Aggregate=null},
            Matrix4x4.Identity,null,0,out var meshBounds,out error),error);
        Assert.Equal(meshBounds.LocalBox,bounds.LocalBox);
        Assert.Equal(2e-6f, mixed.EmitterFixedCost);
    }

    [Fact]
    public void EffectUsesSharedAllocatorAndDoesNotForceTheHighestMeshLevel()
    {
        var paths = EnbInstalledAssetLocator.TryLocate(); if (paths is null) return;
        var assets = EnbRetailAssetBundle.Load(paths);
        var mixed = EnbCurrentRetailRObjEffectAsset.Load(assets, 1761).Mixed!;
        Assert.True(EnbResolvedRenderCamera.TryCreate(new(-250, 0, 0), Quaternion.Identity, new(800, 600, 1), Math.PI / 3, new(1, 10000), out var camera));
        var candidate = mixed.Candidate(Matrix4x4.CreateScale(100), 100, camera, 0);
        var ample = EnbRenderDetailBudgetAllocator.Allocate(1000, [candidate], mixed.EmitterFixedCost);
        var constrained = EnbRenderDetailBudgetAllocator.Allocate(1, [candidate], mixed.EmitterFixedCost);
        Assert.Equal(3, Assert.Single(ample.HlodSelections).LevelOrdinal);
        Assert.Equal(0, Assert.Single(constrained.HlodSelections).LevelOrdinal);
    }

    [Fact]
    public void DelayedAssetLoadReplaysRetainedPosesAndMatchesImmediateLoading()
    {
        var paths = EnbInstalledAssetLocator.TryLocate(); if (paths is null) return;
        var assets = EnbRetailAssetBundle.Load(paths);
        var mixed = EnbCurrentRetailRObjEffectAsset.Load(assets, 1761).Mixed!;
        var poses = new EnbCurrentRetailParticlePoseHistory();
        var delayedPoses = new EnbCurrentRetailParticlePoseHistory();
        var immediate = new EnbCurrentRetailMixedParticleRuntime(mixed, 100, 123,randomStreams:new());
        var delayed = new EnbCurrentRetailMixedParticleRuntime(mixed, 100, 123,randomStreams:new());
        for (uint t = 0; t <= 320; t += 16)
        {
            poses.Record(t, Matrix4x4.CreateRotationZ(t / 1000f) * Matrix4x4.CreateTranslation(0, t / 10f, 0));
            delayedPoses.Record(t, Matrix4x4.CreateRotationZ(t / 1000f) * Matrix4x4.CreateTranslation(0, t / 10f, 0));
            immediate.Advance(mixed, poses, 100);
            if (t >= 160) delayed.Advance(mixed, delayedPoses, 100);
        }
        Assert.Equal(320u, delayed.LastAge);
        for (int i = 0; i < 2; i++)
        {
            Assert.Equal(immediate.Buffers[i].EmittedParticleCount, delayed.Buffers[i].EmittedParticleCount);
            Assert.Equal(immediate.Buffers[i].Sample(320), delayed.Buffers[i].Sample(320));
            delayed.Advance(mixed, delayedPoses, 100); // redraw consumes no duplicate poses/bursts
            Assert.Equal(immediate.Buffers[i].Sample(320), delayed.Buffers[i].Sample(320));
            Assert.Empty(delayed.Buffers[i].Sample(600));
        }
    }

    [Fact]
    public void PersistentMixedEffectsConsumePosesWithoutExhaustingStartupHistory()
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var mixed=EnbCurrentRetailRObjEffectAsset.Load(EnbRetailAssetBundle.Load(paths),1761).Mixed!;
        var poses=new EnbCurrentRetailParticlePoseHistory();
        var runtime=new EnbCurrentRetailMixedParticleRuntime(mixed,100,123,randomStreams:new());
        for(uint t=0;t<=500000;t+=100)
        {
            poses.Record(t,Matrix4x4.CreateTranslation(t/100f,0,0));runtime.Advance(mixed,poses,100);
            Assert.Single(poses.Samples);
            Assert.All(runtime.Buffers,b=>Assert.InRange(b.RetainedParticleCount,0,1000));
        }
        Assert.All(runtime.Buffers,b=>Assert.NotEmpty(b.Sample(500000)));
    }
}
