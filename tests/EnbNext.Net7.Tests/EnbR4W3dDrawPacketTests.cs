using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

[Collection(LoadSensitiveCollection.Name)]
public sealed class EnbR4W3dDrawPacketTests
{
    [Fact]
    public void MutableOwnerAndOccurrenceScaleMatchDetachedCompilerWithoutMutatingOwner()
    {
        var resource=CompileFixture();var definition=SceneObject(1);
        var owner=new EnbRenderObjectState(definition);var spatial=new EnbRenderSpatialState();
        spatial.AdoptDefinition(definition.Spatial!);owner.Spatial=spatial;
        var preparation=new EnbW3dMeshOccurrencePreparation();
        for(int frame=0;frame<12;frame++)
        {
            float scale=frame%3==0?2f:frame%3==1?.5f:1f;
            var transform=definition.Spatial!.Transform with {Position=new(frame,2,3)};
            spatial.Pose.Update(EnbRenderPose.FromTransform(transform));
            var detached=definition with {Scale=scale,Spatial=definition.Spatial with {Transform=transform}};
            Assert.True(EnbW3dDrawPacketCompiler.TryBuildPrepared(owner,resource,null,frame,
                EnbRenderPassKind.OrdinaryWorld,null,EnbW3dLightEnvironment.BlackNoLights,[],null,preparation,
                out var actual,out var error,renderScale:scale),error);
            Assert.True(EnbW3dDrawPacketCompiler.TryBuild(detached,resource,frame,
                EnbRenderPassKind.OrdinaryWorld,null,EnbW3dLightEnvironment.BlackNoLights,[],out var expected,out error),error);
            Assert.Equal(expected.Packets.Count,actual.Packets.Count);
            for(int i=0;i<actual.Packets.Count;i++)
            {
                Assert.Equal(expected.Packets[i].WorldTransform,actual.Packets[i].WorldTransform);
                Assert.Equal(expected.Packets[i].Bounds,actual.Packets[i].Bounds);
                Assert.Equal(expected.Packets[i].OrderKey,actual.Packets[i].OrderKey);
            }
            Assert.Equal(definition.Scale,owner.Scale);Assert.Same(spatial,owner.Spatial);
        }
    }

    [Theory]
    [InlineData(false,false)]
    [InlineData(false,true)]
    [InlineData(true,false)]
    [InlineData(true,true)]
    public void OwnedTraversalKeepsBindingsWhilePoseVisibilityAndAnimationPublicationChange(bool skin,bool hlod)
    {
        var source=CompileFixture();var mesh=Assert.Single(source.Meshes);
        if(skin)mesh=(mesh).Copy().SetBoneIndices(Enumerable.Repeat((ushort)0,mesh.Positions.Count).ToArray());
        var model=source.HierarchyModels[0];var node=model.Nodes[0];
        model=model with {Nodes=[node with {PivotIndex=1},node with {Ordinal=1,PivotIndex=0}]};
        var lod=source.HierarchicalLods[0];var level=lod.Levels[0];var member=level.Members[0];
        lod=lod with {Levels=[level with {Members=[member with {Name=mesh.Name,ReferenceIndex=1},member with {Name=mesh.Name,ReferenceIndex=0}]}],Aggregate=null};
        var animation=new EnbW3dRenderHierarchyAnimation(0,0x200,"move","SHIP_TREE",2,30,false,
            [new EnbW3dRenderAnimationChannel(0,1,1,(ushort)EnbW3dAnimationChannelKind.TranslationX,
                EnbW3dAnimationChannelKind.TranslationX,1,1,[0f,40f])],
            [new EnbW3dRenderAnimationBitChannel(0,1,(ushort)EnbW3dAnimationBitChannelKind.Visibility,
                EnbW3dAnimationBitChannelKind.Visibility,1,1,1,[1])]);
        var resource=source.Copy().SetMeshes([mesh]).SetHierarchyModels(hlod?[]:[model]).SetHierarchicalLods([lod]).SetAnimations([animation]).SetExternalRenderObjects([]);
        var owner=SceneObject(1);var preparation=new EnbW3dMeshOccurrencePreparation();
        EnbW3dHlodSelection? selection=hlod?new(lod.Name,level.Ordinal):null;
        EnbW3dDrawInvocation? queued=null;
        for(int frame=0;frame<100;frame++)
        {
            if(frame==50)resource=resource.Copy().SetAnimations([animation.Copy().SetChannels([animation.Channels[0] with {Data=[0f,80f]}])]);
            var world=Matrix4x4.CreateRotationY(frame*.01f)*Matrix4x4.CreateTranslation(frame,2,3);
            EnbW3dWeightedAnimationSample[] samples=[new("move",frame%2,1)];
            Assert.True(EnbW3dDrawPacketCompiler.TryBuildPrepared(owner,resource,world,frame,EnbRenderPassKind.OrdinaryWorld,
                selection,EnbW3dLightEnvironment.BlackNoLights,samples,null,preparation,out var actual,out var error),error);
            Assert.True(EnbW3dDrawPacketCompiler.TryBuildAtWorldTransform(owner,resource,world,frame,EnbRenderPassKind.OrdinaryWorld,
                selection,EnbW3dLightEnvironment.BlackNoLights,samples,out var expected,out error),error);
            Assert.Equal(expected.Packets.Count,actual.Packets.Count);
            for(int i=0;i<actual.Packets.Count;i++)
            {
                var a=actual.Packets[i];var e=expected.Packets[i];
                Assert.Equal(e.OrderKey,a.OrderKey);Assert.Equal(e.WorldTransform,a.WorldTransform);
                Assert.Equal(e.Bounds,a.Bounds);Assert.Equal(e.Material,a.Material);
                Assert.Equal(e.PivotIndex,a.PivotIndex);Assert.Equal(e.RenderObjectName,a.RenderObjectName);
                Assert.Equal(e.DeformedGeometry?.Positions.ToArray(),a.DeformedGeometry?.Positions.ToArray());
                Assert.Equal(e.DeformedGeometry?.Normals.ToArray(),a.DeformedGeometry?.Normals.ToArray());
            }
            queued??=actual;
            Assert.Equal(0,queued.FrameTimeMilliseconds);
            Assert.Equal(1,preparation.TraversalPreparationCount);
        }
        Assert.Equal(100,preparation.TraversalUseCount);
        Assert.Equal(2,preparation.PreparationCount);
        Assert.Equal(2,queued!.Packets.Count);
        Assert.Equal(0,queued.Packets[0].OrderKey.BindingOrdinal);
        Assert.Equal(1,queued.Packets[1].OrderKey.BindingOrdinal);
    }


    [Fact]
    public void HierarchyOwnerMutatesPivotFieldsAndInvalidatesOnLocalChanges()
    {
        var resource=CompileFixture();var hierarchy=Assert.Single(resource.Hierarchies);
        var owner=new EnbW3dRetainedHierarchyPose();
        var overrides=new Dictionary<int,Matrix4x4>{{1,Matrix4x4.Identity}};
        EnbW3dHierarchyPoseOwner? first=null;
        for(int frame=0;frame<100;frame++)
        {
            var world=Matrix4x4.CreateRotationY(frame*.01f)*Matrix4x4.CreateTranslation(frame,2,3);
            if(frame%4==0)overrides[1]=Matrix4x4.CreateTranslation(frame,1,2);
            Assert.True(owner.Update(hierarchy,world,2,[],overrides,out var actual,out var error),error);
            Assert.True(EnbCurrentRetailShipLegoRuntime.TryEvaluateScaledRigidHierarchyPose(hierarchy,world,2,[],overrides,out var expected,out error),error);
            Assert.Equal(expected.WorldTransforms.ToArray(),actual.WorldTransforms.ToArray());
            Assert.Equal(expected.Visibility.ToArray(),actual.Visibility.ToArray());
            if(first is null)first=actual;else Assert.Same(first,actual);
            long revision=actual.MutationRevision;
            Assert.True(owner.Update(hierarchy,world,2,[],overrides,out var unchanged,out error),error);
            Assert.Same(actual,unchanged);Assert.Equal(revision,unchanged.MutationRevision);
        }
        Assert.Equal(100,first!.MutationRevision);
    }

    [Fact]
    public void EvaluatorReturnsThePivotStorageOwnerItMutates()
    {
        var hierarchy=Assert.Single(CompileFixture().Hierarchies);
        var owner=new EnbW3dHierarchyPoseOwner();
        Assert.True(EnbW3dHierarchyPose.TryEvaluate(hierarchy,Matrix4x4.Identity,null,0,null,
            out var first,out var error,storage:owner),error);
        Assert.Same(owner,first);
        var matrices=owner.WorldTransforms;var visibility=owner.Visibility;
        var saved=matrices.ToArray();
        Assert.True(EnbW3dHierarchyPose.TryEvaluate(hierarchy,Matrix4x4.CreateTranslation(3,4,5),null,0,null,
            out var second,out error,storage:owner),error);
        Assert.Same(owner,second);Assert.Same(matrices,second.WorldTransforms);Assert.Same(visibility,second.Visibility);
        Assert.NotEqual(saved[0],second.WorldTransforms[0]);
    }

    [Fact]
    public void ScaledHierarchyReusesItsOwnerAndArraysAcrossChangingFrames()
    {
        var hierarchy=Assert.Single(CompileFixture().Hierarchies);
        var preparation=new EnbW3dMeshOccurrencePreparation();
        var owner=preparation.HierarchyOwner(hierarchy);
        var inputs=Enumerable.Range(0,1000).Select(i=>new EnbW3dHierarchyState(
            hierarchy.Name,Matrix4x4.CreateTranslation(i,2,3),1+i*.001f,new Dictionary<int,Matrix4x4>())).ToArray();
        foreach(var input in inputs)Assert.True(owner.Update(preparation,Matrix4x4.Identity,[],input,out var error),error);
        var pose=owner.Pose;var matrices=pose.WorldTransforms;var visibility=pose.Visibility;
        long before=GC.GetAllocatedBytesForCurrentThread();
        foreach(var input in inputs)
            if(!owner.Update(preparation,Matrix4x4.Identity,[],input,out _))throw new InvalidOperationException();
        Assert.Equal(0,GC.GetAllocatedBytesForCurrentThread()-before);
        Assert.Same(pose,owner.Pose);Assert.Same(matrices,owner.Pose.WorldTransforms);Assert.Same(visibility,owner.Pose.Visibility);
        foreach(var input in inputs.Take(20))
        {
            Assert.True(owner.Update(preparation,Matrix4x4.Identity,[],input,out var error),error);
            Assert.True(EnbCurrentRetailShipLegoRuntime.TryEvaluateScaledRigidHierarchyPose(hierarchy,input.RigidWorldTransform,
                input.HierarchyTranslationScale,[],input.Transforms,out var rigid,out error),error);
            for(int i=0;i<matrices.Length;i++)Assert.Equal(Matrix4x4.CreateScale(input.RenderObjectScale)*rigid.WorldTransforms[i],matrices[i]);
        }
    }

    [Fact]
    public void RetainedHierarchyDoesNotPublishArraysWhenWorldChanges()
    {
        var hierarchy=Assert.Single(CompileFixture().Hierarchies);
        var owner=new EnbW3dRetainedHierarchyPose();
        for(int i=0;i<200;i++)
            Assert.True(owner.Update(hierarchy,Matrix4x4.CreateTranslation(i,2,3),null,[],null,out _,out _));
        long before=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<1000;i++)
            if(!owner.Update(hierarchy,Matrix4x4.CreateTranslation(i,2,3),null,[],null,out _,out _))
                throw new InvalidOperationException();
        Assert.Equal(0,GC.GetAllocatedBytesForCurrentThread()-before);
    }

    [Fact]
    public void UnchangedPhaseSharesPoseButChangedWordsKeepQueuedFramesIndependent()
    {
        Assert.True(EnbW3dDrawPacketCompiler.TryBuild(SceneObject(1),CompileFixture(),0,
            EnbRenderPassKind.OrdinaryWorld,null,EnbW3dLightEnvironment.BlackNoLights,
            out var list,out var error),error);
        var packet=list.Packets[0];
        var storage=typeof(EnbW3dDrawTask).GetField("_frameStorage",
            System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;
        var unchanged=(packet).ForkTask().SetCurrentRetailPltDayNightPhase(null);
        Assert.Same(storage.GetValue(packet),storage.GetValue(unchanged));
        var positive=(packet).ForkTask().SetCurrentRetailPltDayNightPhase(0f);
        var same=(positive).ForkTask().SetCurrentRetailPltDayNightPhase(0f);
        Assert.Same(storage.GetValue(positive),storage.GetValue(same));
        var negative=(positive).ForkTask().SetCurrentRetailPltDayNightPhase(BitConverter.Int32BitsToSingle(unchecked((int)0x80000000)));
        Assert.NotSame(storage.GetValue(positive),storage.GetValue(negative));
        Assert.Equal(0,BitConverter.SingleToInt32Bits(positive.CurrentRetailPltDayNightPhase!.Value));
        Assert.Equal(unchecked((int)0x80000000),BitConverter.SingleToInt32Bits(negative.CurrentRetailPltDayNightPhase!.Value));
        var moved=(unchanged).ForkTask().SetWorldTransform(Matrix4x4.CreateTranslation(7,8,9));
        Assert.NotEqual(packet.WorldTransform,moved.WorldTransform);
        Assert.Equal(packet.WorldTransform,unchanged.WorldTransform);
        Assert.Null(packet.CurrentRetailPltDayNightPhase);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(2f)]
    public void PreparedEffectFactoryTransformsMatchLiteralAndPreserveQueuedFrames(float radius)
    {
        var resource=CompileFixture();var owner=SceneObject(1);
        var preparation=new EnbW3dMeshOccurrencePreparation();
        EnbW3dDrawTask? queued=null;Matrix4x4 queuedWorld=default;
        for(int tick=0;tick<3;tick++)
        {
            var rigid=Matrix4x4.CreateTranslation(tick*7,2,3);
            var world=Matrix4x4.CreateScale(radius)*rigid;
            var special=new EnbRetailSpecialMaterialTransform(rigid,radius);
            Assert.True(EnbW3dDrawPacketCompiler.TryBuildAtWorldTransform(owner,resource,world,tick,
                EnbRenderPassKind.OrdinaryWorld,null,EnbW3dLightEnvironment.BlackNoLights,
                Array.Empty<EnbW3dWeightedAnimationSample>(),out var expected,out var error,special),error);
            Assert.True(EnbW3dDrawPacketCompiler.TryBuildPrepared(owner,resource,world,tick,
                EnbRenderPassKind.OrdinaryWorld,null,EnbW3dLightEnvironment.BlackNoLights,[],null,preparation,
                out var actual,out error,specialTransform:special),error);
            Assert.Equal<EnbW3dDrawTask>(expected.Packets,actual.Packets);
            if(tick==0){queued=actual.Packets[0];queuedWorld=queued.WorldTransform;}
            Assert.Equal(queuedWorld,queued!.WorldTransform);
        }
    }

    [Fact]
    public void ExternalMemberPublicationReplacesReadyBindingsWithoutChangingQueuedDraws()
    {
        var source=CompileFixture();var hlod=source.HierarchicalLods[0];var level=hlod.Levels[0];
        var member=level.Members[0] with {Name="SUN",ReferenceIndex=1};
        hlod=hlod with {Levels=[level with {Members=[member]}],Aggregate=null};
        var first=EnbDazzleDefinition.Load(EnbRetailIniDocument.Parse("[SUN]\nHistoryWeight=0\nLensflareName=L\n[L]\nFlareCount=0"),"SUN");
        var second=first with {UseCameraTranslation=!first.UseCameraTranslation};
        var pending=new EnbW3dExternalRenderObject("SUN",EnbW3dExternalRenderObjectKind.Dazzle,"sun.w3d","offline.mix");
        var ready=pending with {DazzleDefinition=first};
        var resource=source.Copy().SetHierarchicalLods([hlod]).SetExternalRenderObjects([pending,ready]);
        var preparation=new EnbW3dMeshOccurrencePreparation();
        EnbW3dDrawInvocation Draw(EnbW3dRenderResource input,int tick)
        {
            Assert.True(EnbW3dDrawPacketCompiler.TryBuildPrepared(SceneObject(1),input,Matrix4x4.Identity,tick,
                EnbRenderPassKind.OrdinaryWorld,new(hlod.Name,level.Ordinal),EnbW3dLightEnvironment.BlackNoLights,
                [],null,preparation,out var result,out var error),error);
            return result;
        }
        var queued=Draw(resource,0);
        Assert.Same(first,Assert.Single(queued.Dazzles).Definition);
        var completed=resource.Copy().SetExternalRenderObjects([pending with {DazzleDefinition=second},ready]);
        Assert.Same(second,Assert.Single(Draw(completed,1).Dazzles).Definition);
        Assert.Same(first,Assert.Single(Draw(resource,2).Dazzles).Definition);
        Assert.Same(first,Assert.Single(queued.Dazzles).Definition);
    }

    [Fact]
    public void MeshPreparationBorrowsOnlyTheMatchingOwnerRigidPose()
    {
        var resource=CompileFixture();var hierarchy=Assert.Single(resource.Hierarchies);
        var matrices=new Dictionary<int,Matrix4x4>{{1,Matrix4x4.CreateTranslation(4,2,1)}};
        var transforms=new EnbW3dHierarchyState(hierarchy.Name,Matrix4x4.Identity,2,matrices);
        var preparation=new EnbW3dMeshOccurrencePreparation();var owner=SceneObject(1);
        Assert.True(EnbCurrentRetailShipLegoRuntime.TryEvaluateScaledRigidHierarchyPose(hierarchy,Matrix4x4.Identity,
            2,[],matrices,out var rigid,out var error),error);
        preparation.RigidHierarchyPose=new(hierarchy,Matrix4x4.Identity,2,[],matrices,rigid);
        EnbW3dDrawTask? queued=null;
        for(int frame=0;frame<3;frame++)
        {
            if(frame==1)matrices[1]=Matrix4x4.CreateTranslation(20,8,3);
            if(frame==2)
            {
                owner=owner with {VisualGeneration=owner.VisualGeneration+1};
                matrices[1]=Matrix4x4.CreateTranslation(31,9,4);
                Assert.True(EnbCurrentRetailShipLegoRuntime.TryEvaluateScaledRigidHierarchyPose(hierarchy,Matrix4x4.Identity,
                    2,[],matrices,out rigid,out error),error);
                preparation.RigidHierarchyPose=new(hierarchy,Matrix4x4.Identity,2,[],matrices,rigid);
            }
            Assert.True(EnbW3dDrawPacketCompiler.TryBuildPrepared(owner,resource,Matrix4x4.Identity,frame,
                EnbRenderPassKind.OrdinaryWorld,null,EnbW3dLightEnvironment.BlackNoLights,[],transforms,preparation,out var actual,out error),error);
            Assert.True(EnbW3dDrawPacketCompiler.TryBuildAtWorldTransform(owner,resource,Matrix4x4.Identity,frame,
                EnbRenderPassKind.OrdinaryWorld,null,EnbW3dLightEnvironment.BlackNoLights,[],transforms,out var literal,out error),error);
            Assert.Equal<EnbW3dDrawTask>(literal.Packets,actual.Packets);
            Assert.Equal(frame==2?2:1,preparation.BorrowedPoseReuseCount);
            if(queued is null)queued=Assert.Single(actual.Packets);
            else Assert.NotEqual(queued.WorldTransform,Assert.Single(actual.Packets).WorldTransform);
        }
    }

    [Fact]
    public void RetainedLayoutTracksAnimationAndMutableOverrideInputs()
    {
        var source=CompileFixture();
        var animation=new EnbW3dRenderHierarchyAnimation(0,0x200,"move","SHIP_TREE",2,30,false,
            [new EnbW3dRenderAnimationChannel(0,1,1,(ushort)EnbW3dAnimationChannelKind.TranslationX,
                EnbW3dAnimationChannelKind.TranslationX,1,1,[0f,40f])],[]);
        var resource=source.Copy().SetAnimations([animation]);
        var owner=SceneObject(1); var preparation=new EnbW3dMeshOccurrencePreparation();
        var matrices=new Dictionary<int,Matrix4x4>{{1,Matrix4x4.Identity}};
        var transforms=new EnbW3dHierarchyState("SHIP_TREE",Matrix4x4.Identity,1,matrices);
        EnbW3dWeightedAnimationSample[] samples=[new("move",0,1)];
        EnbW3dDrawInvocation? queued=null; Matrix4x4 queuedWorld=default;
        long expectedBuilds=0;
        for(int change=0;change<8;change++)
        {
            switch(change)
            {
                case 1: matrices[1]=Matrix4x4.CreateTranslation(9,3,1);break;
                case 2: samples[0]=new("move",1,1);break;
                case 3: samples[0]=new("move",1,0.5f);break;
                case 4: transforms.Update(transforms.HierarchyName,transforms.RigidWorldTransform,2,transforms.Transforms,transforms.PendingHierarchyScale);break;
                case 5: transforms.PendingHierarchyScale=3;break;
                case 6: transforms.Update(transforms.HierarchyName,Matrix4x4.CreateTranslation(8,0,0),transforms.RenderObjectScale,transforms.Transforms,transforms.PendingHierarchyScale);break;
                case 7: matrices.Clear();break;
            }
            expectedBuilds++;
            for(int repeat=0;repeat<4;repeat++)
            {
                long time=change*4+repeat;
                var lights=EnbW3dLightEnvironment.CreateCurrentRetail((uint)(time*0x10101),[]);
                Assert.True(EnbW3dDrawPacketCompiler.TryBuildPrepared(owner,resource,Matrix4x4.Identity,time,
                    EnbRenderPassKind.OrdinaryWorld,null,lights,samples,transforms,preparation,out var actual,out var error),error);
                Assert.True(EnbW3dDrawPacketCompiler.TryBuildAtWorldTransform(owner,resource,Matrix4x4.Identity,time,
                    EnbRenderPassKind.OrdinaryWorld,null,lights,samples,transforms,out var expected,out error),error);
                Assert.Equal<EnbW3dDrawTask>(expected.Packets,actual.Packets);
                Assert.Equal(expectedBuilds,preparation.LayoutPreparationCount);
                if(queued is null){queued=actual;queuedWorld=Assert.Single(actual.Packets).WorldTransform;}
                Assert.Equal(queuedWorld,Assert.Single(queued.Packets).WorldTransform);
                Assert.Equal(0,queued.FrameTimeMilliseconds);
            }
        }
        Assert.NotEqual(queuedWorld,Assert.Single(EnbW3dDrawPacketCompiler.TryBuildPrepared(owner,resource,
            Matrix4x4.Identity,40,EnbRenderPassKind.OrdinaryWorld,null,EnbW3dLightEnvironment.BlackNoLights,
            samples,transforms,preparation,out var final,out _) ? final.Packets : []).WorldTransform);
    }

    [Fact]
    public void RetainedStaticHierarchyLayoutUpdatesFrameInputsAndInvalidatesLocally()
    {
        var resource=CompileFixture();var owner=SceneObject(1);var preparation=new EnbW3dMeshOccurrencePreparation();
        var world=Matrix4x4.Identity;EnbW3dDrawInvocation? first=null;
        for(int frame=0;frame<8;frame++)
        {
            var lights=EnbW3dLightEnvironment.CreateCurrentRetail((uint)(frame*0x10101),[]);
            Assert.True(EnbW3dDrawPacketCompiler.TryBuildPrepared(owner,resource,world,frame,EnbRenderPassKind.OrdinaryWorld,
                null,lights,[],null,preparation,out var actual,out var error),error);
            Assert.True(EnbW3dDrawPacketCompiler.TryBuildAtWorldTransform(owner,resource,world,frame,EnbRenderPassKind.OrdinaryWorld,
                null,lights,(EnbW3dAnimationSample?)null,out var expected,out error),error);
            Assert.Equal<EnbW3dDrawTask>(expected.Packets,actual.Packets);
            Assert.Equal<EnbW3dParticleEmitterDrawInstance>(expected.ParticleEmitters,actual.ParticleEmitters);
            Assert.Equal<EnbW3dDazzleDrawInstance>(expected.Dazzles,actual.Dazzles);
            first??=actual;Assert.Equal(0,first.FrameTimeMilliseconds);
            Assert.All(first.Packets,p=>Assert.Equal(0,p.FrameTimeMilliseconds));
        }
        Assert.Equal(1,preparation.LayoutPreparationCount);
        // Animation admission still validates the actual requested animation.
        Assert.False(EnbW3dDrawPacketCompiler.TryBuildPrepared(owner,resource,world,9,EnbRenderPassKind.OrdinaryWorld,
            null,EnbW3dLightEnvironment.BlackNoLights,[new("missing-animation",0,1)],null,preparation,out _,out _));
        Assert.True(EnbW3dDrawPacketCompiler.TryBuildPrepared(owner,resource,world,10,EnbRenderPassKind.OrdinaryWorld,
            null,EnbW3dLightEnvironment.BlackNoLights,[],null,preparation,out _,out _));
        Assert.Equal(2,preparation.LayoutPreparationCount);
        owner=owner with {VisualGeneration=owner.VisualGeneration+1};
        Assert.True(EnbW3dDrawPacketCompiler.TryBuildPrepared(owner,resource,Matrix4x4.CreateTranslation(5,1,2),11,
            EnbRenderPassKind.OrdinaryWorld,null,EnbW3dLightEnvironment.BlackNoLights,[],null,preparation,out var moved,out _));
        Assert.Equal(3,preparation.LayoutPreparationCount);Assert.Equal(owner.VisualGeneration,moved.VisualGeneration);
        Assert.NotEqual(first!.Packets[0].WorldTransform,moved.Packets[0].WorldTransform);
    }
    [Fact]
    public void SkinOutputDependsOnlyOnConsumedBoneWordsAndSurvivesLaterUpdates()
    {
        var mesh=Assert.Single(CompileFixture().Meshes);
        mesh=(mesh).Copy().SetBoneIndices(Enumerable.Repeat((ushort)0,mesh.Positions.Count).ToArray());
        var preparation=new EnbW3dMeshOccurrencePreparation();
        var firstPose=new EnbW3dHierarchyPoseOwner([Matrix4x4.Identity,Matrix4x4.Identity],[true,true],null,null);
        Assert.True(preparation.TryDeform(mesh,firstPose,out var first,out var error),error);
        var unrelated=new EnbW3dHierarchyPoseOwner([Matrix4x4.Identity,Matrix4x4.CreateTranslation(30,0,0)],[false,false],null,null);
        Assert.True(preparation.TryDeform(mesh,unrelated,out var same,out error),error);
        Assert.Same(first,same);
        var saved=first.Positions.ToArray();
        var moved=new EnbW3dHierarchyPoseOwner([Matrix4x4.CreateTranslation(7,0,0),Matrix4x4.Identity],unrelated.Visibility,null,null);
        Assert.True(preparation.TryDeform(mesh,moved,out var current,out error),error);
        Assert.NotSame(first,current);
        Assert.True(EnbW3dSkinDeformer.TryDeform(mesh,moved,out var literal,out error),error);
        Assert.Equal<Vector3>(literal.Positions,current.Positions);
        Assert.Equal<Vector3>(literal.Normals,current.Normals);
        Assert.Equal<Vector3>(saved,first.Positions);
        var signedZero=Matrix4x4.Identity;signedZero.M12=BitConverter.Int32BitsToSingle(unchecked((int)0x80000000));
        Assert.False(EnbW3dSkinDeformer.UsesSameTransforms(mesh,firstPose,new EnbW3dHierarchyPoseOwner([signedZero,Matrix4x4.Identity],firstPose.Visibility,null,null)));
        var missing=new EnbW3dHierarchyPoseOwner([],firstPose.Visibility,null,null);
        Assert.False(preparation.TryDeform(mesh,missing,out _,out error));
        Assert.Contains("missing hierarchy pivot",error);
    }

    [Fact]
    public void PreparedSkinBindingsMatchLiteralDeformationAcrossChangingPoses()
    {
        var source=Assert.Single(CompileFixture().Meshes);var random=new Random(9471);
        var positions=Enumerable.Range(0,500).Select(_=>new Vector3(random.NextSingle(),random.NextSingle(),random.NextSingle())).ToArray();
        var mesh=(source).Copy().SetPositions(positions).SetNormals(positions.Select(Vector3.Normalize).ToArray()).SetBoneIndices(Enumerable.Range(0,positions.Length).Select(i=>(ushort)((i/3)%7)).ToArray());
        for(int frame=0;frame<20;frame++)
        {
            var matrices=Enumerable.Range(0,7).Select(i=>Matrix4x4.CreateScale(1+i*.1f)*Matrix4x4.CreateRotationY(frame*.07f+i)*
                Matrix4x4.CreateTranslation(frame,i,frame-i)).ToImmutableArray();
            var pose=new EnbW3dHierarchyPoseOwner(matrices,Enumerable.Repeat(true,7).ToImmutableArray(),null,null);
            Assert.True(EnbW3dSkinDeformer.TryDeform(mesh,pose,out var expected,out var error),error);
            Assert.True(EnbW3dSkinDeformer.TryDeformPrepared(mesh,pose,out var actual,out error),error);
            Assert.Equal<Vector3>(expected.Positions,actual.Positions);
            Assert.Equal<Vector3>(expected.Normals,actual.Normals);
            var truncated=new EnbW3dHierarchyPoseOwner(matrices.Take(2).ToArray(),pose.Visibility,null,null);
            Assert.False(EnbW3dSkinDeformer.TryDeform(mesh,truncated,out _,out var expectedError));
            Assert.False(EnbW3dSkinDeformer.TryDeformPrepared(mesh,truncated,out _,out error));
            Assert.Equal(expectedError,error);
        }
    }

    [Fact]
    public void PreparedSkinRetainsOnlyUnchangedPoseAndKeepsQueuedGeometryImmutable()
    {
        var source=CompileFixture();
        var mesh=Assert.Single(source.Meshes);
        var resource=source.Copy().SetMeshes([(mesh).Copy().SetBoneIndices(Enumerable.Repeat((ushort)0,mesh.Positions.Count).ToArray())]);
        var preparation=new EnbW3dMeshOccurrencePreparation();
        EnbW3dDrawTask Build(Matrix4x4 world,long clock)
        {
            Assert.True(EnbW3dDrawPacketCompiler.TryBuildPrepared(SceneObject(1),resource,world,clock,
                EnbRenderPassKind.OrdinaryWorld,null,EnbW3dLightEnvironment.BlackNoLights,[],null,
                preparation,out var result,out var error),error);
            return Assert.Single(result.Packets);
        }
        var first=Build(Matrix4x4.Identity,0);
        var same=Build(Matrix4x4.Identity,1);
        Assert.NotNull(first.DeformedGeometry);
        Assert.Same(first.DeformedGeometry,same.DeformedGeometry);
        var saved=first.DeformedGeometry.Positions.ToArray();
        var moved=Build(Matrix4x4.CreateTranslation(7,0,0),2);
        Assert.NotSame(first.DeformedGeometry,moved.DeformedGeometry);
        Assert.Equal<Vector3>(saved,first.DeformedGeometry.Positions);
        for(int i=0;i<saved.Length;i++)Assert.Equal(saved[i]+new Vector3(7,0,0),moved.DeformedGeometry!.Positions[i]);
    }

    [Fact]
    public void PreparedHierarchyOccurrencesPreserveMovingViewsAndReplaceChangedTopology()
    {
        var resource = CompileFixture();
        var owner = SceneObject(1);
        var prepared = new EnbW3dMeshOccurrencePreparation();
        object? firstDefinition = null;
        EnbW3dDrawTask? firstPacket = null;
        for (int frame = 0; frame < 12; frame++)
        {
            var world = Matrix4x4.CreateRotationY(frame * .1f) * Matrix4x4.CreateTranslation(frame, 0, 0);
            Assert.True(EnbW3dDrawPacketCompiler.TryBuildPrepared(owner, resource, world, frame,
                EnbRenderPassKind.OrdinaryWorld, null, EnbW3dLightEnvironment.BlackNoLights, [], null,
                prepared, out var actual, out var error), error);
            Assert.True(EnbW3dDrawPacketCompiler.TryBuildAtWorldTransform(owner, resource, world, frame,
                EnbRenderPassKind.OrdinaryWorld, null, EnbW3dLightEnvironment.BlackNoLights,
                (EnbW3dAnimationSample?)null, out var expected, out error), error);
            Assert.Equal<EnbW3dDrawTask>(expected.Packets, actual.Packets);
            var packet = Assert.Single(actual.Packets);
            firstDefinition ??= packet.PreparedIdentity;
            Assert.Same(firstDefinition, packet.PreparedIdentity);
            firstPacket ??= packet;
            Assert.Equal(0, firstPacket.FrameTimeMilliseconds);
        }
        Assert.Equal(1, prepared.PreparationCount);
        var mesh = Assert.Single(resource.Meshes);
        var changed = resource.Copy().SetMeshes([(mesh).Copy().SetMaterialPasses(mesh.MaterialPasses.Select(p => (p).Copy()).ToArray())]);
        Assert.True(EnbW3dDrawPacketCompiler.TryBuildPrepared(owner, changed, Matrix4x4.Identity, 20,
            EnbRenderPassKind.OrdinaryWorld, null, EnbW3dLightEnvironment.BlackNoLights, [], null,
            prepared, out var replacement, out var diagnostic), diagnostic);
        Assert.NotSame(firstDefinition, Assert.Single(replacement.Packets).PreparedIdentity);
        Assert.Equal(2, prepared.PreparationCount);
        Assert.Same(mesh, firstPacket!.Mesh);
    }

    [Fact]
    public void CachedHlodBindingsReleaseWithTheirResource()
    {
        var references = BuildTemporaryHlodResource();
        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.All(references, reference => Assert.False(reference.IsAlive));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference[] BuildTemporaryHlodResource()
    {
        var resource = CompileFixture();
        var hlod = Assert.Single(resource.HierarchicalLods);
        var level = Assert.Single(hlod.Levels);
        Assert.True(EnbW3dDrawPacketCompiler.TryBuildAtWorldTransform(SceneObject(1), resource,
            Matrix4x4.Identity, 0, EnbRenderPassKind.OrdinaryWorld,
            new EnbW3dHlodSelection(hlod.Name, level.Ordinal), EnbW3dLightEnvironment.BlackNoLights,
            (EnbW3dAnimationSample?)null, out var list, out var diagnostic), diagnostic);
        Assert.Single(list.Packets);
        return [new(resource), new(hlod), new(level)];
    }

    [Fact]
    public void RetainedMeshBindingsDoNotRetainWorldPoseOrLeakIntoResourceCopies()
    {
        var resource = CompileFixture();
        EnbW3dDrawInvocation Build(EnbW3dRenderResource source, float x)
        {
            Assert.True(EnbW3dDrawPacketCompiler.TryBuildAtWorldTransform(SceneObject(1), source,
                Matrix4x4.CreateTranslation(x, 0, 0), 0, EnbRenderPassKind.OrdinaryWorld,
                null, EnbW3dLightEnvironment.BlackNoLights, (EnbW3dAnimationSample?)null,
                out var list, out var diagnostic), diagnostic);
            return list;
        }

        var first = Assert.Single(Build(resource, 0).Packets);
        var moved = Assert.Single(Build(resource, 100).Packets);
        AssertVector(first.WorldTransform.Translation + new Vector3(100, 0, 0), moved.WorldTransform.Translation);
        var model = Assert.Single(resource.HierarchyModels);
        var node = Assert.Single(model.Nodes);
        var copy = resource.Copy().SetHierarchyModels([model with { Nodes = [node, node with { Ordinal = node.Ordinal + 1 }] }]);
        Assert.Equal(2, Build(copy, 0).Packets.Count);
        Assert.Single(Build(resource, 0).Packets);
    }

    [Fact]
    public void ZeroFactoryScaleRetainsHierarchyRotationForNormals()
    {
        var resource = CompileFixture();
        var rigid = Matrix4x4.CreateTranslation(100, 200, 300);
        Assert.True(EnbW3dDrawPacketCompiler.TryBuildAtWorldTransform(SceneObject(0), resource,
            Matrix4x4.CreateScale(0) * rigid, 0, EnbRenderPassKind.OrdinaryWorld,
            new EnbW3dHlodSelection("SHIPROOT", 0), EnbW3dLightEnvironment.BlackNoLights, (EnbW3dAnimationSample?)null,
            out var list, out var diagnostic, new(rigid, 0)), diagnostic);
        var packet = Assert.Single(list.Packets);
        var normalWorld = Assert.IsType<Matrix4x4>(packet.CurrentRetailUnscaledWorldTransform);
        AssertVector(rigid.Translation, packet.WorldTransform.Translation);
        AssertVector(rigid.Translation, normalWorld.Translation);
        AssertVector(Vector3.UnitY, Vector3.TransformNormal(Vector3.UnitX, normalWorld));
        AssertVector(Vector3.Zero, Vector3.TransformNormal(Vector3.UnitX, packet.WorldTransform));
    }

    [Fact]
    public void R4CCompilerPromotesCurrentRetailHierarchyQuaternionAndLocalTransformSemantics()
    {
        EnbW3dRenderResource resource = CompileFixture();
        EnbW3dRenderHierarchy hierarchy = Assert.Single(resource.Hierarchies);
        Assert.Equal("SHIP_TREE", hierarchy.Name);
        Assert.Equal(2, hierarchy.Pivots.Count);

        EnbW3dRenderPivot root = hierarchy.Pivots[0];
        Assert.True(root.IsRoot);
        Assert.Equal(new Vector3(500, 600, 700), root.Translation);

        EnbW3dRenderPivot mount = hierarchy.Pivots[1];
        float halfSqrt = MathF.Sqrt(0.5f);
        Assert.Equal(0f, mount.Rotation.X, 6);
        Assert.Equal(0f, mount.Rotation.Y, 6);
        Assert.Equal(halfSqrt, mount.Rotation.Z, 6);
        Assert.Equal(halfSqrt, mount.Rotation.W, 6);

        Vector3 transformed = Vector3.Transform(Vector3.UnitX, mount.LocalTransform);
        AssertVector(new Vector3(10, 21, 30), transformed);

        EnbW3dRenderHierarchyModel model = Assert.Single(resource.HierarchyModels);
        Assert.Equal("SHIP_TREE", model.HierarchyName);
        EnbW3dRenderHierarchyModelNode node = Assert.Single(model.Nodes);
        Assert.Equal(EnbW3dRenderHierarchyNodeKind.RenderObject, node.Kind);
        Assert.Equal((ushort)1, node.PivotIndex);
    }

    [Fact]
    public void R4CHierarchyPoseUsesObjectTransformForRootAndLocalTimesParentForChildren()
    {
        EnbW3dRenderHierarchy hierarchy = Assert.Single(CompileFixture().Hierarchies);
        Matrix4x4 objectWorld = Matrix4x4.CreateScale(2f) * Matrix4x4.CreateTranslation(100, 200, 300);

        Assert.True(EnbW3dHierarchyPose.TryEvaluate(hierarchy, objectWorld, out Matrix4x4[] poses, out string? diagnostic), diagnostic);
        Assert.Equal(2, poses.Length);

        // Current retail overwrites pivot 0 with the owning object transform;
        // the authored ROOT translation (500,600,700) is deliberately ignored.
        AssertVector(new Vector3(100, 200, 300), Translation(poses[0]));
        AssertVector(new Vector3(120, 240, 360), Translation(poses[1]));

        Vector3 childForward = Vector3.TransformNormal(Vector3.UnitX, poses[1]);
        AssertVector(new Vector3(0, 2, 0), childForward);
    }

    [Fact]
    public void R4CDrawCompilerBuildsStableRigidPacketsAndUsesHmodelBindingWhenPresent()
    {
        EnbW3dRenderResource resource = CompileFixture();
        EnbSceneObjectSnapshot sceneObject = SceneObject(scale: 2f);

        Assert.True(EnbW3dDrawPacketCompiler.TryBuild(
            sceneObject,
            resource,
            frameTimeMilliseconds: 1234,
            renderPass: EnbRenderPassKind.OrdinaryWorld,
            hlodSelection: new EnbW3dHlodSelection("SHIPROOT", 0),
            lightEnvironment: EnbW3dLightEnvironment.BlackNoLights,
            out EnbW3dDrawInvocation drawList,
            out string? diagnostic), diagnostic);

        EnbW3dDrawTask packet = Assert.Single(drawList.Packets);
        Assert.Equal(sceneObject.RenderIdentity, packet.RenderIdentity);
        Assert.Equal(sceneObject.VisualGeneration, packet.VisualGeneration);
        Assert.Equal((uint)0x1234, packet.GameId);
        Assert.Equal("SHIPMESH", packet.Mesh.Name);
        Assert.Equal("SHIP_TREE", packet.HierarchyName);
        Assert.Equal(1, packet.PivotIndex);
        Assert.Equal("SHIPROOT.SHIPMESH", packet.RenderObjectName);
        Assert.Equal(1234, packet.FrameTimeMilliseconds);
        Assert.Equal(2f, packet.CurrentRetailMeshModelScale, 4);
        AssertVector(new Vector3(120, 240, 360), Translation(packet.WorldTransform));
        AssertVector(new Vector3(120, 240, 360), packet.Bounds.Center);
        Assert.Equal(11f, packet.Bounds.Radius, 4);

        Assert.Equal(EnbRenderPassKind.OrdinaryWorld, packet.OrderKey.RenderPass);
        Assert.Equal(EnbW3dDrawBlendClass.Opaque, packet.OrderKey.BlendClass);
        Assert.Equal(EnbW3dStaticSortDisposition.Immediate, packet.OrderKey.StaticSort.Disposition);
        Assert.Equal(resource.Key, packet.OrderKey.ResourceKey);
        Assert.Equal(0, packet.OrderKey.MeshOrdinal);
        Assert.Equal(0, packet.OrderKey.MaterialPassOrdinal);
        Assert.Equal(sceneObject.RenderIdentity, packet.OrderKey.RenderIdentity);

        EnbW3dCompiledMaterialSemantics material = packet.Material.SingleBindingSemantics
            ?? throw new Xunit.Sdk.XunitException("Expected the unambiguous fixture pass to compile to R4B material semantics.");
        Assert.Equal(EnbRenderDepthCompare.LessOrEqual, material.PermutationKey.DepthCompare);
        Assert.True(material.PermutationKey.DepthWriteEnabled);

        EnbW3dRenderHlod hlod = Assert.Single(resource.HierarchicalLods);
        EnbW3dRenderHlodMember member = Assert.Single(Assert.Single(hlod.Levels).Members);
        Assert.Equal((uint)77, member.ReferenceIndex);
        Assert.Equal("SHIPMESH", member.Name);
    }

    [Fact]
    public void R9AWDrawCompilerRepairsRomulusNonUnitQuaternionBeforeApplyingUniformScale()
    {
        Quaternion romulusOrientation = new(0f, 0f, 0.9f, 0f);
        EnbW3dRenderResource resource = CompileFixture();
        EnbSceneObjectSnapshot sceneObject = SceneObject(scale: 5f, orientation: romulusOrientation);

        Assert.True(EnbW3dDrawPacketCompiler.TryBuild(
            sceneObject,
            resource,
            frameTimeMilliseconds: 0,
            renderPass: EnbRenderPassKind.OrdinaryWorld,
            hlodSelection: new EnbW3dHlodSelection("SHIPROOT", 0),
            lightEnvironment: EnbW3dLightEnvironment.BlackNoLights,
            out EnbW3dDrawInvocation drawList,
            out string? diagnostic), diagnostic);

        EnbW3dDrawTask packet = Assert.Single(drawList.Packets);
        Matrix4x4 expectedRigidWorld =
            Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(romulusOrientation)) *
            Matrix4x4.CreateTranslation(100f, 200f, 300f);
        Matrix4x4 expected = resource.Hierarchies[0].Pivots[1].LocalTransform *
                             Matrix4x4.CreateScale(5f) *
                             expectedRigidWorld;

        AssertMatrix(expected, packet.WorldTransform);
        Assert.Equal(5f, new Vector3(packet.WorldTransform.M11, packet.WorldTransform.M12, packet.WorldTransform.M13).Length(), 4);
        Assert.Equal(5f, new Vector3(packet.WorldTransform.M21, packet.WorldTransform.M22, packet.WorldTransform.M23).Length(), 4);
        Assert.Equal(5f, new Vector3(packet.WorldTransform.M31, packet.WorldTransform.M32, packet.WorldTransform.M33).Length(), 4);
    }

    [Fact]
    public void R9XSimpleHlodMemberFailsClosedWhenReferenceIndexIsOutsideHierarchy()
    {
        EnbW3dRenderResource resource = CompileFixture().Copy().SetHierarchyModels(Array.Empty<EnbW3dRenderHierarchyModel>());

        Assert.False(EnbW3dDrawPacketCompiler.TryBuild(
            SceneObject(scale: 2f),
            resource,
            frameTimeMilliseconds: 0,
            renderPass: EnbRenderPassKind.OrdinaryWorld,
            hlodSelection: new EnbW3dHlodSelection("SHIPROOT", 0),
            lightEnvironment: EnbW3dLightEnvironment.BlackNoLights,
            out _,
            out string? diagnostic));

        Assert.Contains("state record 77", diagnostic, StringComparison.Ordinal);
        Assert.Contains("outside 'SHIP_TREE' (2 records)", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void R4MDrawBoundsTransformAuthoredSphereCenterAndCarryStaticSortLevel()
    {
        var localSphereCenter = new Vector3(2f, 3f, 4f);
        EnbResolvedW3dResource source = EnbR4W3dRenderResourceTests.BuildResolvedResource(
            shaderId: 0,
            authoredSortLevel: 32,
            boundingBoxMin: new Vector3(-5f, -6f, -7f),
            boundingBoxMax: new Vector3(8f, 9f, 10f),
            boundingSphereCenter: localSphereCenter);
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(
            source,
            out EnbW3dRenderResource resource,
            out string? diagnostic), diagnostic);

        Assert.True(EnbW3dDrawPacketCompiler.TryBuild(
            SceneObject(scale: 2f),
            resource,
            frameTimeMilliseconds: 0,
            renderPass: EnbRenderPassKind.OrdinaryWorld,
            hlodSelection: null,
            lightEnvironment: EnbW3dLightEnvironment.BlackNoLights,
            out EnbW3dDrawInvocation drawList,
            out diagnostic), diagnostic);

        EnbW3dDrawTask packet = Assert.Single(drawList.Packets);
        Vector3 expectedCenter = Vector3.Transform(localSphereCenter, packet.WorldTransform);
        AssertVector(expectedCenter, packet.Bounds.Center);
        Assert.NotEqual(Translation(packet.WorldTransform), packet.Bounds.Center);
        Assert.Equal(11f, packet.Bounds.Radius, 4);
        Assert.Equal((sbyte)32, packet.OrderKey.StaticSort.RuntimeLevel);
        Assert.Equal(EnbW3dStaticSortDisposition.DeferredBucket, packet.OrderKey.StaticSort.Disposition);
    }

    [Fact]
    public void R4CLegacyHierarchyAndHmodelVersionsReproduceRetailSyntheticRootIndexShift()
    {
        EnbResolvedW3dResource source = EnbR4W3dRenderResourceTests.BuildResolvedResource(
            shaderId: 0,
            hierarchyVersion: 0x0002_FFFFu,
            hierarchyModelVersion: 0x0002_FFFFu);
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(source, out EnbW3dRenderResource resource, out string? diagnostic), diagnostic);

        EnbW3dRenderHierarchy hierarchy = Assert.Single(resource.Hierarchies);
        Assert.True(hierarchy.HasSyntheticRetailRoot);
        Assert.Equal("RootTransform", hierarchy.Pivots[0].Name);
        Assert.Equal((uint)0, hierarchy.Pivots[1].ParentIndex);
        Assert.Equal((uint)1, hierarchy.Pivots[2].ParentIndex);

        EnbW3dRenderHierarchyModelNode node = Assert.Single(Assert.Single(resource.HierarchyModels).Nodes);
        Assert.Equal((ushort)2, node.PivotIndex);

        Assert.True(EnbW3dDrawPacketCompiler.TryBuild(
            SceneObject(scale: 2f),
            resource,
            frameTimeMilliseconds: 0,
            renderPass: EnbRenderPassKind.OrdinaryWorld,
            hlodSelection: null,
            lightEnvironment: EnbW3dLightEnvironment.BlackNoLights,
            out EnbW3dDrawInvocation drawList,
            out diagnostic), diagnostic);
        EnbW3dDrawTask packet = Assert.Single(drawList.Packets);
        Assert.Equal(2, packet.PivotIndex);
        AssertVector(new Vector3(1120, 1440, 1760), Translation(packet.WorldTransform));
    }

    [Fact]
    public void R4CHierarchyEvaluatorFailsClosedForNonTopologicalParentOrder()
    {
        var hierarchy = new EnbW3dRenderHierarchy(
            0x0003_0000u,
            "BAD",
            Vector3.Zero,
            false,
            new EnbW3dRenderPivot[]
            {
                new(0, "ROOT", uint.MaxValue, Vector3.Zero, Vector3.Zero, Quaternion.Identity, Matrix4x4.Identity),
                new(1, "BAD_CHILD", 1, Vector3.Zero, Vector3.Zero, Quaternion.Identity, Matrix4x4.Identity),
            },
            Array.Empty<EnbW3dRenderPivotFixup>());

        Assert.False(EnbW3dHierarchyPose.TryEvaluate(hierarchy, Matrix4x4.Identity, out _, out string? diagnostic));
        string actual = diagnostic ?? throw new Xunit.Sdk.XunitException("Expected a hierarchy topology diagnostic.");
        Assert.Contains("requires an earlier pivot", actual, StringComparison.Ordinal);
    }

    [Fact]
    public void R4CDrawPacketLayerRemainsBackendIndependentAndLeavesLodChoiceToR5()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(root, "src", "EnbNext.Rendering", "EnbW3dDrawPackets.cs"));

        Assert.Contains("EnbW3dHlodSelection", source, StringComparison.Ordinal);
        Assert.Contains("EnbW3dHierarchyPose", source, StringComparison.Ordinal);
        Assert.Contains("EnbW3dDrawPacketCompiler", source, StringComparison.Ordinal);
        Assert.Contains("ReferenceIndex", File.ReadAllText(Path.Combine(root, "src", "EnbNext.Rendering", "EnbW3dRenderResource.cs")), StringComparison.Ordinal);
        Assert.DoesNotContain("cameraDistance", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("projectedDetail", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Avalonia", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EnbNext.Net7", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Veldrid", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Silk.NET", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Direct3D", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OpenGL", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Vulkan", source, StringComparison.OrdinalIgnoreCase);
    }

    private static EnbW3dRenderResource CompileFixture()
    {
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(
            EnbR4W3dRenderResourceTests.BuildResolvedResource(shaderId: 0),
            out EnbW3dRenderResource resource,
            out string? diagnostic), diagnostic);
        return resource;
    }

    private static EnbSceneObjectSnapshot SceneObject(float scale, Quaternion? orientation = null)
        => new(
            RenderIdentity: 0x1122_3344_5566_7788UL,
            VisualGeneration: 7,
            GameId: 0x1234,
            IsLocalShip: false,
            IsCurrentTarget: false,
            ObjectType: 1,
            BaseAsset: 42,
            Scale: scale,
            HsvH: 0,
            HsvS: 0,
            HsvV: 0,
            RelationshipRaw: 0,
            IsAttacking: false,
            Name: "R4C fixture",
            Radar: new EnbSceneRadarSnapshot(false, 0, null),
            Spatial: new EnbSceneSpatialSnapshot(
                EnbSceneSpatialProviderKind.Fixed,
                new EnbSceneTransformSnapshot(
                    ClientTime: 100,
                    Position: new Vector3(100, 200, 300),
                    Orientation: orientation ?? Quaternion.Identity,
                    Velocity: Vector3.Zero,
                    Speed: 0,
                    Rotation: Vector3.Zero,
                    OrientationColumn0: Vector3.UnitX),
                Planet: null),
            BaseAssetReference: null,
            ClientBaseAssetReference: null,
            Decals: ImmutableArray<EnbSceneDecalSnapshot>.Empty,
            ColorizationEntries: ImmutableArray<EnbSceneColorizationSnapshot>.Empty,
            RenderStateLinks: ImmutableArray<EnbSceneRenderStateLinkSnapshot>.Empty,
            ActiveRenderStates: ImmutableArray<uint>.Empty,
            RenderStateActions: ImmutableArray<EnbSceneRenderStateActionSnapshot>.Empty,
            Attachments: ImmutableArray<EnbSceneAttachmentSnapshot>.Empty,
            NameDecal: null,
            SubParts: ImmutableArray<EnbSceneSubPartSnapshot>.Empty,
            Avatar: null,
            SpecialTargetEndpointId: 0,
            SpecialTargetTransition: 0);

    private static Vector3 Translation(Matrix4x4 matrix)
        => new(matrix.M41, matrix.M42, matrix.M43);

    private static void AssertVector(Vector3 expected, Vector3 actual)
    {
        Assert.Equal(expected.X, actual.X, 4);
        Assert.Equal(expected.Y, actual.Y, 4);
        Assert.Equal(expected.Z, actual.Z, 4);
    }

    private static void AssertMatrix(Matrix4x4 expected, Matrix4x4 actual)
    {
        Assert.Equal(expected.M11, actual.M11, 4);
        Assert.Equal(expected.M12, actual.M12, 4);
        Assert.Equal(expected.M13, actual.M13, 4);
        Assert.Equal(expected.M14, actual.M14, 4);
        Assert.Equal(expected.M21, actual.M21, 4);
        Assert.Equal(expected.M22, actual.M22, 4);
        Assert.Equal(expected.M23, actual.M23, 4);
        Assert.Equal(expected.M24, actual.M24, 4);
        Assert.Equal(expected.M31, actual.M31, 4);
        Assert.Equal(expected.M32, actual.M32, 4);
        Assert.Equal(expected.M33, actual.M33, 4);
        Assert.Equal(expected.M34, actual.M34, 4);
        Assert.Equal(expected.M41, actual.M41, 4);
        Assert.Equal(expected.M42, actual.M42, 4);
        Assert.Equal(expected.M43, actual.M43, 4);
        Assert.Equal(expected.M44, actual.M44, 4);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "EnbNext.slnx")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate EnbNext repository root.");
    }
}
