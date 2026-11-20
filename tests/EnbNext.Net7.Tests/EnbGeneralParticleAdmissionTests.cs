using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

[Collection(LoadSensitiveCollection.Name)]
public sealed class EnbGeneralParticleAdmissionTests
{
    private static EnbCurrentRetailParticleScalarProperty Scalar(float v)=>new(0,[new(0,v)]);
    private static EnbCurrentRetailParticleEmitterDefinition Definition => new(0,"general","",2,10,21,
        new(2,0,0),Vector3.Zero,0,7,9,21,new(0,Vector3.Zero),new(0,Vector3.Zero),0,1,
        new(3,0,0,1,0,0,0,1,0,0,0,0,0,0,0,0),1,4,
        new(Vector3.Zero,[new(0,255,128,64,0)]),Scalar(.8f),Scalar(.2f),Scalar(0),0,Scalar(255))
        {BlurTime=Scalar(.1f),LinePropertyWords=[0x0100000e,0,0,0,0x3f800000,0,0,1,2,3,4,5,6,7,8,9]};

    [Theory]
    [InlineData(0,false)] [InlineData(1,false)] [InlineData(2,false)] [InlineData(3,false)] [InlineData(4,false)]
    [InlineData(0,true)] [InlineData(1,true)] [InlineData(2,true)] [InlineData(3,true)] [InlineData(4,true)]
    public void EveryFamilyUsesRetainedBirthsAndDrains(int mode,bool continuous)
    {
        var d=Definition.Copy().SetRenderMode((uint)mode).SetMaximumEmissions(continuous?0:21);
        EnbCurrentRetailRObjParticleAsset.ValidateAnimated(d);
        EnbCurrentRetailRObjEffectAsset.ValidateParticleJet(d);
        if(continuous)EnbCurrentRetailShipParticleRuntime.Validate(d);else EnbCurrentRetailRObjParticleAsset.ValidateFinite(d);
        var state=EnbCurrentRetailAnimatedParticleRuntime.CreateEmitter(d,randomStreams:new());
        EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(state,d,0,0,Matrix4x4.Identity,true,1,true);
        EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(state,d,101,101,Matrix4x4.Identity,true,1);
        Assert.Equal(21,state.EmittedCount);
        var packet=Draw(d,state.WorldBirths,state.MotionHistory,101);
        Assert.NotEmpty(packet.Mesh.Indices);
        Assert.Equal(packet.DeformedGeometry!.Positions.ToArray(),Draw(d,state.WorldBirths,state.MotionHistory,101).DeformedGeometry!.Positions.ToArray());
        if(mode==1)Assert.Contains(new Vector2(15/16f,15/16f),packet.Material.Source.TextureStages.SelectMany(s=>s.EffectiveRuntimeTexCoords??[]));
        if(mode==2)Assert.False(packet.Mesh.CurrentRetailGeometrySortRequestOverride);
        Assert.Equal(10,packet.OrderKey.StaticSort.RuntimeLevel); //outer buffer queue survives line flag4
        EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(state,d,2200,2099,Matrix4x4.Identity,false,1);
        Assert.Empty(state.WorldBirths);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void AlphaTestClassOneBuffersRemainImmediate(int mode)
    {
        var d=Definition.Copy().SetRenderMode((uint)mode).SetShader(Definition.Shader with {AlphaTest=1,DestinationBlend=0});
        var state=EnbCurrentRetailAnimatedParticleRuntime.CreateEmitter(d,randomStreams:new());
        EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(state,d,0,0,Matrix4x4.Identity,true,1,true);
        EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(state,d,101,101,Matrix4x4.Identity,true,1);
        var packet=Draw(d,state.WorldBirths,state.MotionHistory,101);
        Assert.Equal(EnbW3dStaticSortDisposition.Immediate,packet.OrderKey.StaticSort.Disposition);
        if(mode>=3)
        {
            var reference=new EnbCurrentRetailStreakBufferRuntime(d,1,0,randomStreams:new());
            reference.Advance(0,Matrix4x4.Identity);reference.Advance(101,Matrix4x4.Identity);
            Assert.True(EnbCurrentRetailStreakRuntime.TryBuildPacket(default,1,1,1,"general",0,d,reference.Sample(101),Camera(),101,out var streak,out var error),error);
            Assert.Equal(EnbW3dStaticSortDisposition.Immediate,streak!.OrderKey.StaticSort.Disposition);
        }
    }

    [Theory]
    [InlineData(3,false)] [InlineData(4,false)] [InlineData(3,true)] [InlineData(4,true)]
    public void GeneralStreakMatchesDedicatedBufferIncludingAcceleration(int mode,bool local)
    {
        var d=Definition.Copy().SetRenderMode((uint)mode).SetMaximumEmissions(1).SetBurstSize(1).SetLocalSpace(local).SetAccelerationPerSecondSquared(new(3,-2,1)).SetOpacity(new(.12f,[new(0,.8f)])).SetSize(new(.03f,[new(0,.2f)])).SetBlurTime(new(.03f,[new(0,.1f)])).SetFrame(new(.5f,[new(0,.25f)])).SetColor(Definition.Color with {Random=new(.1f,.2f,.3f)});
        var state=EnbCurrentRetailAnimatedParticleRuntime.CreateEmitter(d,randomStreams:new());
        var reference=new EnbCurrentRetailStreakBufferRuntime(d,1,0,randomStreams:new());
        var pose=Matrix4x4.CreateTranslation(1,2,3);
        reference.Advance(0,pose);reference.Advance(101,pose);
        EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(state,d,0,0,pose,true,1,true);
        EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(state,d,101,101,pose,true,1);
        foreach(uint clock in new uint[]{101,150,230})
        {
            var actual=Draw(d,state.WorldBirths,state.MotionHistory,clock,pose);
            Assert.True(EnbCurrentRetailStreakRuntime.TryBuildPacket(default,1,1,1,"general",0,d,reference.Sample(clock),Camera(),clock,out var expected,out var error),error);
            Assert.Equal(expected!.DeformedGeometry!.Positions.ToArray(),actual.DeformedGeometry!.Positions.ToArray());
            Assert.Equal(expected.Mesh.EffectiveColor1,actual.Mesh.EffectiveColor1);
        }
    }

    [Fact]
    public void NativeCapacityReplacesConsumerThresholdsAndContainsExcessiveAllocations()
    {
        foreach(var d in new[]{Definition.Copy().SetMaximumEmissions(4097).SetBurstSize(513).SetLifetimeSeconds(61),
            Definition.Copy().SetMaximumEmissions(0).SetBurstSize(10001).SetEmissionRatePerSecond(.05f).SetLifetimeSeconds(61),
            Definition.Copy().SetMaximumEmissions(1).SetEmissionRatePerSecond(2000),
            Definition.Copy().SetMaximumEmissions(1).SetEmissionRatePerSecond(0)})
            EnbRetailParticleAdmission.Validate(d);
        Assert.Throws<InvalidDataException>(()=>EnbRetailParticleAdmission.Validate(Definition.Copy().SetMaximumEmissions(0).SetEmissionRatePerSecond(2000)));
        Assert.Contains("resource budget",Assert.Throws<InvalidDataException>(()=>EnbRetailParticleAdmission.Validate(
            Definition.Copy().SetMaximumEmissions(0).SetBurstSize(1000001))).Message);
    }


    [Fact]
    public void BoundOwnerRetainsNativeCapacityBeyondFormerHistoryLimit()
    {
        var d=Definition.Copy().SetMaximumEmissions(0).SetBurstSize(10001).SetEmissionRatePerSecond(.05f).SetLifetimeSeconds(61);
        EnbRetailParticleAdmission.Validate(d);
        var owner=EnbCurrentRetailAnimatedParticleRuntime.CreateEmitter(d,randomStreams:new());
        EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(owner,d,0,0,Matrix4x4.Identity,true,1,true);
        for(uint tick=1;tick<=3;tick++)
            EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(owner,d,tick*20001,20001,Matrix4x4.Identity,true,1);
        Assert.Equal(30003,owner.EmittedCount);
        Assert.Equal(30003,owner.WorldBirths.Count);
        EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(owner,d,122000,61997,Matrix4x4.Identity,false,1);
        Assert.Empty(owner.WorldBirths);Assert.Empty(owner.Births);Assert.Empty(owner.Worlds);
    }

    [Theory] [InlineData(0f)] [InlineData(-1f)]
    public void NonpositiveRateUsesNativeOneSecondIntervalAndMinimumBuffer(float rate)
    {
        var d=Definition.Copy().SetRenderMode(3).SetEmissionRatePerSecond(rate);
        var owner=new EnbCurrentRetailStreakBufferRuntime(d,1,0,randomStreams:new());
        owner.Advance(0,Matrix4x4.Identity);owner.Advance(1000,Matrix4x4.Identity);
        Assert.Equal(0,owner.EmittedParticleCount);
        owner.Advance(1001,Matrix4x4.Identity);
        Assert.Equal(21,owner.EmittedParticleCount);Assert.Equal(2,owner.Sample(1001).Count);
    }

    [Fact]
    public void ExcessiveFiniteCallbackIsContainedBeforeConsumingBirthRandomness()
    {
        var d=Definition.Copy().SetEmissionRatePerSecond(2000).SetMaximumEmissions(1000001).SetBurstSize(1).SetLifetimeSeconds(.001f);
        var streams=new EnbRetailParticleRandomStreams();
        var owner=EnbCurrentRetailAnimatedParticleRuntime.CreateEmitter(d,randomStreams:streams);
        EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(owner,d,0,0,Matrix4x4.Identity,true,1,true);
        uint before=streams.Births.Counter;
        Assert.Contains("work budget",Assert.Throws<InvalidDataException>(()=>
            EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(owner,d,1,1,Matrix4x4.Identity,true,1)).Message);
        Assert.Equal(before,streams.Births.Counter);Assert.Equal(0,owner.EmittedCount);
    }

    [Theory] [InlineData(3)] [InlineData(4)]
    public void FiniteZeroIntervalStreakCompletesAndRetires(int mode)
    {
        var d=Definition.Copy().SetRenderMode((uint)mode).SetEmissionRatePerSecond(2000).SetMaximumEmissions(23);
        var owner=new EnbCurrentRetailStreakBufferRuntime(d,1,0,randomStreams:new());
        owner.Advance(0,Matrix4x4.Identity);owner.Advance(1,Matrix4x4.Identity);
        Assert.Equal(23,owner.EmittedParticleCount);Assert.False(owner.IsEmitting);
        Assert.NotEmpty(owner.Sample(1));Assert.Empty(owner.Sample(2001));
    }

    [Theory] [InlineData(0u,3u,8)] [InlineData(0u,4u,16)] [InlineData(1u,3u,8)]
    [InlineData(1u,4u,16)] [InlineData(1u,5u,16)] [InlineData(1u,uint.MaxValue,16)]
    public void AtlasClampAndCellMaskFollowPointGroup(uint mode,uint atlas,int side)
    {
        var d=Definition.Copy().SetRenderMode(mode).SetFrameMode(atlas);
        var p=Draw(d,[new(0,0,Matrix4x4.Identity){NativeBufferPosition=new(3,0,0)}],new(),1);
        var uv=p.Material.Source.TextureStages.Single().EffectiveRuntimeTexCoords!;
        var origin=new Vector2((255&(side-1))/(float)side,((255&(side*side-1))/side)/(float)side);
        Assert.Equal(origin+(mode==0?new Vector2(.5f/side,0):Vector2.Zero),uv[0]);
    }

    [Fact]
    public void LineMetadataUsesNativeMappingIgnoresReservedWordsAndRandomWidth()
    {
        var d=Definition.Copy().SetRenderMode(2).SetSize(new(7,[new(0,.2f)])).SetFrameMode(uint.MaxValue);
        EnbCurrentRetailParticleBirth[] births=[new(0,0,Matrix4x4.Identity){NativeBufferPosition=new(3,0,0)},
            new(0,1,Matrix4x4.Identity){NativeBufferPosition=new(3,1,0)}];
        var p=Draw(d,births,new(),1);
        Assert.All(p.Material.Source.TextureStages.Single().EffectiveRuntimeTexCoords!,uv=>Assert.Equal(0,uv.X));
        var words=d.LinePropertyWords.ToArray();Array.Clear(words,7,9);
        var control=Draw(d.Copy().SetLinePropertyWords([..words]).SetSize(Scalar(.2f)),births,new(),1);
        Assert.Equal(control.DeformedGeometry!.Positions.ToArray(),p.DeformedGeometry!.Positions.ToArray());
        words[0]=0x0200000e;words[4]=BitConverter.SingleToUInt32Bits(999);
        var tiled=Draw(d.Copy().SetLinePropertyWords([..words]),births,new(),1);
        Assert.Contains(tiled.Material.Source.TextureStages.Single().EffectiveRuntimeTexCoords!,uv=>uv.X==1);
        Assert.Contains(tiled.Material.Source.TextureStages.Single().EffectiveRuntimeTexCoords!,uv=>uv.Y==256);
    }

    [Fact]
    public void StreakTailShaderModesFollowNativeSelector()
    {
        var head=new EnbRenderColor32(20,40,60,80);var d=Definition.Copy().SetRenderMode(3);
        Assert.Equal(new EnbRenderColor32(0,0,0,0),EnbCurrentRetailStreakRuntime.TailColor(d,head));
        Assert.Equal(new EnbRenderColor32(255,255,255,255),EnbCurrentRetailStreakRuntime.TailColor(d.Copy().SetShader(d.Shader with {DestinationBlend=2}),head));
        Assert.Equal(head,EnbCurrentRetailStreakRuntime.TailColor(d.Copy().SetShader(d.Shader with {DestinationBlend=0}),head));
        Assert.Equal(head with {A=0},EnbCurrentRetailStreakRuntime.TailColor(d.Copy().SetShader(d.Shader with {DestinationBlend=0,AlphaTest=1}),head));
        Assert.Equal(head,EnbCurrentRetailStreakRuntime.TailColor(d.Copy().SetTextureFileName("smoke.tga"),head));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void SpriteTopologyOutlivesFramesWhileGeometryAndMaterialsRemainCurrent(int mode)
    {
        var d=Definition.Copy().SetRenderMode((uint)mode);
        EnbCurrentRetailParticleBirth[] births=[new(0,0,Matrix4x4.Identity),new(0,1,Matrix4x4.Identity)];
        var first=Draw(d,births,new(),101);
        var saved=first.DeformedGeometry!.Positions.ToArray();
        var second=Draw(d,births,new(),151);
        Assert.Same(first.Mesh.Indices,second.Mesh.Indices);
        Assert.NotEqual(saved,second.DeformedGeometry!.Positions.ToArray());
        var cache=new EnbW3dIndexedSubmissionCache();cache.BeginFrame();
        Assert.True(cache.TryCompile(first,out var original,out var error),error);
        Assert.True(cache.TryCompile(second,out var updated,out error),error);
        Assert.Equal(1,cache.HitCount);
        Assert.Same(second,updated.Packet);
        Assert.False(EnbW3dIndexedSubmissionCompiler.CanReuseGeometry(original,second));
        Assert.Equal(saved,first.DeformedGeometry.Positions.ToArray());
        var replacement=(second).ForkTask().SetMaterial(second.Material with {Source=(second.Material.Source).Copy().SetTriangleBindings(second.Material.Source.TriangleBindings.ToArray())});
        Assert.True(cache.TryCompile(replacement,out _,out error),error);
        Assert.Equal(2,cache.MissCount);
        var invalid=(second).ForkTask().SetMesh((second.Mesh).Copy().SetIndices(new uint[]{uint.MaxValue,0,1}));
        Assert.False(cache.TryCompile(invalid,out _,out _));
        var larger=Draw(d,[..births,new(0,2,Matrix4x4.Identity),new(0,3,Matrix4x4.Identity),new(0,4,Matrix4x4.Identity)],new(),151);
        Assert.NotSame(first.Mesh.Indices,larger.Mesh.Indices);
        Assert.Equal(mode==0?6:12,first.Mesh.Indices.Count);
        Assert.Equal(saved,first.DeformedGeometry.Positions.ToArray());
        Assert.True(cache.TryCompile(larger,out _,out error),error);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void RetainedParticleLanesRefreshEveryStreamAndRejectBindingReplacement(int mode)
    {
        var definition=Definition.Copy().SetRenderMode((uint)mode);
        var image=EnbTextureImage.CreateCurrentRetailRuntimeArgb4444(1,1,[new EnbColor32(255,255,255,255)]);
        var texture=new EnbResolvedTextureResource("fixture",EnbTextureResolutionStatus.Decoded,Image:image);
        var settings=EnbRenderTextureRuntimeSettings.RetailDefaults;
        var capabilities=EnbRenderTextureBackendCapabilities.ModernDefault;
        EnbW3dIndexedSubmissionPlan Build(int count,long clock,EnbResolvedTextureResource resolved)
        {
            var instance=new EnbW3dParticleEmitterDrawInstance("general","general",1,EnbRenderPassKind.OrdinaryWorld,
                definition,null,Matrix4x4.Identity,"",0,0) {RetainedTexture=resolved,RuntimeTextureName="fixture"};
            var births=Enumerable.Range(0,mode==2?Math.Max(2,count):count).Select(i=>new EnbCurrentRetailParticleBirth(0,(uint)i,Matrix4x4.CreateTranslation(i,0,0))).ToArray();
            Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(default,1,1,instance,Camera(),EnbW3dLightEnvironment.BlackNoLights,
                0,0,clock,out var packet,out _,out var error,worldBirths:births,activeEmissionRun:1),error);
            Assert.True(EnbW3dIndexedSubmissionCompiler.TryCompile(packet!,out var result,out error),error);
            return result;
        }
        var first=Build(2,101,texture);
        Assert.True(EnbW3dProgrammableTextureLaneCompiler.TryCompile(first,Camera(),settings,capabilities,_=>image,out var retained,out var error),error);
        var saved=retained.LightingPlan.Color1.ToArray();
        foreach(int count in new[]{2,5,3,1,4})
        {
            var current=Build(count,200+count*17,texture);
            Assert.True(EnbW3dProgrammableTextureLaneCompiler.TryUpdateParticleFrame(retained,current,Camera(),settings,capabilities,out var updated,out error),error);
            Assert.True(EnbW3dProgrammableTextureLaneCompiler.TryCompile(current,Camera(),settings,capabilities,_=>image,out var oracle,out error),error);
            Assert.Equal(oracle.LightingPlan.Normals.ToArray(),updated.LightingPlan.Normals.ToArray());
            Assert.Equal(oracle.LightingPlan.Color1.ToArray(),updated.LightingPlan.Color1.ToArray());
            Assert.Equal(oracle.LightingPlan.TextureCoordinates0.ToArray(),updated.LightingPlan.TextureCoordinates0.ToArray());
            Assert.Equal(oracle.TextureCoordinates1.ToArray(),updated.TextureCoordinates1.ToArray());
            Assert.Equal(oracle.LightingPlan.CanonicalClipFromModel,updated.LightingPlan.CanonicalClipFromModel);
            Assert.Equal(oracle.LightingPlan.NormalFromModel,updated.LightingPlan.NormalFromModel);
            Assert.Equal(oracle.LightingPlan.ModelToWorld,updated.LightingPlan.ModelToWorld);
            Assert.Same(current.Packet,updated.Submission.Packet);
            Assert.Equal(oracle.RunStates.Select(r=>r.Stage0Program),updated.RunStates.Select(r=>r.Stage0Program));
            Assert.Equal(oracle.Submission.Runs.Select(r=>(r.StartIndex,r.IndexCount,r.VertexCount)),updated.Submission.Runs.Select(r=>(r.StartIndex,r.IndexCount,r.VertexCount)));
            Assert.Equal(saved,retained.LightingPlan.Color1.ToArray());
        }
        var changedNormals=first.Packet.DeformedGeometry! with {Normals=System.Collections.Immutable.ImmutableArray.CreateRange(
            Enumerable.Repeat(new Vector3(float.NaN,0,0),first.Positions.Count))};
        var invalid=first with {Packet=(first.Packet).ForkTask().SetDeformedGeometry(changedNormals)};
        Assert.False(EnbW3dProgrammableTextureLaneCompiler.TryUpdateParticleFrame(retained,invalid,Camera(),settings,capabilities,out _,out _));
        var replaced=Build(2,401,texture with { });
        Assert.False(EnbW3dProgrammableTextureLaneCompiler.TryUpdateParticleFrame(retained,replaced,Camera(),settings,capabilities,out _,out _));
        Assert.False(EnbW3dProgrammableTextureLaneCompiler.TryUpdateParticleFrame(retained,first,Camera(),settings with {TextureReduction=settings.TextureReduction+1},capabilities,out _,out _));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void FrameStreamsPreserveQueuedDrawsAndExpireOnlyAtTheirOwnDrain(int mode)
    {
        var d=Definition.Copy().SetRenderMode((uint)mode);
        EnbCurrentRetailParticleBirth[] births=[new(0,0,Matrix4x4.Identity),new(0,1,Matrix4x4.Identity)];
        var arena=new EnbW3dTransientGeometryArena();
        var detached=Draw(d,births,new(),101);
        var detachedValues=detached.Mesh.Positions.ToArray();
        var frame=arena.BeginFrame();
        var first=Draw(d,births,new(),101,frame:frame);
        Assert.IsType<EnbW3dFrameStream<Vector3>>(first.Mesh.Positions);
        Assert.Null(first.DeformedGeometry);
        Assert.Equal(detachedValues,first.Mesh.Positions.ToArray());
        Assert.Equal(detached.Mesh.EffectiveColor1,first.Mesh.EffectiveColor1);
        var second=Draw(d,births,new(),151,frame:frame);
        Assert.Equal(detachedValues,first.Mesh.Positions.ToArray());
        Assert.NotEqual(detachedValues,second.Mesh.Positions.ToArray());
        var cache=new EnbW3dIndexedSubmissionCache();cache.BeginFrame();
        Assert.True(cache.TryCompile(first,out var submission,out var error),error);
        Assert.Equal(EnbW3dSubmissionVertexSource.CpuDeformed,submission.VertexSource);
        Assert.False(EnbW3dIndexedSubmissionCompiler.CanReuseGeometry(submission,first));
        Assert.True(EnbNext.Rendering.OpenGL.EnbOpenGlIndexedUploadPlanner.TryPlan(submission,true,out var upload,out error),error);
        Assert.True(EnbW3dIndexedSubmissionCompiler.TryCompile(detached,out var oracleSubmission,out error),error);
        Assert.True(EnbNext.Rendering.OpenGL.EnbOpenGlIndexedUploadPlanner.TryPlan(oracleSubmission,true,out var oracleUpload,out error),error);
        Assert.Equal(oracleUpload.PositionElements.ToArray(),upload.PositionElements.ToArray());
        using var nested=arena.BeginFrame();
        var nestedPacket=Draw(d,births,new(),201,frame:nested);
        var nestedValues=nestedPacket.Mesh.Positions.ToArray();
        var publishedPick=new EnbSubmittedMeshPick(submission,Matrix4x4.Identity);
        var expectedPick=new EnbSubmittedMeshPick(oracleSubmission,Matrix4x4.Identity);
        frame.Dispose();
        Assert.Throws<ObjectDisposedException>(()=>first.Mesh.Positions[0]);
        for(int x=0;x<=10;x++)for(int y=0;y<=10;y++)
            Assert.Equal(expectedPick.Contains(x/10d,y/10d),publishedPick.Contains(x/10d,y/10d));
        Assert.Equal(nestedValues,nestedPacket.Mesh.Positions.ToArray());
        long arrays=arena.ArrayAllocations;
        using var next=arena.BeginFrame();
        var reused=Draw(d,births,new(),251,frame:next);
        frame.Dispose(); // A second dispose must not invalidate the bank's new lease.
        Assert.Equal(Draw(d,births,new(),251).Mesh.Positions,reused.Mesh.Positions);
        Assert.Equal(arrays,arena.ArrayAllocations);
        // A later draw reuses the arena bank, not the geometry published to UI picking.
        for(int x=0;x<=10;x++)for(int y=0;y<=10;y++)
            Assert.Equal(expectedPick.Contains(x/10d,y/10d),publishedPick.Contains(x/10d,y/10d));
        Assert.False((publishedPick with {Submission=publishedPick.Submission with {Runs=[]}}).Contains(.5,.5));
        Assert.True(cache.TryCompile(reused,out var updated,out error),error);
        Assert.Same(reused,updated.Packet);
        Assert.True(EnbNext.Rendering.OpenGL.EnbOpenGlIndexedUploadPlanner.TryPlan(updated,true,out var reusedUpload,out error,upload),error);
        Assert.Equal(updated.Positions.SelectMany(v=>new[]{v.X,v.Y,v.Z}).ToArray(),reusedUpload.PositionElements.ToArray());
        var larger=Draw(d,[..births,new(0,2,Matrix4x4.Identity),new(0,3,Matrix4x4.Identity)],new(),301,frame:next);
        Assert.True(cache.TryCompile(larger,out var grown,out error),error);
        Assert.Equal(larger.Mesh.Positions.Count,grown.Positions.Count);
        Assert.Equal(detachedValues,detached.Mesh.Positions.ToArray());
    }

    [Theory] [InlineData(0)] [InlineData(1)]
    public void PhysicalParticleBuffersRetainIndependentCountDependentGeometry(int mode)
    {
        var definition=Definition.Copy().SetRenderMode((uint)mode).SetFrameMode(0);
        var firstOwner=new EnbCurrentRetailParticleEmitterRuntime.GeometryOwner(definition);
        var otherOwner=new EnbCurrentRetailParticleEmitterRuntime.GeometryOwner(definition);
        EnbCurrentRetailParticleBirth[] births=[new(0,0,Matrix4x4.Identity),new(0,1,Matrix4x4.Identity)];
        var first=Draw(definition,births,new(),101,geometry:firstOwner);
        var other=Draw(definition,[..births,new(0,2,Matrix4x4.Identity)],new(),101,geometry:otherOwner);
        var repeated=Draw(definition,births,new(),151,geometry:firstOwner);
        Assert.True(first.DeformedGeometry!.Normals.Equals(repeated.DeformedGeometry!.Normals));
        Assert.Same(first.Mesh.Indices,repeated.Mesh.Indices);
        Assert.Same(first.Material,repeated.Material);
        Assert.Same(first.Material.Source,repeated.Material.Source);
        Assert.Equal(Draw(definition,births,new(),151).Mesh.Positions.ToArray(),repeated.Mesh.Positions.ToArray());
        Assert.Equal(first.Material.Source.TextureStages[0].EffectiveRuntimeTexCoords,repeated.Material.Source.TextureStages[0].EffectiveRuntimeTexCoords);
        Assert.NotEqual(first.Mesh.Positions.Count,other.Mesh.Positions.Count);
        Assert.Equal(first.Mesh.Positions.Count,first.DeformedGeometry.Normals.Count);
    }

    [Fact]
    public void SpriteTopologyPublicationKeepsConcurrentRangesAndAtlasesConsistent()
    {
        var owner=new EnbCurrentRetailParticleEmitterRuntime.GeometryOwner(Definition);
        Parallel.For(0,2000,i=>
        {
            int count=1+i%17, corners=3+i%2;
            bool atlas=i%3!=0;
            var range=owner.Topology(count,corners,atlas,out var uv);
            Assert.Equal(count*corners,range.VertexCount);
            Assert.Equal(count*(corners-2)*3,range.Indices.Count);
            for(int j=0;j<count;j++)
            {
                int offset=j*(corners-2)*3;
                Assert.Equal((uint)(j*corners),range.Indices[offset]);
                Assert.Equal((uint)(j*corners+2),range.Indices[offset+2]);
                if(corners==4)Assert.Equal((uint)(j*corners),range.Indices[offset+5]);
            }
            if(atlas)
            {
                Assert.NotNull(uv);
                Assert.Equal(count*corners,uv.Length);
                Assert.Equal(corners==4?Vector2.Zero:new Vector2(.5f,0),uv[0]);
            }
            else Assert.Null(uv);
        });
        var expected=owner.Topology(12,4,true,out var expectedUv);
        long before=GC.GetAllocatedBytesForCurrentThread();
        bool same=true;
        for(int i=0;i<100;i++)
        {
            var range=owner.Topology(12,4,true,out var uv);
            same &= ReferenceEquals(expected,range) && ReferenceEquals(expectedUv,uv);
        }
        long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
        Assert.True(same);
        Assert.Equal(0,allocated);
    }

    [Fact]
    public void OwnedParticleMaterialPrefixTracksBindingReplacementWithoutCrossBufferEviction()
    {
        var definition=Definition.Copy().SetRenderMode(0).SetFrameMode(0);
        var owner=new EnbCurrentRetailParticleEmitterRuntime.GeometryOwner(definition);
        var other=new EnbCurrentRetailParticleEmitterRuntime.GeometryOwner(definition);
        EnbW3dRenderMaterialPass Resolve(EnbCurrentRetailParticleEmitterRuntime.GeometryOwner buffer,int count,string texture)
        {
            Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryBuildMaterial(definition,null,count,
                out var pass,out _,out var why,texture,geometryOwner:buffer),why);
            return pass;
        }
        var first=Resolve(owner,2,"first");
        var second=Resolve(other,7,"first");
        Assert.Same(first,Resolve(owner,2,"first"));
        Assert.Same(second,Resolve(other,7,"first"));
        var replacement=Resolve(owner,2,"second");
        Assert.NotSame(first,replacement);
        Assert.Same(replacement,Resolve(owner,2,"second"));
        Assert.Equal(2,first.TriangleBindings.Count);
        Assert.Equal(7,second.TriangleBindings.Count);
        Assert.Same(second,Resolve(other,7,"first"));
    }

    [Fact]
    public void PhysicalLineBindingsReadOwnedIndicesWithoutFramePublication()
    {
        var owner=new EnbCurrentRetailParticleEmitterRuntime.GeometryOwner(Definition);
        var other=new EnbCurrentRetailParticleEmitterRuntime.GeometryOwner(Definition);
        Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryBuildMaterial(Definition,null,2,out var source,out _,out var why),why);
        uint[] a=[0,1,2,2,3,0],b=[1,2,3,3,0,1];
        static EnbW3dRenderMaterialPass Bind(EnbCurrentRetailParticleEmitterRuntime.GeometryOwner owner,EnbW3dRenderMaterialPass source,uint[] values)
        {owner.Line.Indices.Clear();owner.Line.Indices.AddRange(values);return owner.Line.Bind(source);}
        var first=Bind(owner,source,a);var second=Bind(other,source,b);
        Assert.Equal(new[]{0,2},first.TriangleBindings.Select(x=>x.MaterialVertexIndex));
        Assert.Same(first,Bind(owner,source,b));
        Assert.Equal(new[]{1,3},first.TriangleBindings.Select(x=>x.MaterialVertexIndex));
        Assert.Same(second,Bind(other,source,b));
        Assert.Same(first,Bind(owner,source,a));
        Assert.Equal(new[]{1,3},second.TriangleBindings.Select(x=>x.MaterialVertexIndex));
        Parallel.For(0,1000,i=>
        {
            var independent=new EnbCurrentRetailParticleEmitterRuntime.GeometryOwner(Definition);
            var values=i%2==0?a:b;var result=Bind(independent,source,values);
            Assert.Equal((int)values[0],result.TriangleBindings[0].MaterialVertexIndex);
            Assert.Equal((int)values[3],result.TriangleBindings[1].MaterialVertexIndex);
        });
        Assert.Equal(new[]{0,2},first.TriangleBindings.Select(x=>x.MaterialVertexIndex));
    }

    [Fact]
    public void ConnectedLinesUseDistinctFrameRangesAndKeepDetachedControlParity()
    {
        var d=Definition.Copy().SetRenderMode(2).SetSize(new(7,[new(0,.2f)])).SetFrameMode(0);
        EnbCurrentRetailParticleBirth[] births=[new(0,0,Matrix4x4.Identity){NativeBufferPosition=new(3,0,0)},
            new(0,1,Matrix4x4.Identity){NativeBufferPosition=new(3,1,0)}];
        var expected=Draw(d,births,new(),1);
        var arena=new EnbW3dTransientGeometryArena();
        EnbW3dDrawTask first;
        using(var frame=arena.BeginFrame())
        {
            first=Draw(d,births,new(),1,frame:frame);
            Assert.Null(first.DeformedGeometry);
            Assert.IsType<EnbW3dFrameStream<Vector3>>(first.Mesh.Positions);
            Assert.Equal(expected.Mesh.Positions.ToArray(),first.Mesh.Positions.ToArray());
            Assert.Equal(expected.CurrentRetailViewPositions!.ToArray(),first.CurrentRetailViewPositions!.ToArray());
            Assert.Equal(expected.Mesh.EffectiveColor1!.ToArray(),first.Mesh.EffectiveColor1!.ToArray());
            Assert.Equal(expected.Mesh.Indices,first.Mesh.Indices);
            var saved=first.Mesh.Positions.ToArray();
            var other=Draw(d,births,new(),20,world:Matrix4x4.CreateTranslation(1,2,3),frame:frame);
            Assert.NotSame(first.Mesh.Positions,other.Mesh.Positions);
            Assert.Equal(saved,first.Mesh.Positions.ToArray());
        }
        Assert.Throws<ObjectDisposedException>(()=>first.Mesh.Positions[0]);
        using(var next=arena.BeginFrame())
        {
            var reused=Draw(d,births,new(),1,frame:next);
            Assert.Equal(expected.Mesh.Positions.ToArray(),reused.Mesh.Positions.ToArray());
        }
        Assert.Equal(expected.DeformedGeometry!.Positions.ToArray(),expected.Mesh.Positions.ToArray());
    }

    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void OwnedParticleInvocationsReuseAfterDrainAndKeepQueuedViewsDistinct(int mode)
    {
        var definition=Definition.Copy().SetRenderMode((uint)mode).SetFrameMode(0);
        var owner=new EnbCurrentRetailParticleEmitterRuntime.GeometryOwner(definition,"effect-composite/7/8/9/0/2");
        var tasks=new EnbW3dDrawTaskArena();var nestedTasks=new EnbW3dDrawTaskArena();
        var streams=new EnbW3dTransientGeometryArena();
        EnbCurrentRetailParticleBirth[] births=[new(0,0,Matrix4x4.Identity),new(0,1,Matrix4x4.Identity)];
        EnbW3dDrawTask first,second;
        using(tasks.Enter())using(var frame=streams.BeginFrame())
        {
            first=Draw(definition,births,new(),101,frame:frame,geometry:owner);
            Assert.Equal("effect-composite/7/8/9/0/2",first.RenderInstancePath);
            var values=first.Mesh.Positions.ToArray();
            using(nestedTasks.Enter())using(var inner=streams.BeginFrame())
            {
                var nested=Draw(definition,births,new(),151,frame:inner,geometry:owner);
                Assert.NotSame(first.Mesh,nested.Mesh);
                Assert.Equal(first.RenderInstancePath,nested.RenderInstancePath);
                Assert.Equal(values,first.Mesh.Positions.ToArray());
            }
            second=Draw(definition,births,new(),201,frame:frame,geometry:owner);
            Assert.NotSame(first,second);Assert.NotSame(first.Material.Source,second.Material.Source);
            Assert.Equal(values,first.Mesh.Positions.ToArray());
        }
        using(tasks.Enter())using(var frame=streams.BeginFrame())
        {
            var reused=Draw(definition,births,new(),301,frame:frame,geometry:owner);
            Assert.Equal("effect-composite/7/8/9/0/2",reused.RenderInstancePath);
            Assert.Same(first,reused);Assert.Same(first.Mesh,reused.Mesh);
            var oracle=Draw(definition,births,new(),301);
            Assert.Equal(oracle.Mesh.Positions.ToArray(),reused.Mesh.Positions.ToArray());
            Assert.Equal(oracle.Material.Source.TextureStages[0].EffectiveRuntimeTexCoords.ToArray(),reused.Material.Source.TextureStages[0].EffectiveRuntimeTexCoords.ToArray());
            Assert.Equal(301,reused.FrameTimeMilliseconds);
            Assert.Same(second,Draw(definition,births,new(),351,frame:frame,geometry:owner));
        }
    }

    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void OwnedParticleLanesMutateInPlaceAcrossRangeAndAtlasChanges(int mode)
    {
        var definition=Definition.Copy().SetRenderMode((uint)mode).SetFrameMode(2);
        var owner=new EnbCurrentRetailParticleEmitterRuntime.GeometryOwner(definition);
        var tasks=new EnbW3dDrawTaskArena();var streams=new EnbW3dTransientGeometryArena();
        var image=EnbTextureImage.CreateCurrentRetailRuntimeArgb4444(1,1,[new EnbColor32(255,255,255,255)]);
        var texture=new EnbResolvedTextureResource("fixture",EnbTextureResolutionStatus.Decoded,Image:image);
        var settings=EnbRenderTextureRuntimeSettings.RetailDefaults;
        var caps=EnbRenderTextureBackendCapabilities.ModernDefault;
        EnbW3dProgrammableTextureLanePlan? lane=null;
        var indexedCache=new EnbW3dIndexedSubmissionCache();
        IReadOnlyList<Vector2>? retainedCoordinates=null;
        foreach(int count in new[]{8,2,5,3,1,4,8})
        {
            using var scope=tasks.Enter();using var frame=streams.BeginFrame();
            var instance=new EnbW3dParticleEmitterDrawInstance("general","general",1,EnbRenderPassKind.OrdinaryWorld,
                definition,null,Matrix4x4.Identity,"",0,0){RetainedTexture=texture,RuntimeTextureName="fixture"};
            var births=Enumerable.Range(0,count).Select(i=>new EnbCurrentRetailParticleBirth(0,(uint)i,Matrix4x4.CreateTranslation(i,0,0))).ToArray();
            Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(default,1,1,instance,Camera(),EnbW3dLightEnvironment.BlackNoLights,
                0,0,101+count*17,out var task,out _,out var error,worldBirths:births,frameStreams:frame,geometryOwner:owner),error);
            if(mode==2 && count==1){Assert.Null(task);continue;}
            var coordinates=task!.Material.Source.TextureStages[0].EffectiveRuntimeTexCoords;
            if(retainedCoordinates is not null)Assert.Same(retainedCoordinates,coordinates);
            retainedCoordinates=coordinates;
            Assert.True(indexedCache.TryCompile(task!,out var submission,out error),error);
            Assert.Equal(task!.Mesh.Indices.Count,submission.Runs.Sum(run=>run.IndexCount));
            Assert.True(EnbW3dProgrammableTextureLaneCompiler.TryCompile(submission,Camera(),settings,caps,_=>image,out var oracle,out error),error);
            if(lane is null){lane=oracle;continue;}
            var oldLighting=lane.LightingPlan;var oldRun=lane.RunStates[0];
            Assert.True(EnbW3dProgrammableTextureLaneCompiler.TryUpdateParticleFrame(lane,submission,Camera(),settings,caps,out var updated,out error),error);
            Assert.Same(lane,updated);Assert.Same(oldLighting,updated.LightingPlan);Assert.Same(oldRun,updated.RunStates[0]);
            Assert.Equal(oracle.LightingPlan.Color1.ToArray(),updated.LightingPlan.Color1.ToArray());
            Assert.Equal(oracle.LightingPlan.TextureCoordinates0.ToArray(),updated.LightingPlan.TextureCoordinates0.ToArray());
            Assert.Equal(oracle.TextureCoordinates1.ToArray(),updated.TextureCoordinates1.ToArray());
            Assert.Equal(oracle.Submission.Runs.ToArray(),updated.Submission.Runs.ToArray());
            Assert.Equal(oracle.LightingPlan.CanonicalClipFromModel,updated.LightingPlan.CanonicalClipFromModel);
        }
    }

    private static EnbResolvedRenderCamera Camera()
    {EnbResolvedRenderCamera.TryCreate(Vector3.Zero,Quaternion.Identity,new(800,600,1),Math.PI/3,new(.01f,100),out var c);return c;}
    private static EnbW3dDrawTask Draw(EnbCurrentRetailParticleEmitterDefinition d,IReadOnlyList<EnbCurrentRetailParticleBirth> births,
        EnbRetailParticleMotionHistory motion,long clock,Matrix4x4? world=null,EnbW3dTransientGeometryArena.Frame? frame=null,EnbCurrentRetailParticleEmitterRuntime.GeometryOwner? geometry=null)
    {
        var instance=new EnbW3dParticleEmitterDrawInstance("general","general",1,EnbRenderPassKind.OrdinaryWorld,d,null,world??Matrix4x4.Identity,"",0,0) {RuntimeTextureName=d.RenderMode<=2?"fixture":null};
        Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(default,1,1,instance,Camera(),EnbW3dLightEnvironment.BlackNoLights,0,0,clock,
            out var p,out _,out var error,worldBirths:births,motionHistory:motion,activeEmissionRun:1,frameStreams:frame,geometryOwner:geometry),error);
        return Assert.IsType<EnbW3dDrawTask>(p);
    }
}
