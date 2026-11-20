using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Text;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;
using EnbNext.Rendering.OpenGL;

namespace EnbNext.Net7.Tests;

public sealed class EnbR6Z6CurrentRetailParticleBufferTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OrdinaryHlodEmitterDoesNotRequireAPackageTexture(bool empty)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        Assert.True(EnbCurrentRetailParticleEmitterDecoder.TryDecode(BuildPweapExhaustDefinition(),out var definition,out var error),error);
        definition=definition.Copy().SetTextureFileName(empty?"":"missing-world-emitter-683.tga");
        var prepared=new EnbCurrentRetailRObjParticleAsset(default,null,definition).PrepareTexture(assets);
        var resource=CreateEmitterResource(definition);
        resource=resource.Copy().SetExternalRenderObjects([resource.ExternalRenderObjects[0] with
            {ParticleTextureLocation=null,ParticleRetainedTexture=prepared.RetainedTexture}]);
        Assert.True(EnbW3dDrawPacketCompiler.TryBuild(CreateSceneObject(),resource,401,EnbRenderPassKind.OrdinaryWorld,
            new("ROOT",0),EnbW3dLightEnvironment.BlackNoLights,out var list,out error),error);
        var instance=Assert.Single(list.ParticleEmitters);
        Assert.Same(prepared.RetainedTexture,instance.RetainedTexture);
        Assert.True(EnbResolvedRenderCamera.TryCreate(new(2000,0,0),Quaternion.Identity,new(640,480,1),Math.PI/3,new(1,10000),out var camera));
        Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(list.ResourceKey,list.RenderIdentity,list.VisualGeneration,
            instance,camera,EnbW3dLightEnvironment.BlackNoLights,0,0,401,out var packet,out var receipt,out error),error);
        Assert.NotNull(packet);Assert.Equal(2,receipt.LiveParticleCount);
        var bindings=packet.Material.Source.TextureStages.SelectMany(s=>s.Textures).ToArray();
        if(empty)Assert.Empty(bindings);
        else
        {
            Assert.NotEmpty(bindings);
            Assert.All(bindings,b=>Assert.Equal(EnbTextureFormat.CurrentRetailMissingArgb8,b.CurrentRetailResolvedTexture!.Image!.Format));
        }
    }

    [Theory]
    [InlineData("GE_EN_buff01p1.w3d", 1u, .1f)]
    [InlineData("GE_EN_buff01p2.w3d", 3u, .212f)]
    public void InstalledEnergyStreakDefinitionsRetainAuthoredData(string name, uint randomizer, float lifetime)
    {
        var paths = EnbInstalledAssetLocator.TryLocate();
        if (paths is null) return;
        var catalog = EnbMixResourceCatalog.Load(paths.MixFilesDirectory)!;
        Assert.True(catalog.TryReadResource(name, out var bytes, out _));
        Assert.True(EnbCurrentRetailParticleEmitterDecoder.TryDecode(bytes, out var d, out var error), error);
        Assert.Equal(4u, d.RenderMode);
        Assert.Equal(randomizer, d.CreationRandomizer.ClassId);
        Assert.Equal(lifetime, d.LifetimeSeconds);
        Assert.Equal(2u, d.BurstSize);
        Assert.Equal(100f, d.EmissionRatePerSecond);
        Assert.Equal(1f, d.VelocityInheritanceFactor);
        Assert.Equal("", d.TextureFileName);
        Assert.Equal(16, d.LinePropertyWords.Length);
        Assert.Equal(0x3f800000u, d.LinePropertyWords[4]);
        Assert.NotNull(d.BlurTime);
        Assert.Equal(2, d.BlurTime.Keys.Count);
        if (randomizer == 3)
        {
            Assert.Equal(new float[] { 0, 0, .073f, .138f }, d.Color.Keys.Select(k => k.TimeSeconds));
            Assert.Equal(new EnbCurrentRetailParticleColorKey(0, 255, 255, 255, 0), d.Color.Keys[0]);
            Assert.Equal(new EnbCurrentRetailParticleColorKey(0, 50, 72, 250, 0), d.Color.Keys[1]);
            Assert.Equal(new EnbCurrentRetailParticleScalarKey(.084f, .025f), d.BlurTime.Keys[1]);
        }
        Assert.Throws<InvalidDataException>(() => EnbCurrentRetailRObjParticleAsset.ValidateFinite(d));
    }

    [Fact]
    public void PacketLifetimeAndTerminalColorSlopeUseNativeTruncation()
    {
        Assert.True(EnbCurrentRetailParticleEmitterDecoder.TryDecode(BuildPweapExhaustDefinition(),out var d,out _));
        d=d.Copy().SetLifetimeSeconds(.073f).SetColor(new(Vector3.Zero,[new(0,0,0,0,0),new(.073f,255,255,255,0),new(.073f,255,0,0,0)]));
        Assert.True(EnbResolvedRenderCamera.TryCreate(new(2000,0,0),Quaternion.Identity,
            new(640,480,1),Math.PI/3,new(1,10000),out var camera));
        var instance=new EnbW3dParticleEmitterDrawInstance("test","test.w3d",1,
            EnbRenderPassKind.OrdinaryWorld,d,new("test.dds","fixture.mix",0,1),Matrix4x4.Identity,"",0,0);
        foreach(long age in new long[]{71,72,73})
        {
            Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(new("test.w3d","fixture.mix",0,1),
                1,1,instance,camera,EnbW3dLightEnvironment.BlackNoLights,0,0,age,out var packet,out var receipt,out var error,
                worldBirths:[new(0,0,Matrix4x4.Identity)]),error);
            Assert.Equal(age<73?1:0,receipt.LiveParticleCount);
            if(age==73)Assert.Null(packet);
            else
            {
                Assert.NotNull(packet);
                Assert.All(packet.Mesh.EffectiveColor1,c=>{Assert.Equal(c.R,c.G);Assert.Equal(c.G,c.B);Assert.Equal(age==71?248:251,c.R);});
            }
        }
    }

    [Fact]
    public void CoincidentColorKeysDecodeWithoutMergingAndProduceFinitePacket()
    {
        byte[] bytes = BuildPweapExhaustDefinition();
        int properties = FixtureChunkPayload(bytes, 0x0505);
        WriteSingle(bytes, properties + 48, 0f); // second colour key, retain its distinct colour
        Assert.True(EnbCurrentRetailParticleEmitterDecoder.TryDecode(bytes, out var d, out var error), error);
        Assert.Equal(4, d.Color.Keys.Count);
        Assert.Equal(0, d.Color.Keys[1].TimeSeconds);
        Assert.NotEqual(d.Color.Keys[0].R, d.Color.Keys[1].R);
        Assert.True(EnbResolvedRenderCamera.TryCreate(new(2000,0,0), Quaternion.Identity,
            new(640,480,1), Math.PI/3, new(1,10000), out var camera));
        var instance = new EnbW3dParticleEmitterDrawInstance("test", "test.w3d", 1,
            EnbRenderPassKind.OrdinaryWorld, d, new("test.dds","fixture.mix",0,1), Matrix4x4.Identity,"",0,0);
        Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(new("test.w3d","fixture.mix",0,1),
            1,1,instance,camera,EnbW3dLightEnvironment.BlackNoLights,0,0,401,out var packet,out _,out error), error);
        Assert.NotNull(packet);
        WriteSingle(bytes, properties + 56, -1); // still reject negative/decreasing keys
        Assert.False(EnbCurrentRetailParticleEmitterDecoder.TryDecode(bytes, out _, out _));
    }

    [Fact]
    public void BlurCurveUsesTwelveByteHeaderAndRetainsLineWords()
    {
        byte[] blur = new byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(blur, 1);
        WriteSingle(blur, 4, .2f);
        BinaryPrimitives.WriteUInt32LittleEndian(blur.AsSpan(8), 3); // reserved, not initial time
        WriteSingle(blur, 20, .084f);
        WriteSingle(blur, 24, .025f);
        byte[] line = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(line.AsSpan(60), 0x12345678);
        var original = BuildPweapExhaustDefinition();
        Assert.True(EnbCurrentRetailParticleEmitterDecoder.TryDecode(original, out var absent, out _));
        Assert.Null(absent.BlurTime);
        Assert.Equal(new uint[16], absent.LinePropertyWords.ToArray());
        var explicitZero = ContainerChunk(0x0500, original[8..], LeafChunk(0x0509, new byte[64]));
        Assert.True(EnbCurrentRetailParticleEmitterDecoder.TryDecode(explicitZero, out var zero, out _));
        Assert.Equal(zero.LinePropertyWords.ToArray(), absent.LinePropertyWords.ToArray());
        var bytes = ContainerChunk(0x0500, original[8..], LeafChunk(0x050C, blur), LeafChunk(0x0509, line));
        Assert.True(EnbCurrentRetailParticleEmitterDecoder.TryDecode(bytes, out var d, out var error), error);
        Assert.Equal(.2f, d.BlurTime!.Random);
        Assert.Equal(new EnbCurrentRetailParticleScalarKey[] { new(0,0), new(.084f,.025f) }, d.BlurTime.Keys);
        Assert.Equal(0x12345678u, d.LinePropertyWords[15]);
    }

    [Theory]
    [InlineData(0x0509u, 0)]
    [InlineData(0x0509u, 63)]
    [InlineData(0x050Cu, 0)]
    [InlineData(0x050Cu, 11)]
    public void MalformedPreviouslyIgnoredParticleChunksAreRejected(uint type, int length)
    {
        var bytes = ContainerChunk(0x0500, BuildPweapExhaustDefinition()[8..], LeafChunk(type, new byte[length]));
        Assert.False(EnbCurrentRetailParticleEmitterDecoder.TryDecode(bytes, out _, out var error));
        Assert.Contains($"0x{type:X4}", error);
    }

    [Theory]
    [InlineData(0x0509u,65)]
    [InlineData(0x050Cu,19)] [InlineData(0x050Cu,21)] [InlineData(0x050Cu,28)]
    public void OptionalParticleReadersIgnoreUnreadTails(uint type,int length)
    {
        var bytes=ContainerChunk(0x0500,BuildPweapExhaustDefinition()[8..],LeafChunk(type,new byte[length]));
        Assert.True(EnbCurrentRetailParticleEmitterDecoder.TryDecode(bytes,out var d,out var error),error);
        if(type==0x509)Assert.Equal(new uint[16],d.LinePropertyWords.ToArray());
        else Assert.Equal(new EnbCurrentRetailParticleScalarKey(0,0),Assert.Single(d.BlurTime!.Keys));
    }

    private static int FixtureChunkPayload(byte[] bytes, uint type)
    {
        for (int offset = 8; offset < bytes.Length;)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset)) == type) return offset + 8;
            offset += 8 + checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4)));
        }
        throw new InvalidOperationException("Missing fixture chunk");
    }

    [Fact]
    public void InstalledPweapExhaustMatchesClosedDefinitionWhenRetailDataIsAvailable()
    {
        EnbInstalledAssetPaths? paths = EnbInstalledAssetLocator.TryLocate();
        if (paths is null)
            return;

        EnbMixResourceCatalog catalog = EnbMixResourceCatalog.Load(paths.MixFilesDirectory)
            ?? throw new Xunit.Sdk.XunitException("Installed MIX resource catalog was not loaded.");
        Assert.True(catalog.TryReadResource(
            "PWEAPEXHAUST.W3D",
            out byte[] bytes,
            out EnbMixResourceLocation location));
        Assert.Equal("mixfile_art_17.mix", location.ArchiveName, ignoreCase: true);
        Assert.True(EnbCurrentRetailParticleEmitterDecoder.TryDecode(
            bytes,
            out EnbCurrentRetailParticleEmitterDefinition definition,
            out string? diagnostic), diagnostic);
        Assert.Equal("PWEAPEXHAUST", definition.Name, ignoreCase: true);
        Assert.Equal("smoke_jlt.tga", definition.TextureFileName, ignoreCase: true);
        Assert.Equal(8f, definition.LifetimeSeconds);
        Assert.Equal(5f, definition.EmissionRatePerSecond);
        Assert.Equal(new Vector3(125f, 0f, 125f), definition.VelocityPerSecond);
        Assert.True(catalog.TryLocateResource(
            definition.TextureFileName,
            out EnbMixResourceLocation textureLocation));
        Assert.Equal("smoke_jlt.dds", textureLocation.Name, ignoreCase: true);
    }

    [Fact]
    public void PweapExhaustDefinitionDecodesExactCurrentRetailFields()
    {
        Assert.True(EnbCurrentRetailParticleEmitterDecoder.TryDecode(
            BuildPweapExhaustDefinition(),
            out EnbCurrentRetailParticleEmitterDefinition definition,
            out string? diagnostic), diagnostic);

        Assert.Equal(0x0002_0000u, definition.Version);
        Assert.Equal("PWEAPEXHAUST", definition.Name);
        Assert.Equal("smoke_jlt.tga", definition.TextureFileName);
        Assert.Equal(8f, definition.LifetimeSeconds);
        Assert.Equal(5f, definition.EmissionRatePerSecond);
        Assert.Equal(1u, definition.BurstSize);
        Assert.Equal(new Vector3(125f, 0f, 125f), definition.VelocityPerSecond);
        Assert.Equal(0u, definition.RenderMode);
        Assert.Equal(0u, definition.FrameMode);
        Assert.Equal(4, definition.Color.Keys.Count);
        Assert.Equal(4, definition.Size.Keys.Count);
        Assert.Equal(700f, definition.Size.Keys[^1].Value);
        Assert.Equal(0.1f, definition.Rotation.Keys[^1].Value);
        Assert.Equal(90f, definition.InitialOrientationRandom);
    }

    [Fact]
    public void SelectedEmitterProducesDeferredPrelitTexturedTriPacketAfterRetailIntervals()
    {
        Assert.True(EnbCurrentRetailParticleEmitterDecoder.TryDecode(
            BuildPweapExhaustDefinition(),
            out EnbCurrentRetailParticleEmitterDefinition definition,
            out string? diagnostic), diagnostic);
        EnbW3dRenderResource resource = CreateEmitterResource(definition);
        Assert.True(EnbW3dDrawPacketCompiler.TryBuild(
            CreateSceneObject(),
            resource,
            frameTimeMilliseconds: 401,
            renderPass: EnbRenderPassKind.OrdinaryWorld,
            hlodSelection: new EnbW3dHlodSelection("ROOT", 0),
            lightEnvironment: EnbW3dLightEnvironment.BlackNoLights,
            out EnbW3dDrawInvocation drawList,
            out diagnostic), diagnostic);
        Assert.Empty(drawList.Packets);
        EnbW3dParticleEmitterDrawInstance instance = Assert.Single(drawList.ParticleEmitters);

        Assert.True(EnbResolvedRenderCamera.TryCreate(
            new Vector3(2000f, 0f, 0f),
            Quaternion.Identity,
            new EnbRenderViewport(640, 480, 1.0),
            Math.PI / 3.0,
            new EnbRenderClipRange(1f, 10000f),
            out EnbResolvedRenderCamera camera));
        Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(
            drawList.ResourceKey,
            drawList.RenderIdentity,
            drawList.VisualGeneration,
            instance,
            camera,
            EnbW3dLightEnvironment.BlackNoLights,
            randomSeedSalt: 0,
            startTimeMilliseconds: 0,
            frameTimeMilliseconds: 401,
            out EnbW3dDrawTask? packet,
            out EnbCurrentRetailParticleEmitterReceipt receipt,
            out diagnostic), diagnostic);

        Assert.NotNull(packet);
        Assert.Equal(2, receipt.EmittedParticleCount);
        Assert.Equal(2, receipt.LiveParticleCount);
        Assert.Equal(15, packet.OrderKey.StaticSort.RuntimeLevel);
        Assert.Equal(EnbW3dStaticSortDisposition.DeferredBucket, packet.OrderKey.StaticSort.Disposition);
        Assert.NotNull(packet.DeformedGeometry);
        Assert.Equal(6, packet.Mesh.Positions.Count);
        Assert.Equal(2, packet.Mesh.TriangleMetadata.Count);
        Assert.Equal(6, packet.Mesh.EffectiveColor1.Count);
        EnbW3dCompiledMaterialSemantics semantics = Assert.IsType<EnbW3dCompiledMaterialSemantics>(packet.Material.SingleBindingSemantics);
        Assert.False(semantics.VertexLighting.LightingEnabled);
        Assert.Equal(EnbRenderCullMode.None, semantics.FixedFunction.CullMode);
        Assert.Equal(1, semantics.PermutationKey.TextureStageCount);
        Assert.True(EnbW3dIndexedSubmissionCompiler.TryCompile(
            packet,
            out EnbW3dIndexedSubmissionPlan submission,
            out diagnostic), diagnostic);
        Assert.Single(submission.Runs);
        Assert.Equal(2, submission.Runs[0].TriangleCount);
        EnbTextureImage texture = EnbTextureImage.CreateCurrentRetailRuntimeArgb4444(
            1,
            1,
            [new EnbColor32(255, 255, 255, 255)]);
        Assert.True(EnbW3dProgrammableTextureLaneCompiler.TryCompile(
            submission,
            camera,
            EnbRenderTextureRuntimeSettings.RetailDefaults,
            EnbRenderTextureBackendCapabilities.ModernDefault,
            _ => texture,
            out EnbW3dProgrammableTextureLanePlan lane,
            out diagnostic), diagnostic);
        Assert.False(lane.LightingPlan.RunStates[0].Lighting.LightingEnabled);
        Assert.Equal(
            EnbW3dProgrammableStage0ProgramKind.ModulateTextureDiffuse,
            lane.RunStates[0].Stage0Program);

        // Grow and shrink the retained material prefix, then replace its
        // texture. Compare each result with a newly decoded owner definition.
        foreach(int clock in new[]{201,1001,401,1601,601})
        foreach(string? runtimeTexture in new string?[]{null,"replacement-particle.tga",null})
        {
            var current=instance with {RuntimeTextureName=runtimeTexture};
            Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(drawList.ResourceKey,
                drawList.RenderIdentity,drawList.VisualGeneration,current,camera,EnbW3dLightEnvironment.BlackNoLights,
                0,0,clock,out var reused,out _,out diagnostic),diagnostic);
            Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(drawList.ResourceKey,
                drawList.RenderIdentity,drawList.VisualGeneration,current with {Definition=definition.Copy()},camera,
                EnbW3dLightEnvironment.BlackNoLights,0,0,clock,out var fresh,out _,out diagnostic),diagnostic);
            Assert.NotNull(reused);Assert.NotNull(fresh);
            Assert.Equal(fresh.DeformedGeometry!.Positions.ToArray(),reused.DeformedGeometry!.Positions.ToArray());
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(fresh.Material.Source.TriangleBindings),
                System.Text.Json.JsonSerializer.Serialize(reused.Material.Source.TriangleBindings));
            Assert.True(EnbW3dIndexedSubmissionCompiler.TryCompile(reused,out var compiled,out diagnostic),diagnostic);
            Assert.Equal(reused.Mesh.Indices.Count,compiled.Runs.Sum(r=>r.IndexCount));
        }
    }

    [Fact]
    public void TexturedBackendPreservesPrelitParticleColorWithoutSceneLighting()
    {
        (string vertex, _) = EnbOpenGlStage0TextureBackend.CreateShaderSources(
            new GlVersion(GlProfileType.OpenGLES, 3, 0));

        Assert.Contains("uniform int uLightingEnabled", vertex, StringComparison.Ordinal);
        Assert.Contains("vDiffuse=uLightingEnabled!=0?", vertex, StringComparison.Ordinal);
        Assert.Contains(":aColor1", vertex, StringComparison.Ordinal);
        Assert.Contains(":vec3(0.0)", vertex, StringComparison.Ordinal);
    }

    private static EnbW3dRenderResource CreateEmitterResource(
        EnbCurrentRetailParticleEmitterDefinition definition)
    {
        var hierarchy = new EnbW3dRenderHierarchy(
            0x30000u,
            "ROOT",
            Vector3.Zero,
            HasSyntheticRetailRoot: false,
            [
                new EnbW3dRenderPivot(
                    0, "ROOT", uint.MaxValue, Vector3.Zero, Vector3.Zero,
                    Quaternion.Identity, Matrix4x4.Identity),
            ],
            Array.Empty<EnbW3dRenderPivotFixup>());
        var hlod = new EnbW3dRenderHlod(
            "ROOT",
            "ROOT",
            [
                new EnbW3dRenderHlodLevel(
                    0, float.MaxValue, BitConverter.SingleToUInt32Bits(float.MaxValue),
                    [new EnbW3dRenderHlodMember(0u, "PWEAPEXHAUST")]),
            ]);
        return new EnbW3dRenderResource(
            new EnbW3dRenderResourceKey("PSS04.W3D", "test.mix", 0, 1),
            "PSS04.W3D",
            Array.Empty<EnbW3dRenderMesh>(),
            [hierarchy],
            Array.Empty<EnbW3dRenderHierarchyAnimation>(),
            Array.Empty<EnbW3dRenderHierarchyModel>(),
            [hlod])
        {
            ExternalRenderObjects =
            [
                new EnbW3dExternalRenderObject(
                    "PWEAPEXHAUST",
                    EnbW3dExternalRenderObjectKind.ParticleEmitter,
                    "pweapexhaust.w3d",
                    "mixfile_art_17.mix")
                {
                    ParticleEmitterDefinition = definition,
                    ParticleTextureLocation = new EnbW3dRenderTextureLocation(
                        "smoke_jlt.dds", "test.mix", 1, 1),
                },
            ],
        };
    }

    private static EnbSceneObjectSnapshot CreateSceneObject()
        => new(
            RenderIdentity: 0x5566_7788_99AA_BBCCUL,
            VisualGeneration: 1,
            GameId: 0x1549,
            IsLocalShip: true,
            IsCurrentTarget: false,
            ObjectType: 0x25,
            BaseAsset: 1549,
            Scale: 1f,
            HsvH: 0,
            HsvS: 0,
            HsvV: 0,
            RelationshipRaw: 0,
            IsAttacking: false,
            Name: "ParticleBuffer fixture",
            Radar: new EnbSceneRadarSnapshot(false, 0, null),
            Spatial: new EnbSceneSpatialSnapshot(
                EnbSceneSpatialProviderKind.Fixed,
                new EnbSceneTransformSnapshot(
                    401, Vector3.Zero, Quaternion.Identity, Vector3.Zero, 0f,
                    Vector3.Zero, Vector3.UnitX),
                null),
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

    [Theory]
    [InlineData(0f, 0)]
    [InlineData(1f, 1)]
    [InlineData(2.9f, 2)]
    public void EmissionLimitIsAnAuthoredFloatConvertedToInteger(float authored, int expected)
    {
        Assert.True(EnbCurrentRetailParticleEmitterDecoder.TryDecode(BuildPweapExhaustDefinition(authored), out var d, out var error), error);
        Assert.Equal(expected, d.MaximumEmissions);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(2147483648f)]
    public void MalformedEmissionLimitIsRejected(float authored)
        => Assert.False(EnbCurrentRetailParticleEmitterDecoder.TryDecode(BuildPweapExhaustDefinition(authored), out _, out _));

    [Theory]
    [InlineData(33, false)]
    [InlineData(34, true)]
    [InlineData(127, true)]
    [InlineData(232, true)]
    [InlineData(233, false)]
    [InlineData(1000, false)]
    public void SingleQuadHasRetailTimingGeometryScaleAndAdditiveSort(int tick, bool visible)
    {
        Assert.True(EnbCurrentRetailParticleEmitterDecoder.TryDecode(BuildPweapExhaustDefinition(1), out var d, out _));
        d = d.Copy().SetRenderMode(1).SetLifetimeSeconds(.2f).SetEmissionRatePerSecond(30.303f).SetCreationRandomizer(new(0,Vector3.Zero)).SetVelocityPerSecond(Vector3.Zero).SetInitialOrientationRandom(0).SetRotation(new(0,[new(0,0)])).SetSize(new(0,[new(0,4)])).SetShader(d.Shader with { DestinationBlend=1 });
        EnbCurrentRetailRObjParticleAsset.Validate(d);
        EnbCurrentRetailRObjParticleAsset.Validate(d.Copy().SetMaximumEmissions(0));
        Assert.True(EnbResolvedRenderCamera.TryCreate(new(2000,0,0),Quaternion.Identity,new(640,480,1),Math.PI/3,new(1,10000),out var camera));
        var instance = new EnbW3dParticleEmitterDrawInstance("flash","flash.w3d",1,EnbRenderPassKind.OrdinaryWorld,d,
            new("lf.dds","fixture.mix",0,1),Matrix4x4.Identity,"",0,0);
        Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(new("flash.w3d","fixture.mix",0,1),1,1,instance,camera,
            EnbW3dLightEnvironment.BlackNoLights,0,0,tick,out var packet,out var receipt,out var error,particleSizeScale:3),error);
        Assert.Equal(visible,packet is not null);
        Assert.Equal(visible ? 1 : 0,receipt.LiveParticleCount);
        if (packet is null) return;
        Assert.Equal(4,packet.Mesh.Positions.Count);
        Assert.Equal(2,packet.Mesh.TriangleMetadata.Count);
        Assert.Equal(new uint[] {0,1,2,2,3,0},packet.Mesh.Indices);
        Assert.Equal(new Vector2[] {new(0,0),new(0,1),new(1,1),new(1,0)},packet.Mesh.MaterialPasses[0].TextureStages[0].EffectiveRuntimeTexCoords);
        Assert.Equal(12f,Vector3.Distance(packet.Mesh.Positions[0],packet.Mesh.Positions[1]),3);
        Assert.Equal(10,packet.OrderKey.StaticSort.RuntimeLevel);
        Assert.True(EnbW3dIndexedSubmissionCompiler.TryCompile(packet,out var submission,out error),error);
        Assert.Equal(2,Assert.Single(submission.Runs).TriangleCount);
    }

    [Fact]
    public void FlashBirthInterpolatesThenRemainsDetachedFromMovingOwner()
    {
        var birth = new EnbCurrentRetailParticleBirthRuntime();
        birth.Record(0,Matrix4x4.Identity);
        birth.Record(40,Matrix4x4.CreateTranslation(40,0,0));
        Assert.False(birth.Capture(33,33,2));
        Assert.True(birth.Capture(40,33,2));
        Assert.Equal(new Vector3(33,0,0),birth.BirthWorld!.Value.Translation);
        Assert.Equal(2,birth.BirthWorld.Value.M11);
        birth.Record(100,Matrix4x4.CreateTranslation(100,0,0));
        Assert.True(birth.Capture(100,33,2));
        Assert.Equal(new Vector3(33,0,0),birth.BirthWorld.Value.Translation);
        var missing = new EnbCurrentRetailParticleBirthRuntime();
        missing.Record(40,Matrix4x4.Identity);
        Assert.Throws<InvalidDataException>(() => missing.Capture(40,33,1));
        var turning = new EnbCurrentRetailParticleBirthRuntime();
        turning.Record(0,Matrix4x4.Identity);
        turning.Record(66,Matrix4x4.CreateRotationZ(MathF.PI/2));
        Assert.True(turning.Capture(66,33,1));
        float nativeBirthScale=MathF.Pow(MathF.Sin(MathF.PI/4)/(MathF.PI/4),2);
        Assert.True(Vector3.Distance(Vector3.TransformNormal(Vector3.UnitX,turning.BirthWorld!.Value),
            new Vector3(MathF.Sqrt(.5f),MathF.Sqrt(.5f),0)*nativeBirthScale)<.00001f);
    }

    [Theory]
    [InlineData(50, null, 0, 0)]
    [InlineData(51, null, 1, 1)]
    [InlineData(2001, null, 40, 20)]
    [InlineData(3000, null, 40, 0)]
    [InlineData(900, 501, 10, 10)]
    [InlineData(1500, 501, 10, 0)]
    public void MovingFiniteEmitterRetainsBirthsAndStopsEmissionWithoutDroppingTail(int tick, int? stop, int emitted, int live)
    {
        Assert.True(EnbCurrentRetailParticleEmitterDecoder.TryDecode(BuildPweapExhaustDefinition(40), out var d, out _));
        d=d.Copy().SetRenderMode(1).SetLifetimeSeconds(1).SetEmissionRatePerSecond(20).SetCreationRandomizer(new(0,Vector3.Zero)).SetVelocityRandomizer(new(0,Vector3.Zero)).SetVelocityPerSecond(Vector3.Zero).SetOutwardVelocityPerSecond(0).SetSize(new(0,[new(0,1)]));
        EnbCurrentRetailRObjParticleAsset.ValidateFinite(d);
        var history=new EnbCurrentRetailParticlePoseHistory();
        history.Record(0,Matrix4x4.Identity);
        uint latest=(uint)(stop ?? tick);
        history.Record(latest,Matrix4x4.CreateTranslation(latest,0,0));
        Assert.True(EnbResolvedRenderCamera.TryCreate(new(2000,0,0),Quaternion.Identity,new(640,480,1),Math.PI/3,new(1,10000),out var camera));
        var instance=new EnbW3dParticleEmitterDrawInstance("finite","finite.w3d",1,EnbRenderPassKind.OrdinaryWorld,d,
            new("lf.dds","fixture.mix",0,1),Matrix4x4.Identity,"",0,0);
        Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(new("finite.w3d","fixture.mix",0,1),1,1,instance,camera,
            EnbW3dLightEnvironment.BlackNoLights,0,0,tick,out var packet,out var receipt,out var error,
            birthWorld: b=>history.Resolve(b,2),emissionStopMilliseconds:stop),error);
        Assert.Equal(emitted,receipt.EmittedParticleCount); Assert.Equal(live,receipt.LiveParticleCount);
        if (packet is null) { Assert.Equal(0,live); return; }
        for (int i=0;i<live;i++)
        {
            var center=packet.Mesh.Positions.Skip(i*4).Take(4).Aggregate(Vector3.Zero,(a,b)=>a+b)/4;
            Assert.Equal((emitted-live+i+1)*50,center.X,2);
        }
        var frozen=history.Resolve(emitted*50,2);
        history.Record(latest+100,Matrix4x4.CreateTranslation(9000,0,0));
        Assert.Equal(frozen,history.Resolve(emitted*50,2));
    }

    [Theory]
    [InlineData(50, 0, 0)]
    [InlineData(51, 3, 3)]
    [InlineData(101, 6, 6)]
    [InlineData(151, 8, 8)]
    [InlineData(1100, 8, 2)]
    [InlineData(1150, 8, 0)]
    public void FiniteBurstsClampPartialFinalBurstAndExpireAtLastBirth(int tick, int emitted, int live)
    {
        Assert.True(EnbCurrentRetailParticleEmitterDecoder.TryDecode(BuildPweapExhaustDefinition(8), out var d, out _));
        d=d.Copy().SetRenderMode(1).SetBurstSize(3).SetLifetimeSeconds(1).SetEmissionRatePerSecond(20).SetCreationRandomizer(new(0,Vector3.Zero)).SetVelocityRandomizer(new(0,Vector3.Zero)).SetVelocityPerSecond(Vector3.Zero).SetOutwardVelocityPerSecond(0).SetSize(new(0,[new(0,1)]));
        EnbCurrentRetailRObjParticleAsset.ValidateFinite(d);
        var asset=new EnbCurrentRetailRObjParticleAsset(new("burst.w3d","fixture.mix",0,1),new("lf.dds","fixture.mix",0,1),d);
        Assert.Equal(1150u,asset.EndMilliseconds);
        var history=new EnbCurrentRetailParticlePoseHistory();
        history.Record(0,Matrix4x4.Identity);
        history.Record((uint)tick,Matrix4x4.CreateTranslation(tick,0,0));
        Assert.True(EnbResolvedRenderCamera.TryCreate(new(2000,0,0),Quaternion.Identity,new(640,480,1),Math.PI/3,new(1,10000),out var camera));
        var instance=new EnbW3dParticleEmitterDrawInstance("burst","burst.w3d",1,EnbRenderPassKind.OrdinaryWorld,d,
            asset.Texture,Matrix4x4.Identity,"",0,0);
        Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(asset.Key,1,1,instance,camera,
            EnbW3dLightEnvironment.BlackNoLights,0,0,tick,out var packet,out var receipt,out var error,
            birthWorld: b=>history.Resolve(b,1)),error);
        Assert.Equal(emitted,receipt.EmittedParticleCount); Assert.Equal(live,receipt.LiveParticleCount);
        for(int i=0;i<live;i++)
        {
            var center=packet!.Mesh.Positions.Skip(i*4).Take(4).Aggregate(Vector3.Zero,(a,b)=>a+b)/4;
            Assert.Equal(((emitted-live+i)/3+1)*50,center.X,2);
        }
    }

    [Theory]
    [InlineData(1169)]
    [InlineData(1202)]
    [InlineData(1204)]
    [InlineData(1789)]
    [InlineData(1780)]
    [InlineData(1902)]
    [InlineData(1563)]
    public void InstalledFiniteBurstEmittersUseIndependentBirthHistory(int id)
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var asset=EnbCurrentRetailRObjEffectAsset.Load(EnbRetailAssetBundle.Load(paths),id);
        Assert.Null(asset.Particle);
        var particle=Assert.Single(asset.AggregateParticles!);
        Assert.InRange(particle.EndMilliseconds,1u,20000u);
    }

    [Fact]
    public void InstalledInterruptionAggregateRetainsParentAndAllFourAuthoredEmitters()
    {
        if (EnbInstalledAssetLocator.TryLocate() is not { } paths) return;
        var asset=EnbCurrentRetailRObjEffectAsset.Load(EnbRetailAssetBundle.Load(paths),1880);
        Assert.Null(asset.Particle);
        //98C420/9A3A50 retain this empty parent and its emitter children;
        //stationary pivots do not turn it into a standalone replay burst.
        Assert.NotNull(asset.Resource);
        Assert.Single(asset.Resource.HierarchicalLods);
        Assert.Single(asset.Resource.Hierarchies);
        Assert.Equal(4,asset.AggregateParticles!.Count);
        Assert.Equal(4,asset.AggregateParticles.Select(p=>p.Definition.Name).Distinct().Count());
        Assert.All(asset.AggregateParticles,p=> { Assert.Equal(40,p.Definition.MaximumEmissions); Assert.Equal(3000u,p.EndMilliseconds); });
        Assert.Equal(2,asset.AggregateParticles.Select(p=>p.Definition.TextureFileName).Distinct().Count());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void QuadAtlasSelectsExactlyOneAuthoredCell(int frame)
    {
        Assert.True(EnbCurrentRetailParticleEmitterDecoder.TryDecode(BuildPweapExhaustDefinition(1),out var d,out _));
        d=d.Copy().SetRenderMode(1).SetFrameMode(1).SetEmissionRatePerSecond(100).SetFrame(new(0,[new(0,frame)]));
        Assert.True(EnbResolvedRenderCamera.TryCreate(new(2000,0,0),Quaternion.Identity,new(640,480,1),Math.PI/3,new(1,10000),out var camera));
        var instance=new EnbW3dParticleEmitterDrawInstance("atlas","atlas.w3d",1,EnbRenderPassKind.OrdinaryWorld,d,
            new("atlas.dds","fixture.mix",0,1),Matrix4x4.Identity,"",0,0);
        Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(new("atlas.w3d","fixture.mix",0,1),1,1,instance,camera,
            EnbW3dLightEnvironment.BlackNoLights,0,0,11,out var packet,out _,out var error),error);
        var uv=packet!.Mesh.MaterialPasses[0].TextureStages[0].EffectiveRuntimeTexCoords;
        var origin=new Vector2(frame&1,frame>>1)*.5f;
        Assert.Equal(new[] {origin,origin+new Vector2(0,.5f),origin+new Vector2(.5f,.5f),origin+new Vector2(.5f,0)},uv);
    }

    [Fact]
    public void SphereSurfaceBirthsUseRadiusAndBufferAccelerationStaysInWorldSpace()
    {
        Assert.True(EnbCurrentRetailParticleEmitterDecoder.TryDecode(BuildPweapExhaustDefinition(40),out var d,out _));
        d=d.Copy().SetRenderMode(1).SetFrameMode(1).SetEmissionRatePerSecond(100).SetLifetimeSeconds(2).SetCreationRandomizer(new(2,new(2,0,0))).SetVelocityRandomizer(new(0,Vector3.Zero)).SetVelocityPerSecond(Vector3.Zero).SetAccelerationPerSecondSquared(new(0,0,3)).SetOutwardVelocityPerSecond(0);
        Assert.True(EnbResolvedRenderCamera.TryCreate(new(2000,0,0),Quaternion.Identity,new(640,480,1),Math.PI/3,new(1,10000),out var camera));
        var instance=new EnbW3dParticleEmitterDrawInstance("sphere","sphere.w3d",1,EnbRenderPassKind.OrdinaryWorld,d,
            new("atlas.dds","fixture.mix",0,1),Matrix4x4.CreateScale(2)*Matrix4x4.CreateRotationY(1),"",0,0);
        Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(new("sphere.w3d","fixture.mix",0,1),1,1,instance,camera,
            EnbW3dLightEnvironment.BlackNoLights,0,0,401,out var packet,out _,out var error,particleSizeScale:2),error);
        for (int i=0;i<40;i++)
        {
            var center=packet!.Mesh.Positions.Skip(i*4).Take(4).Aggregate(Vector3.Zero,(a,b)=>a+b)/4;
            float age=(401-(i+1)*10)/1000f;
            center-=new Vector3(0,0,3*age*age);
            Assert.Equal(4,center.Length(),3);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void FiniteBurstAccelerationUsesWorldAxesAfterSourceTurns(uint mode)
    {
        Assert.True(EnbCurrentRetailParticleEmitterDecoder.TryDecode(BuildPweapExhaustDefinition(3),out var d,out _));
        d=d.Copy().SetRenderMode(mode).SetFrameMode(0).SetBurstSize(3).SetEmissionRatePerSecond(20).SetLifetimeSeconds(2).SetCreationRandomizer(new(0,Vector3.Zero)).SetVelocityRandomizer(new(0,Vector3.Zero)).SetVelocityPerSecond(Vector3.Zero).SetOutwardVelocityPerSecond(0).SetAccelerationPerSecondSquared(new(0,0,4));
        EnbCurrentRetailRObjParticleAsset.ValidateFinite(d);
        var history=new EnbCurrentRetailParticlePoseHistory();
        history.Record(0,Matrix4x4.CreateRotationX(MathF.PI/2));
        history.Record(51,Matrix4x4.CreateRotationX(MathF.PI/2));
        var born=history.Resolve(50,2);
        history.Record(1050,Matrix4x4.CreateTranslation(500,600,700));
        Assert.Equal(born,history.Resolve(50,2));
        Assert.True(EnbResolvedRenderCamera.TryCreate(new(2000,0,0),Quaternion.Identity,new(640,480,1),Math.PI/3,new(1,10000),out var camera));
        var instance=new EnbW3dParticleEmitterDrawInstance("acceleration","acceleration.w3d",1,EnbRenderPassKind.OrdinaryWorld,d,
            new("lf.dds","fixture.mix",0,1),Matrix4x4.Identity,"",0,0);
        Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(new("acceleration.w3d","fixture.mix",0,1),1,1,instance,camera,
            EnbW3dLightEnvironment.BlackNoLights,0,0,1050,out var packet,out var receipt,out var error,
            particleSizeScale:2,birthWorld:b=>history.Resolve(b,2)),error);
        Assert.Equal(3,receipt.LiveParticleCount);
        int vertices=mode==0 ? 3 : 4;
        for(int i=0;i<3;i++)
        {
            var center=packet!.Mesh.Positions.Skip(i*vertices).Take(vertices).Aggregate(Vector3.Zero,(a,b)=>a+b)/vertices;
            Assert.True(Vector3.Distance(new(0,0,4),center)<.001f);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void VelocityRandomizersFollowAuthoredShapeAndContinueAlongFrozenWorldVelocity(int kind)
    {
        Assert.True(EnbCurrentRetailParticleEmitterDecoder.TryDecode(BuildPweapExhaustDefinition(40),out var d,out _));
        d=d.Copy().SetRenderMode(1).SetFrameMode(0).SetEmissionRatePerSecond(100).SetLifetimeSeconds(2).SetCreationRandomizer(new(0,Vector3.Zero)).SetVelocityRandomizer(new((uint)kind,new(3,2,1))).SetVelocityPerSecond(Vector3.Zero).SetAccelerationPerSecondSquared(Vector3.Zero).SetOutwardVelocityPerSecond(0);
        Assert.True(EnbResolvedRenderCamera.TryCreate(new(2000,0,0),Quaternion.Identity,new(640,480,1),Math.PI/3,new(1,10000),out var camera));
        var instance=new EnbW3dParticleEmitterDrawInstance("velocity","velocity.w3d",1,EnbRenderPassKind.OrdinaryWorld,d,
            new("atlas.dds","fixture.mix",0,1),Matrix4x4.Identity,"",0,0);
        EnbW3dDrawTask Sample(int tick)
        {
            Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(new("velocity.w3d","fixture.mix",0,1),1,1,instance,camera,
                EnbW3dLightEnvironment.BlackNoLights,0,0,tick,out var packet,out _,out var error),error);
            return packet!;
        }
        var first=Sample(601); var second=Sample(801);
        var velocities=new List<Vector3>();
        for(int i=0;i<40;i++)
        {
            Vector3 Center(EnbW3dDrawTask packet)=>packet.Mesh.Positions.Skip(i*4).Take(4).Aggregate(Vector3.Zero,(a,b)=>a+b)/4;
            var velocity=Center(first)/((601-(i+1)*10)/1000f);
            Assert.True(Vector3.Distance(velocity,Center(second)/((801-(i+1)*10)/1000f))<.001f);
            if(kind==2) Assert.Equal(3,velocity.Length(),3);
            else if(kind==1) Assert.InRange(velocity.Length(),0,3.001f);
            else if(kind==3) { Assert.InRange(MathF.Abs(velocity.X),0,3.001f); Assert.InRange(new Vector2(velocity.Y,velocity.Z).Length(),0,2.001f); }
            else { Assert.InRange(MathF.Abs(velocity.X),0,3.001f); Assert.InRange(MathF.Abs(velocity.Y),0,2.001f); Assert.InRange(MathF.Abs(velocity.Z),0,1.001f); }
            velocities.Add(velocity);
        }
        Assert.True(velocities.Distinct().Count()>30);
        Assert.Contains(velocities,v=>v.Length()>.5f);
    }

    [Theory]
    [InlineData(1210)]
    [InlineData(1212)]
    [InlineData(1519)]
    [InlineData(1798)]
    [InlineData(1543)]
    [InlineData(1694)]
    [InlineData(1510)]
    [InlineData(1896)]
    [InlineData(1899)]
    [InlineData(1904)]
    [InlineData(1906)]
    [InlineData(1594)]
    public void InstalledContinuousEffectFamiliesUseRetainedBirths(int id)
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var asset=EnbCurrentRetailRObjEffectAsset.Load(EnbRetailAssetBundle.Load(paths),id);
        Assert.NotNull(asset.Particle);
        var runtime=new EnbCurrentRetailShipParticleRuntime(asset.Particle.Definition,allowInheritedMotion:true);
        runtime.Advance(0,Matrix4x4.Identity,true);
        for(int t=16;t<1251;t+=16)runtime.Advance(t,Matrix4x4.CreateTranslation(10f*t/1251,0,0),true);
        runtime.Advance(1251,Matrix4x4.CreateTranslation(10,0,0),true);
        Assert.NotEmpty(runtime.Births);
        Assert.All(runtime.Births,b=>Assert.InRange(Vector3.Distance(b.InheritedVelocityPerMillisecond,
            new Vector3(10f/1251f*asset.Particle.Definition.VelocityInheritanceFactor,0,0)),0,.000001f));
        if(asset.Particle.Definition.VelocityInheritanceFactor!=0)
        {
            var original=runtime.Births[0];
            var emitter=new EnbW3dParticleEmitterDrawInstance(asset.Particle.Definition.Name,"fixture",1,
                EnbRenderPassKind.OrdinaryWorld,asset.Particle.Definition,asset.Particle.Texture,Matrix4x4.Identity,"fixture",0,0);
            EnbResolvedRenderCamera.TryCreate(Vector3.Zero,Quaternion.Identity,new(800,600,1),Math.PI/2,new(1,10000),out var camera);
            Vector3[] Draw(EnbCurrentRetailParticleBirth birth)
            {
                birth=birth with {World=Matrix4x4.CreateScale(3)*birth.World};
                Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(asset.Particle.Key,1,1,emitter,camera,
                    EnbW3dLightEnvironment.BlackNoLights,1,0,1251,out var packet,out _,out var error,
                    particleSizeScale:3,worldBirths:[birth]),error);
                return Assert.IsType<EnbW3dDrawTask>(packet).DeformedGeometry!.Positions.ToArray();
            }
            // Inheritance is already applied by the birth writer, before any view.
            var moving=Draw(original);var still=Draw(original with {InheritedVelocityPerMillisecond=Vector3.Zero,
                SampledVelocityPerMillisecond=original.SampledVelocityPerMillisecond-original.InheritedVelocityPerMillisecond});
            var expected=original.InheritedVelocityPerMillisecond*(1251-original.Time);
            for(int i=0;i<moving.Length;i++)Assert.InRange(Vector3.Distance(moving[i]-still[i],expected),0,.0001f);
        }
        var frozen=runtime.Births.ToArray();
        runtime.Advance(1252,Matrix4x4.CreateTranslation(1000,0,0),false);
        Assert.All(runtime.Births,b=>Assert.Contains(b,frozen));
        runtime.Advance(1252+EnbRetailParticleEmissionArithmetic.LifetimeMilliseconds(asset.Particle.Definition.LifetimeSeconds),Matrix4x4.Identity,false);
        Assert.Empty(runtime.Births);
    }

    [Theory]
    [InlineData(1153)]
    [InlineData(1173)]
    [InlineData(1174)]
    public void InstalledFoldParticlesUseAnimatedPivotsAndRetainIndependentBirths(int id)
    {
        if (EnbInstalledAssetLocator.TryLocate() is not { } paths) return;
        var asset=EnbCurrentRetailRObjEffectAsset.Load(EnbRetailAssetBundle.Load(paths),id);
        Assert.NotNull(asset.Animation); Assert.Equal(3,asset.AggregateParticles!.Count);
        var history=new EnbCurrentRetailParticlePoseHistory();
        var runtime=new EnbCurrentRetailAnimatedParticleRuntime();
        for (uint t=0;t<=1100;t+=10)
        {
            history.Record(t,Matrix4x4.CreateTranslation(t/10f,0,0)); runtime.Advance(asset,history,2);
            if (id==1153 && t<1000) Assert.Empty(runtime.Emitters[0].Births);
        }
        Assert.All(runtime.Emitters,e=>Assert.NotEmpty(e.Births));
        var first=runtime.Emitters[0]; var birth=first.Births[0]; var frozen=first.Worlds[birth];
        history.Record(1110,Matrix4x4.CreateTranslation(9000,0,0)); runtime.Advance(asset,history,2);
        Assert.Equal(frozen,first.Worlds[birth]);
        int count=first.Births.Count; runtime.Advance(asset,history,2); Assert.Equal(count,first.Births.Count);
        Assert.All(runtime.Emitters,e=>Assert.Equal(e.Births.Count,e.Births.Distinct().Count()));
    }

    [Fact]
    public void AnimatedFiniteEmitterResumesPartialAllowanceAndRestartsOnlyAfterCompletion()
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var original=EnbCurrentRetailRObjEffectAsset.Load(EnbRetailAssetBundle.Load(paths),1153);
        var particle=original.AggregateParticles![0];
        particle=particle with {Definition=particle.Definition.Copy().SetMaximumEmissions(2).SetEmissionRatePerSecond(20)};
        var animation=new EnbW3dRenderHierarchyAnimation(0,0x30000,"VISIBILITY",original.Resource!.Hierarchies.Single().Name,6,10,false,[],
            [new(0,5,0,EnbW3dAnimationBitChannelKind.Visibility,(ushort)particle.PivotIndex,particle.PivotIndex,0,[0b101101])]);
        var asset=original with {Animation=animation,AggregateParticles=[particle]};
        EnbCurrentRetailRObjParticleAsset.ValidateAnimated(particle.Definition);
        var history=new EnbCurrentRetailParticlePoseHistory();
        var runtime=new EnbCurrentRetailAnimatedParticleRuntime();
        void Advance(uint age,int expected)
        {
            history.Record(age,Matrix4x4.CreateTranslation(age,0,0));runtime.Advance(asset,history,1);
            Assert.Equal(expected,runtime.Emitters[0].Births.Count);
        }
        Advance(0,0);Advance(60,1);
        var birth=runtime.Emitters[0].Births[0];var world=runtime.Emitters[0].Worlds[birth];
        Advance(100,1);Advance(200,2);Advance(300,2);Advance(400,2);Advance(500,4);
        Assert.Equal(world,runtime.Emitters[0].Worlds[birth]);
    }

    private static byte[] BuildPweapExhaustDefinition(float maximumEmissions = 0)
    {
        byte[] header = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x0002_0000u);
        Encoding.ASCII.GetBytes("PWEAPEXHAUST\0").CopyTo(header, 4);

        byte[] info = new byte[332];
        Encoding.ASCII.GetBytes("smoke_jlt.tga\0").CopyTo(info, 0);
        WriteSingle(info, 268, 8f);
        WriteSingle(info, 272, 5f);
        WriteSingle(info, 276, maximumEmissions);
        WriteSingle(info, 288, 0.504f);
        WriteSingle(info, 292, 0f);
        WriteSingle(info, 296, 0f);
        WriteVector3(info, 300, new Vector3(125f, 0f, 125f));
        WriteVector3(info, 312, Vector3.Zero);

        byte[] infoV2 = new byte[124];
        BinaryPrimitives.WriteUInt32LittleEndian(infoV2, 1u);
        BinaryPrimitives.WriteUInt32LittleEndian(infoV2.AsSpan(4, 4), 0u);
        WriteVector3(infoV2, 8, new Vector3(0.1f));
        BinaryPrimitives.WriteUInt32LittleEndian(infoV2.AsSpan(36, 4), 0u);
        WriteVector3(infoV2, 40, Vector3.Zero);
        byte[] shader = [3, 0, 0, 3, 0, 0, 0, 1, 1, 0, 0, 0, 0, 0, 0, 0];
        shader.CopyTo(infoV2, 76);
        BinaryPrimitives.WriteUInt32LittleEndian(infoV2.AsSpan(92, 4), 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(infoV2.AsSpan(96, 4), 0u);

        byte[] properties = new byte[112];
        BinaryPrimitives.WriteUInt32LittleEndian(properties, 4u);
        BinaryPrimitives.WriteUInt32LittleEndian(properties.AsSpan(4, 4), 1u);
        BinaryPrimitives.WriteUInt32LittleEndian(properties.AsSpan(8, 4), 4u);
        WriteSingle(properties, 16, 0f);
        WriteSingle(properties, 20, 1f);
        int cursor = 40;
        WriteColorKey(properties, ref cursor, 0f, 0xF7, 0xF7, 0xF7);
        WriteColorKey(properties, ref cursor, 1.008f, 0x7A, 0xE4, 0x86);
        WriteColorKey(properties, ref cursor, 2.768f, 0x39, 0x39, 0x39);
        WriteColorKey(properties, ref cursor, 6.7400002f, 0, 0, 0);
        WriteScalarKey(properties, ref cursor, 0f, 1f);
        WriteScalarKey(properties, ref cursor, 0f, 0f);
        WriteScalarKey(properties, ref cursor, 1.656f, 258f);
        WriteScalarKey(properties, ref cursor, 4.144f, 500f);
        WriteScalarKey(properties, ref cursor, 7.864f, 700f);

        byte[] rotation = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(rotation, 1u);
        WriteSingle(rotation, 4, 0f);
        WriteSingle(rotation, 8, 90f);
        cursor = 16;
        WriteScalarKey(rotation, ref cursor, 0f, 0.005f);
        WriteScalarKey(rotation, ref cursor, 4.2f, 0.1f);

        byte[] frame = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, 0u);
        WriteSingle(frame, 4, 0f);
        cursor = 16;
        WriteScalarKey(frame, ref cursor, 0f, 0f);

        return ContainerChunk(
            0x0500,
            LeafChunk(0x0501, header),
            LeafChunk(0x0502, new byte[12]),
            LeafChunk(0x0503, info),
            LeafChunk(0x0504, infoV2),
            LeafChunk(0x0505, properties),
            LeafChunk(0x050A, rotation),
            LeafChunk(0x050B, frame));
    }

    private static void WriteColorKey(byte[] bytes, ref int cursor, float time, byte r, byte g, byte b)
    {
        WriteSingle(bytes, cursor, time);
        bytes[cursor + 4] = r;
        bytes[cursor + 5] = g;
        bytes[cursor + 6] = b;
        cursor += 8;
    }

    private static void WriteScalarKey(byte[] bytes, ref int cursor, float time, float value)
    {
        WriteSingle(bytes, cursor, time);
        WriteSingle(bytes, cursor + 4, value);
        cursor += 8;
    }

    private static void WriteVector3(byte[] bytes, int offset, Vector3 value)
    {
        WriteSingle(bytes, offset, value.X);
        WriteSingle(bytes, offset + 4, value.Y);
        WriteSingle(bytes, offset + 8, value.Z);
    }

    private static void WriteSingle(byte[] bytes, int offset, float value)
        => BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(offset, 4), BitConverter.SingleToInt32Bits(value));

    private static byte[] LeafChunk(uint type, byte[] payload)
        => Chunk(type, payload, false);

    private static byte[] ContainerChunk(uint type, params byte[][] children)
        => Chunk(type, children.SelectMany(static child => child).ToArray(), true);

    private static byte[] Chunk(uint type, byte[] payload, bool container)
    {
        byte[] output = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(output, type);
        BinaryPrimitives.WriteUInt32LittleEndian(
            output.AsSpan(4, 4),
            checked((uint)payload.Length) | (container ? 0x8000_0000u : 0u));
        payload.CopyTo(output, 8);
        return output;
    }
}
