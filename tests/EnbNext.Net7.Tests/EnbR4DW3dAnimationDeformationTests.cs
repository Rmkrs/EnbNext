using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Text;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR4DW3dAnimationDeformationTests
{
    [Fact]
    public void PreparedSkinPublishesItsCompletedPositionsAndKeepsOlderPicks()
    {
        var source=CompileR4Fixture();var mesh=(Assert.Single(source.Meshes)).Copy().SetBoneIndices(new ushort[]{0,1,1});
        var resource=source.Copy().SetMeshes(new[]{mesh});var preparation=new EnbW3dMeshOccurrencePreparation();
        var arena=new EnbW3dTransientGeometryArena();EnbSubmittedMeshPick pick=default, literalPick=default;
        Vector3[] expected=[];long allocatedArrays=0;
        Assert.True(EnbW3dDrawPacketCompiler.TryBuildPrepared(SceneObject(1),resource,Matrix4x4.CreateTranslation(-1,0,0),0,
            EnbRenderPassKind.OrdinaryWorld,null,EnbW3dLightEnvironment.BlackNoLights,[],null,preparation,out _,out var warmError),warmError);
        for(int tick=0;tick<3;tick++)
        {
            using var frame=arena.BeginFrame();using var scope=preparation.UseGeometryFrame(frame);
            Assert.True(EnbW3dDrawPacketCompiler.TryBuildPrepared(SceneObject(1),resource,Matrix4x4.CreateTranslation(tick,0,0),tick,
                EnbRenderPassKind.OrdinaryWorld,null,EnbW3dLightEnvironment.BlackNoLights,[],null,preparation,out var list,out var error),error);
            var packet=Assert.Single(list.Packets);
            Assert.True(EnbW3dIndexedSubmissionCompiler.TryCompile(packet,out var submission,out error),error);
            var stream=Assert.IsType<EnbW3dFrameStream<Vector3>>(submission.Positions);
            Assert.Throws<InvalidOperationException>(()=>{_=stream.WriteSpan.Length;});
            if(tick==0)
            {
                expected=stream.ToArray();pick=new(submission,Matrix4x4.Identity);
                Assert.Equal(expected,packet.DeformedGeometry!.PublicationPositions);
                allocatedArrays=arena.ArrayAllocations;
                var detached=packet.DeformedGeometry with {Positions=expected,Normals=Array.Empty<Vector3>()};
                Assert.Null(detached.PublicationPositions);
                literalPick=new(submission with {Packet=(packet).ForkTask().SetDeformedGeometry(detached)},Matrix4x4.Identity);
            }
        }
        Assert.Equal(allocatedArrays,arena.ArrayAllocations);
        var expectedBounds=EnbSubmittedMeshProjection.Project(expected,Matrix4x4.Identity,800,600);
        Assert.Equal(expectedBounds,pick.ProjectBounds(800,600));
        for(int y=0;y<=20;y++)for(int x=0;x<=20;x++)
            Assert.Equal(literalPick.Contains(x/20d,y/20d),pick.Contains(x/20d,y/20d));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreparedSkinRangesSurviveNestedAndRepeatedViewsThenExpireAndReuseCapacity(bool withNormals)
    {
        var source=CompileR4Fixture();var mesh=(Assert.Single(source.Meshes)).Copy().SetBoneIndices(new ushort[]{0,1,1});
        if(!withNormals)mesh=(mesh).Copy().SetNormals([]);
        var resource=source.Copy().SetMeshes(new[]{mesh});
        var owner=SceneObject(1);var preparation=new EnbW3dMeshOccurrencePreparation();
        var arena=new EnbW3dTransientGeometryArena();
        EnbW3dDeformedGeometry Build(int tick,EnbW3dTransientGeometryArena.Frame? frame)
        {
            using var scope=preparation.UseGeometryFrame(frame);
            var world=Matrix4x4.CreateRotationY(tick*.02f)*Matrix4x4.CreateTranslation(tick,2,3);
            Assert.True(EnbW3dDrawPacketCompiler.TryBuildPrepared(owner,resource,world,tick,EnbRenderPassKind.OrdinaryWorld,
                null,EnbW3dLightEnvironment.BlackNoLights,[],null,preparation,out var actual,out var error),error);
            Assert.True(EnbW3dDrawPacketCompiler.TryBuildAtWorldTransform(owner,resource,world,tick,EnbRenderPassKind.OrdinaryWorld,
                null,EnbW3dLightEnvironment.BlackNoLights,Array.Empty<EnbW3dWeightedAnimationSample>(),out var expected,out error),error);
            var a=Assert.Single(actual.Packets);var e=Assert.Single(expected.Packets);
            Assert.Equal(e.OrderKey,a.OrderKey);Assert.Equal(e.WorldTransform,a.WorldTransform);Assert.Equal(e.Bounds,a.Bounds);
            Assert.Equal(e.DeformedGeometry!.Positions.ToArray(),a.DeformedGeometry!.Positions.ToArray());
            Assert.Equal(e.DeformedGeometry.Normals.ToArray(),a.DeformedGeometry.Normals.ToArray());
            if(a.DeformedGeometry.PublicationPositions is {} publication)
                for(int i=0;i<publication.Count;i++)
                {
                    var expectedVertex=e.DeformedGeometry.Positions[i];var actualVertex=publication[i];
                    Assert.Equal(BitConverter.SingleToInt32Bits(expectedVertex.X),BitConverter.SingleToInt32Bits(actualVertex.X));
                    Assert.Equal(BitConverter.SingleToInt32Bits(expectedVertex.Y),BitConverter.SingleToInt32Bits(actualVertex.Y));
                    Assert.Equal(BitConverter.SingleToInt32Bits(expectedVertex.Z),BitConverter.SingleToInt32Bits(actualVertex.Z));
                }
            return a.DeformedGeometry;
        }
        var detached=Build(0,null);var saved=detached.Positions.ToArray();
        EnbW3dDeformedGeometry queued;
        EnbW3dNormalStream queuedNormals=default;
        using(var outer=arena.BeginFrame())
        {
            queued=Build(1,outer);Assert.IsType<EnbW3dFrameStream<Vector3>>(queued.Positions);
            var queuedCopy=queued.Positions.ToArray();
            queuedNormals=EnbW3dNormalStream.Snapshot(queued.Normals);
            Assert.Equal(queued.Normals.ToArray(),queuedNormals.ToArray());
            Assert.Same(queued,Build(1,outer));
            using(var inner=arena.BeginFrame())
            {
                var nested=Build(2,inner);Assert.NotSame(queued.Positions,nested.Positions);
                Assert.Equal(queuedCopy,queued.Positions.ToArray());
            }
            Build(3,outer);Assert.Equal(queuedCopy,queued.Positions.ToArray());
        }
        Assert.Throws<ObjectDisposedException>(()=>queued.Positions[0]);
        if(withNormals)Assert.Throws<ObjectDisposedException>(()=>queuedNormals[0]);
        long allocations=arena.ArrayAllocations;
        for(int tick=4;tick<104;tick++)using(var frame=arena.BeginFrame())Build(tick,frame);
        Assert.Equal(allocations,arena.ArrayAllocations);
        // An unchanged/paused pose after drain becomes detached once, then can
        // persist without keeping a frame lease or deforming on each display.
        EnbW3dDeformedGeometry paused;
        using(var frame=arena.BeginFrame())paused=Build(103,frame);
        Assert.IsNotType<EnbW3dFrameStream<Vector3>>(paused.Positions);
        using(var frame=arena.BeginFrame())Assert.Same(paused,Build(103,frame));
        Assert.Equal(saved,detached.Positions.ToArray());
        Assert.Equal(mesh.Positions.Count,paused.Positions.Count);
    }

    [Fact]
    public void LoadedNextSignedFractionUsesClassicOutsideRangeDefaultsAndInterpolation()
    {
        var animation=Animation(2,channels:[Channel(EnbW3dAnimationChannelKind.TranslationX,1,1,[10f,20f])]);
        Assert.True(EnbW3dAnimationSampler.TrySamplePivot(animation,1,-.75f,out var sample,out var diagnostic),diagnostic);
        // Floor(-.75)=-1; channel -1 is neutral, frame 0 is 10, fraction .25.
        Assert.Equal(2.5f,sample.Translation.X,5);
        Assert.True(sample.Visible);
    }

    [Fact]
    public void AnimationChannelsMayRunPastTheClipButAShortChannelFailsTheClip()
    {
        // 009C2EF0 does not compare channel frames with NumFrames; 009C6650
        // fails the whole load when a channel's declared samples are absent.
        byte[] longer = ContainerChunk(0x0200,
            LeafChunk(0x0201, AnimationHeader(0x0004_0001u, "LONG", "TREE", 2, 30)),
            LeafChunk(0x0202, AnimationChannel(0, 3, 1, (ushort)EnbW3dAnimationChannelKind.TranslationX, 0, 1f, 2f, 3f, 4f)),
            LeafChunk(0x0203, AnimationBitChannel(0, 3, (ushort)EnbW3dAnimationBitChannelKind.Visibility, 0, 1, [0x0F])));
        Assert.True(EnbW3dModel.TryDecode(longer, out EnbW3dModel model));
        Assert.Equal(3, Assert.Single(Assert.Single(model.Animations).Channels).LastFrame);

        byte[] shortChannel = ContainerChunk(0x0200,
            LeafChunk(0x0201, AnimationHeader(0x0004_0001u, "SHORT", "TREE", 2, 30)),
            LeafChunk(0x0202, AnimationChannel(2, 65535, 1, (ushort)EnbW3dAnimationChannelKind.TranslationX, 0, 1f)));
        Assert.False(EnbW3dModel.TryDecode(shortChannel, out _));

        // 009C67C0: bytes past the declared bit data are skipped by the chunk close.
        byte[] paddedBits = ContainerChunk(0x0200,
            LeafChunk(0x0201, AnimationHeader(0x0004_0001u, "PAD", "TREE", 2, 30)),
            LeafChunk(0x0203, AnimationBitChannel(0, 1, (ushort)EnbW3dAnimationBitChannelKind.Visibility, 0, 1, [0x03, 0xEE, 0xEE])));
        Assert.True(EnbW3dModel.TryDecode(paddedBits, out EnbW3dModel padded));
        Assert.Equal(new byte[] { 0x03 }, Assert.Single(Assert.Single(padded.Animations).BitChannels).PackedData.ToArray());
    }

    [Fact]
    public void R4DRenderCompilerAppliesLegacyAnimationChannelPivotShiftFromAnimationVersion()
    {
        byte[] animationBytes = ContainerChunk(
            0x0200,
            LeafChunk(0x0201, AnimationHeader(0x0002_FFFFu, "LEGACY.IDLE", "SHIP_TREE", 2, 30)),
            LeafChunk(0x0202, AnimationChannel(
                firstFrame: 0,
                lastFrame: 1,
                vectorLength: 1,
                flags: (ushort)EnbW3dAnimationChannelKind.TranslationX,
                pivotIndex: 0,
                data: [1f, 2f])),
            LeafChunk(0x0203, AnimationBitChannel(
                firstFrame: 0,
                lastFrame: 1,
                flags: (ushort)EnbW3dAnimationBitChannelKind.Visibility,
                pivotIndex: 0,
                defaultValue: 1,
                packedData: [0b0000_0011])));

        Assert.True(EnbW3dModel.TryDecode(animationBytes, out EnbW3dModel model));
        var source = new EnbResolvedW3dResource(
            "LEGACY.W3D",
            "LEGACY",
            new EnbMixResourceLocation("LEGACY.W3D", "legacy.mix", 10, animationBytes.Length),
            model,
            Array.Empty<EnbResolvedTextureReference>());

        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(source, out EnbW3dRenderResource resource, out string? diagnostic), diagnostic);
        EnbW3dRenderHierarchyAnimation animation = Assert.Single(resource.Animations);
        Assert.True(animation.HasLegacySyntheticRootChannelShift);

        EnbW3dRenderAnimationChannel channel = Assert.Single(animation.Channels);
        Assert.Equal((ushort)0, channel.SourcePivotIndex);
        Assert.Equal(1, channel.PivotIndex);

        EnbW3dRenderAnimationBitChannel bitChannel = Assert.Single(animation.BitChannels);
        Assert.Equal((ushort)0, bitChannel.SourcePivotIndex);
        Assert.Equal(1, bitChannel.PivotIndex);
    }

    [Fact]
    public void R4DSamplerInterpolatesTranslationWrapsNextFrameAndUsesShortestQuaternionPath()
    {
        EnbW3dRenderHierarchyAnimation animation = Animation(
            numFrames: 2,
            channels:
            [
                Channel(EnbW3dAnimationChannelKind.TranslationX, pivot: 1, vectorLength: 1, data: [0f, 10f]),
                Channel(EnbW3dAnimationChannelKind.Quaternion, pivot: 1, vectorLength: 4, data:
                [
                    0f, 0f, 0f, 1f,
                    0f, 0f, 0f, -1f,
                ]),
            ]);

        Assert.True(EnbW3dAnimationSampler.TrySamplePivot(animation, 1, 0.5f, out EnbW3dPivotAnimationSample middle, out string? diagnostic), diagnostic);
        Assert.Equal(5f, middle.Translation.X, 5);
        Assert.Equal(Quaternion.Identity.X, middle.Rotation.X, 5);
        Assert.Equal(Quaternion.Identity.Y, middle.Rotation.Y, 5);
        Assert.Equal(Quaternion.Identity.Z, middle.Rotation.Z, 5);
        Assert.Equal(Quaternion.Identity.W, middle.Rotation.W, 5);

        Assert.True(EnbW3dAnimationSampler.TrySamplePivot(animation, 1, 1.5f, out EnbW3dPivotAnimationSample wrapped, out diagnostic), diagnostic);
        Assert.Equal(5f, wrapped.Translation.X, 5); // frame 1 -> wrapped frame 0
    }

    [Fact]
    public void R4DSamplerUsesLsbFirstDiscreteVisibilityAndAuthoredDefaultOutsideRange()
    {
        EnbW3dRenderHierarchyAnimation animation = Animation(
            numFrames: 4,
            bitChannels:
            [
                new EnbW3dRenderAnimationBitChannel(
                    FirstFrame: 1,
                    LastFrame: 2,
                    Flags: 0,
                    KnownKind: EnbW3dAnimationBitChannelKind.Visibility,
                    SourcePivotIndex: 1,
                    PivotIndex: 1,
                    DefaultValue: 1,
                    PackedData: new byte[] { 0b0000_0001 }),
            ]);

        AssertSampleVisibility(animation, frame: 0.75f, expected: true);  // outside, authored default == 1
        AssertSampleVisibility(animation, frame: 1.75f, expected: true);  // relative bit 0
        AssertSampleVisibility(animation, frame: 2.75f, expected: false); // relative bit 1

        EnbW3dRenderHierarchyAnimation noVisibility = Animation(numFrames: 4);
        AssertSampleVisibility(noVisibility, frame: 2.75f, expected: true);
    }

    [Fact]
    public void R4DActivePivotSamplerIgnoresScalarRotationChannelsThreeThroughFive()
    {
        EnbW3dRenderHierarchyAnimation animation = Animation(
            numFrames: 2,
            channels:
            [
                Channel(EnbW3dAnimationChannelKind.RotationX, 1, 1, [100f, 200f]),
                Channel(EnbW3dAnimationChannelKind.RotationY, 1, 1, [300f, 400f]),
                Channel(EnbW3dAnimationChannelKind.RotationZ, 1, 1, [500f, 600f]),
            ]);

        Assert.True(EnbW3dAnimationSampler.TrySamplePivot(animation, 1, 0.5f, out EnbW3dPivotAnimationSample sample, out string? diagnostic), diagnostic);
        Assert.Equal(Quaternion.Identity, sample.Rotation);
    }

    [Fact]
    public void R4DHierarchyAnimationLayersLocalOffsetOverAuthoredPivotAndNeverAnimatesRuntimePivotZero()
    {
        EnbW3dRenderHierarchy hierarchy = Assert.Single(CompileR4Fixture().Hierarchies);
        EnbW3dRenderHierarchyAnimation animation = Animation(
            numFrames: 2,
            channels:
            [
                Channel(EnbW3dAnimationChannelKind.TranslationX, 0, 1, [1000f, 1000f]),
                Channel(EnbW3dAnimationChannelKind.TranslationX, 1, 1, [0f, 10f]),
            ]);
        Matrix4x4 objectWorld = Matrix4x4.CreateScale(2f) * Matrix4x4.CreateTranslation(100, 200, 300);

        Assert.True(EnbW3dHierarchyPose.TryEvaluate(hierarchy, objectWorld, animation, 0.5f, out EnbW3dHierarchyPoseOwner pose, out string? diagnostic), diagnostic);
        AssertVector(new Vector3(100, 200, 300), Translation(pose.WorldTransforms[0]));
        AssertVector(new Vector3(120, 250, 360), Translation(pose.WorldTransforms[1]));
    }

    [Fact]
    public void R4DHierarchyPoseDoesNotApplyPreservedPivotFixupMatrices()
    {
        var hierarchy = new EnbW3dRenderHierarchy(
            0x0003_0000u,
            "FIXUP_NEUTRAL",
            Vector3.Zero,
            false,
            new EnbW3dRenderPivot[]
            {
                new(0, "ROOT", uint.MaxValue, Vector3.Zero, Vector3.Zero, Quaternion.Identity, Matrix4x4.Identity),
                new(1, "CHILD", 0, Vector3.UnitX, Vector3.Zero, Quaternion.Identity, Matrix4x4.CreateTranslation(Vector3.UnitX)),
            },
            new EnbW3dRenderPivotFixup[]
            {
                new(99, 98, 97, 96, 95, 94, 93, 92, 91, 90, 89, 88),
            });

        Assert.True(EnbW3dHierarchyPose.TryEvaluate(hierarchy, Matrix4x4.Identity, out Matrix4x4[] pose, out string? diagnostic), diagnostic);
        AssertVector(Vector3.UnitX, Translation(pose[1]));
    }

    [Fact]
    public void R4DSkinDeformerUsesOneEffectiveU16PivotIndexPerVertexAndIgnoresTranslationForNormals()
    {
        EnbW3dRenderMesh mesh = MinimalSkinMesh(
            positions: [Vector3.UnitX, Vector3.UnitY],
            normals: [Vector3.UnitX, Vector3.UnitY],
            boneIndices: [0, 1]);
        var pose = new EnbW3dHierarchyPoseOwner(
            [
                Matrix4x4.CreateTranslation(10, 20, 30),
                Matrix4x4.CreateScale(2f) * Matrix4x4.CreateTranslation(100, 200, 300),
            ],
            [true, true],
            null,
            null);

        Assert.True(EnbW3dSkinDeformer.TryDeform(mesh, pose, out EnbW3dDeformedGeometry geometry, out string? diagnostic), diagnostic);
        AssertVector(new Vector3(11, 20, 30), geometry.Positions[0]); // effective runtime bone index 0 stays 0
        AssertVector(new Vector3(100, 202, 300), geometry.Positions[1]);
        AssertVector(Vector3.UnitX, geometry.Normals[0]);
        AssertVector(new Vector3(0, 2, 0), geometry.Normals[1]);
    }

    [Fact]
    public void R4DDrawCompilerUsesMeshInfluenceStateForIdentityWorldDeformationRegardlessOfHmodelNodeKind()
    {
        EnbW3dRenderResource source = CompileR4Fixture();
        EnbW3dRenderMesh mesh = (Assert.Single(source.Meshes)).Copy().SetBoneIndices(new ushort[] { 0, 1, 1 });
        EnbW3dRenderHierarchyModel originalModel = Assert.Single(source.HierarchyModels);
        EnbW3dRenderHierarchyModel influencedModel = originalModel with
        {
            // Current retail's mesh flag 0x400, not the HMODEL connection
            // chunk kind, selects the deformation/final-submit path. Keep this
            // as an ordinary 0x0302-style RenderObject binding on purpose.
            Nodes = new EnbW3dRenderHierarchyModelNode[]
            {
                new(0, EnbW3dRenderHierarchyNodeKind.RenderObject, mesh.Name, 1),
            },
        };
        EnbW3dRenderResource resource = source.Copy().SetMeshes(new[] { mesh }).SetHierarchyModels(new[] { influencedModel });

        Assert.True(EnbW3dDrawPacketCompiler.TryBuild(
            SceneObject(scale: 2f),
            resource,
            frameTimeMilliseconds: 1234,
            renderPass: EnbRenderPassKind.OrdinaryWorld,
            hlodSelection: null,
            lightEnvironment: EnbW3dLightEnvironment.BlackNoLights,
            animationSample: null,
            out EnbW3dDrawInvocation drawList,
            out string? diagnostic), diagnostic);

        EnbW3dDrawTask packet = Assert.Single(drawList.Packets);
        Assert.Equal(Matrix4x4.Identity, packet.WorldTransform);
        EnbW3dDeformedGeometry deformed = packet.DeformedGeometry
            ?? throw new Xunit.Sdk.XunitException("Expected current-retail influenced-mesh draw geometry.");
        AssertVector(new Vector3(102, 200, 300), deformed.Positions[0]);
        AssertVector(new Vector3(118, 240, 360), deformed.Positions[1]);
        AssertVector(new Vector3(-2, 0, 0), deformed.Normals[1]);
        // HMODEL keeps the influenced render object bound to its node pivot
        // for visibility/culling even though the deformed draw uses identity world.
        AssertVector(new Vector3(120, 240, 360), packet.Bounds.Center);
        Assert.Equal(11f, packet.Bounds.Radius, 4);
    }

    [Fact]
    public void R4DDrawCompilerUsesHmodelNodePivotVisibilityForInfluencedMeshBindings()
    {
        EnbW3dRenderResource source = CompileR4Fixture();
        EnbW3dRenderMesh mesh = (Assert.Single(source.Meshes)).Copy().SetBoneIndices(new ushort[] { 0, 1, 1 });
        EnbW3dRenderHierarchyModel originalModel = Assert.Single(source.HierarchyModels);
        EnbW3dRenderHierarchyModel skinModel = originalModel with
        {
            Nodes = new EnbW3dRenderHierarchyModelNode[]
            {
                new(0, EnbW3dRenderHierarchyNodeKind.Skin, mesh.Name, 1),
            },
        };
        EnbW3dRenderHierarchyAnimation hidden = Animation(
            numFrames: 2,
            name: "SHIP.HIDDEN",
            hierarchyName: "SHIP_TREE",
            bitChannels:
            [
                new EnbW3dRenderAnimationBitChannel(
                    FirstFrame: 0,
                    LastFrame: 1,
                    Flags: 0,
                    KnownKind: EnbW3dAnimationBitChannelKind.Visibility,
                    SourcePivotIndex: 1,
                    PivotIndex: 1,
                    DefaultValue: 1,
                    PackedData: new byte[] { 0b0000_0000 }),
            ]);
        EnbW3dRenderResource resource = source.Copy().SetMeshes(new[] { mesh }).SetHierarchyModels(new[] { skinModel }).SetAnimations(new[] { hidden });

        Assert.True(EnbW3dDrawPacketCompiler.TryBuild(
            SceneObject(scale: 2f),
            resource,
            frameTimeMilliseconds: 1234,
            renderPass: EnbRenderPassKind.OrdinaryWorld,
            hlodSelection: new EnbW3dHlodSelection("SHIPROOT", 0),
            lightEnvironment: EnbW3dLightEnvironment.BlackNoLights,
            animationSample: new EnbW3dAnimationSample("SHIP.HIDDEN", 0f),
            out EnbW3dDrawInvocation drawList,
            out string? diagnostic), diagnostic);

        Assert.Empty(drawList.Packets);
    }

    [Fact]
    public void R4DHmodelSkinConnectionWithoutMeshInfluencesRemainsRigid()
    {
        EnbW3dRenderResource source = CompileR4Fixture();
        EnbW3dRenderMesh mesh = Assert.Single(source.Meshes);
        Assert.Empty(mesh.BoneIndices);

        EnbW3dRenderHierarchyModel originalModel = Assert.Single(source.HierarchyModels);
        EnbW3dRenderHierarchyModel skinConnectionModel = originalModel with
        {
            Nodes = new EnbW3dRenderHierarchyModelNode[]
            {
                new(0, EnbW3dRenderHierarchyNodeKind.Skin, mesh.Name, 1),
            },
        };
        EnbW3dRenderResource resource = source.Copy().SetHierarchyModels(new[] { skinConnectionModel });

        Assert.True(EnbW3dDrawPacketCompiler.TryBuild(
            SceneObject(scale: 2f),
            resource,
            frameTimeMilliseconds: 1234,
            renderPass: EnbRenderPassKind.OrdinaryWorld,
            hlodSelection: null,
            lightEnvironment: EnbW3dLightEnvironment.BlackNoLights,
            animationSample: null,
            out EnbW3dDrawInvocation drawList,
            out string? diagnostic), diagnostic);

        EnbW3dDrawTask packet = Assert.Single(drawList.Packets);
        Assert.Null(packet.DeformedGeometry);
        AssertVector(new Vector3(120, 240, 360), Translation(packet.WorldTransform));
    }

    [Fact]
    public void R9WDrawCompilerAppliesFinalRecordOverrideBeforeBoundMeshSubmission()
    {
        EnbW3dRenderResource resource = CompileR4Fixture();
        Assert.True(EnbW3dDrawPacketCompiler.TryBuild(
            SceneObject(scale: 2f),
            resource,
            frameTimeMilliseconds: 1234,
            renderPass: EnbRenderPassKind.OrdinaryWorld,
            hlodSelection: new EnbW3dHlodSelection("SHIPROOT", 0),
            lightEnvironment: EnbW3dLightEnvironment.BlackNoLights,
            animationBlend: Array.Empty<EnbW3dWeightedAnimationSample>(),
            out EnbW3dDrawInvocation baselineDrawList,
            out string? diagnostic), diagnostic);
        EnbW3dDrawTask baseline = Assert.Single(baselineDrawList.Packets);

        var postTransforms = new EnbW3dHierarchyState(
            "SHIP_TREE",
            Matrix4x4.CreateTranslation(100, 200, 300),
            RenderObjectScale: 2f,
            Transforms: new Dictionary<int, Matrix4x4>
            {
                [1] = Matrix4x4.CreateTranslation(0, 7, 0),
            });

        Assert.True(EnbW3dDrawPacketCompiler.TryBuild(
            SceneObject(scale: 2f),
            resource,
            frameTimeMilliseconds: 1234,
            renderPass: EnbRenderPassKind.OrdinaryWorld,
            hlodSelection: new EnbW3dHlodSelection("SHIPROOT", 0),
            lightEnvironment: EnbW3dLightEnvironment.BlackNoLights,
            animationBlend: Array.Empty<EnbW3dWeightedAnimationSample>(),
            hierarchyPostTransforms: postTransforms,
            out EnbW3dDrawInvocation drawList,
            out diagnostic), diagnostic);

        EnbW3dDrawTask packet = Assert.Single(drawList.Packets);
        Matrix4x4 expectedWorld = Matrix4x4.CreateScale(2f) *
                                  Matrix4x4.CreateTranslation(0, 7, 0) *
                                  Matrix4x4.CreateScale(0.5f) *
                                  baseline.WorldTransform;
        Assert.Equal(expectedWorld, packet.WorldTransform);
        AssertVector(Vector3.Transform(packet.Mesh.BoundingSphereCenter, expectedWorld), packet.Bounds.Center);
    }

    [Fact]
    public void R9XSimpleHlodMeshUsesMemberReferenceRecordAndFinalOverride()
    {
        EnbW3dRenderResource source = CompileR4Fixture();
        EnbW3dRenderHlod sourceHlod = Assert.Single(source.HierarchicalLods);
        EnbW3dRenderHlodLevel sourceLevel = Assert.Single(sourceHlod.Levels);
        EnbW3dRenderHlod hlod = sourceHlod with
        {
            Levels =
            [
                sourceLevel with
                {
                    Members = [new EnbW3dRenderHlodMember(1, "SHIPMESH")],
                },
            ],
        };
        EnbW3dRenderResource resource = source.Copy().SetHierarchyModels(Array.Empty<EnbW3dRenderHierarchyModel>()).SetHierarchicalLods([hlod]);

        Assert.True(EnbW3dDrawPacketCompiler.TryBuild(
            SceneObject(scale: 2f),
            resource,
            frameTimeMilliseconds: 1234,
            renderPass: EnbRenderPassKind.OrdinaryWorld,
            hlodSelection: new EnbW3dHlodSelection("SHIPROOT", 0),
            lightEnvironment: EnbW3dLightEnvironment.BlackNoLights,
            animationBlend: Array.Empty<EnbW3dWeightedAnimationSample>(),
            out EnbW3dDrawInvocation baselineDrawList,
            out string? diagnostic), diagnostic);

        EnbW3dDrawTask baseline = Assert.Single(baselineDrawList.Packets);
        Assert.Equal("SHIP_TREE", baseline.HierarchyName);
        Assert.Equal(1, baseline.PivotIndex);
        Assert.Equal("SHIPMESH", baseline.RenderObjectName);
        AssertVector(new Vector3(120, 240, 360), Translation(baseline.WorldTransform));

        var postTransforms = new EnbW3dHierarchyState(
            "SHIP_TREE",
            Matrix4x4.CreateTranslation(100, 200, 300),
            RenderObjectScale: 2f,
            Transforms: new Dictionary<int, Matrix4x4>
            {
                [1] = Matrix4x4.CreateTranslation(0, 7, 0),
            });

        Assert.True(EnbW3dDrawPacketCompiler.TryBuild(
            SceneObject(scale: 2f),
            resource,
            frameTimeMilliseconds: 1234,
            renderPass: EnbRenderPassKind.OrdinaryWorld,
            hlodSelection: new EnbW3dHlodSelection("SHIPROOT", 0),
            lightEnvironment: EnbW3dLightEnvironment.BlackNoLights,
            animationBlend: Array.Empty<EnbW3dWeightedAnimationSample>(),
            hierarchyPostTransforms: postTransforms,
            out EnbW3dDrawInvocation drawList,
            out diagnostic), diagnostic);

        EnbW3dDrawTask packet = Assert.Single(drawList.Packets);
        Matrix4x4 expectedWorld = Matrix4x4.CreateScale(2f) *
                                  Matrix4x4.CreateTranslation(0, 7, 0) *
                                  Matrix4x4.CreateScale(0.5f) *
                                  baseline.WorldTransform;
        Assert.Equal(expectedWorld, packet.WorldTransform);
        AssertVector(Vector3.Transform(packet.Mesh.BoundingSphereCenter, expectedWorld), packet.Bounds.Center);
    }

    [Fact]
    public void R4DDrawCompilerConsumesExplicitAnimationWithoutChoosingAnimationOrLodPolicy()
    {
        EnbW3dRenderResource source = CompileR4Fixture();
        EnbW3dRenderHierarchyAnimation animation = Animation(
            numFrames: 2,
            name: "SHIP.IDLE",
            hierarchyName: "SHIP_TREE",
            channels:
            [
                Channel(EnbW3dAnimationChannelKind.TranslationX, 1, 1, [0f, 10f]),
            ]);
        EnbW3dRenderResource resource = source.Copy().SetAnimations(new[] { animation });

        Assert.True(EnbW3dDrawPacketCompiler.TryBuild(
            SceneObject(scale: 2f),
            resource,
            frameTimeMilliseconds: 999,
            renderPass: EnbRenderPassKind.OrdinaryWorld,
            hlodSelection: null,
            lightEnvironment: EnbW3dLightEnvironment.BlackNoLights,
            animationSample: new EnbW3dAnimationSample("SHIP.IDLE", 0.5f),
            out EnbW3dDrawInvocation drawList,
            out string? diagnostic), diagnostic);

        EnbW3dDrawTask packet = Assert.Single(drawList.Packets);
        Assert.Null(packet.DeformedGeometry);
        AssertVector(new Vector3(120, 250, 360), Translation(packet.WorldTransform));
        Assert.Equal(new EnbW3dAnimationSample("SHIP.IDLE", 0.5f), drawList.AnimationSample);

        Assert.False(EnbW3dDrawPacketCompiler.TryBuild(
            SceneObject(scale: 2f),
            resource,
            frameTimeMilliseconds: 999,
            renderPass: EnbRenderPassKind.OrdinaryWorld,
            hlodSelection: null,
            lightEnvironment: EnbW3dLightEnvironment.BlackNoLights,
            animationSample: new EnbW3dAnimationSample("NOT_A_REAL_ANIMATION", 0f),
            out _,
            out diagnostic));
        Assert.Contains("was not found", diagnostic ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void R4DAnimationAndDeformationLayerRemainsBackendIndependent()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(root, "src", "EnbNext.Rendering", "EnbW3dAnimationRuntime.cs"));
        string drawPackets = File.ReadAllText(Path.Combine(root, "src", "EnbNext.Rendering", "EnbW3dDrawPackets.cs"));

        Assert.Contains("EnbW3dAnimationSample", drawPackets, StringComparison.Ordinal);
        Assert.Contains("EnbW3dSkinDeformer", drawPackets, StringComparison.Ordinal);
        Assert.DoesNotContain("cameraDistance", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("projectedDetail", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Avalonia", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Veldrid", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Silk.NET", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Direct3D", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OpenGL", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Vulkan", source, StringComparison.OrdinalIgnoreCase);
    }

    private static EnbW3dRenderResource CompileR4Fixture()
    {
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(
            EnbR4W3dRenderResourceTests.BuildResolvedResource(shaderId: 0),
            out EnbW3dRenderResource resource,
            out string? diagnostic), diagnostic);
        return resource;
    }

    private static EnbW3dRenderHierarchyAnimation Animation(
        uint numFrames,
        string name = "TEST.ANIM",
        string hierarchyName = "SHIP_TREE",
        IReadOnlyList<EnbW3dRenderAnimationChannel>? channels = null,
        IReadOnlyList<EnbW3dRenderAnimationBitChannel>? bitChannels = null)
        => new(
            Ordinal: 0,
            Version: 0x0003_0000u,
            Name: name,
            HierarchyName: hierarchyName,
            NumFrames: numFrames,
            FrameRate: 30,
            HasLegacySyntheticRootChannelShift: false,
            Channels: channels ?? Array.Empty<EnbW3dRenderAnimationChannel>(),
            BitChannels: bitChannels ?? Array.Empty<EnbW3dRenderAnimationBitChannel>());

    private static EnbW3dRenderAnimationChannel Channel(
        EnbW3dAnimationChannelKind kind,
        int pivot,
        ushort vectorLength,
        IReadOnlyList<float> data)
        => new(
            FirstFrame: 0,
            LastFrame: 1,
            VectorLength: vectorLength,
            Flags: (ushort)kind,
            KnownKind: kind,
            SourcePivotIndex: checked((ushort)pivot),
            PivotIndex: pivot,
            Data: data);

    private static EnbW3dRenderMesh MinimalSkinMesh(
        IReadOnlyList<Vector3> positions,
        IReadOnlyList<Vector3> normals,
        IReadOnlyList<ushort> boneIndices)
        => new(
            Ordinal: 0,
            Name: "SKIN",
            ContainerName: string.Empty,
            BoundingRadius: 10f,
            Positions: positions,
            Normals: normals,
            BoneIndices: boneIndices,
            Indices: Array.Empty<uint>(),
            TriangleMetadata: Array.Empty<EnbW3dRenderTriangleMetadata>(),
            VertexMaterials: Array.Empty<EnbW3dRenderVertexMaterial>(),
            MaterialPasses: Array.Empty<EnbW3dRenderMaterialPass>());

    private static void AssertSampleVisibility(EnbW3dRenderHierarchyAnimation animation, float frame, bool expected)
    {
        Assert.True(EnbW3dAnimationSampler.TrySamplePivot(animation, 1, frame, out EnbW3dPivotAnimationSample sample, out string? diagnostic), diagnostic);
        Assert.Equal(expected, sample.Visible);
    }

    private static EnbSceneObjectSnapshot SceneObject(float scale)
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
            Name: "R4D fixture",
            Radar: new EnbSceneRadarSnapshot(false, 0, null),
            Spatial: new EnbSceneSpatialSnapshot(
                EnbSceneSpatialProviderKind.Fixed,
                new EnbSceneTransformSnapshot(
                    ClientTime: 100,
                    Position: new Vector3(100, 200, 300),
                    Orientation: Quaternion.Identity,
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

    private static Vector3 Translation(Matrix4x4 matrix) => new(matrix.M41, matrix.M42, matrix.M43);

    private static void AssertVector(Vector3 expected, Vector3 actual)
    {
        Assert.Equal(expected.X, actual.X, 4);
        Assert.Equal(expected.Y, actual.Y, 4);
        Assert.Equal(expected.Z, actual.Z, 4);
    }

    private static byte[] AnimationHeader(uint version, string name, string hierarchyName, uint numFrames, uint frameRate)
    {
        byte[] payload = new byte[44];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), version);
        WriteFixedAscii(payload.AsSpan(4, 16), name);
        WriteFixedAscii(payload.AsSpan(20, 16), hierarchyName);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(36, 4), numFrames);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(40, 4), frameRate);
        return payload;
    }

    private static byte[] AnimationChannel(
        ushort firstFrame,
        ushort lastFrame,
        ushort vectorLength,
        ushort flags,
        ushort pivotIndex,
        params float[] data)
    {
        byte[] payload = new byte[12 + (data.Length * 4)];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), firstFrame);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2, 2), lastFrame);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4, 2), vectorLength);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(6, 2), flags);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8, 2), pivotIndex);
        for (int index = 0; index < data.Length; index++)
            WriteSingle(payload.AsSpan(12 + (index * 4), 4), data[index]);
        return payload;
    }

    private static byte[] AnimationBitChannel(
        ushort firstFrame,
        ushort lastFrame,
        ushort flags,
        ushort pivotIndex,
        byte defaultValue,
        byte[] packedData)
    {
        byte[] payload = new byte[9 + packedData.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), firstFrame);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2, 2), lastFrame);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4, 2), flags);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(6, 2), pivotIndex);
        payload[8] = defaultValue;
        packedData.CopyTo(payload, 9);
        return payload;
    }

    private static byte[] LeafChunk(uint type, byte[] payload)
    {
        byte[] bytes = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), type);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), (uint)payload.Length);
        payload.CopyTo(bytes, 8);
        return bytes;
    }

    private static byte[] ContainerChunk(uint type, params byte[][] children)
    {
        byte[] payload = Concat(children);
        byte[] bytes = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), type);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), (uint)payload.Length | 0x8000_0000u);
        payload.CopyTo(bytes, 8);
        return bytes;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        byte[] result = new byte[parts.Sum(static part => part.Length)];
        int offset = 0;
        foreach (byte[] part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }
        return result;
    }

    private static void WriteFixedAscii(Span<byte> destination, string value)
    {
        destination.Clear();
        byte[] bytes = Encoding.ASCII.GetBytes(value);
        bytes.AsSpan(0, Math.Min(bytes.Length, destination.Length - 1)).CopyTo(destination);
    }

    private static void WriteSingle(Span<byte> destination, float value)
        => BinaryPrimitives.WriteInt32LittleEndian(destination, BitConverter.SingleToInt32Bits(value));

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EnbNext.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate EnbNext repository root from test output directory.");
    }
}
