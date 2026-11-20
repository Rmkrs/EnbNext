using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

[Collection(LoadSensitiveCollection.Name)]
public sealed class EnbR9AkCurrentRetailShipNameDecalTests
{
    [Theory]
    [InlineData("SHIP.HULL")]
    [InlineData("SHIP.NAMEDECAL0")]
    public void PersistentNameBindingHasNoSteadyKeysMarksOrDescriptorAllocationsAndPreservesQueuedPose(string meshName)
    {
        var name=new EnbSceneNameDecalSnapshot("AB",[(byte)'A',(byte)'B',0],Vector3.One);
        var obj=Assert.Single(new EnbPresentationSnapshotBuilder().Build(EnbR11FlightTests.FlightWorld(),null,1000).Objects)
            with {NameDecal=name,NameDecalApplyCount=1};
        var raw=CreatePacket(meshName);
        var cache=new EnbShipMaterialCache();var binding=new EnbShipMaterialCache.NameBinding();
        cache.RetainOwner((raw.RenderIdentity,raw.VisualGeneration));cache.BeginFrame();
        EnbCurrentRetailShipNameDecalTexture? texture=null;
        EnbW3dDrawTask Apply(EnbW3dDrawTask packet)=>cache.ApplyName(packet,obj,texture,0,null,false,out _,binding);
        var unresolved=Apply(raw);
        Assert.True(EnbCurrentRetailShipNameDecalRuntime.TryBuildTexture(name,CreateFontAtlas(true),"name",out var ready,out _));
        texture=ready;var first=Apply(raw);
        if(meshName=="SHIP.NAMEDECAL0")Assert.NotSame(unresolved.Material,first.Material);
        var moved=(raw).ForkTask().SetWorldTransform(Matrix4x4.CreateTranslation(1,2,3)).SetFrameTimeMilliseconds(1234);
        var second=Apply(moved);Assert.Equal(moved.FrameInputs,second.FrameInputs);Assert.Equal(raw.FrameInputs,first.FrameInputs);
        Assert.Same(first.Material,second.Material);
        for(int i=0;i<100;i++)Apply(raw);
        long keys=cache.NameKeyDerivations,marks=cache.MarkSeenWrites;
        long before=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<1000;i++)Apply(moved);
        long bytes=GC.GetAllocatedBytesForCurrentThread()-before;
        // A changed material needs an immutable frame header, exactly like a with-copy.
        // It must not rebuild any prepared descriptor or capture another queued pose.
        long copyBefore=GC.GetAllocatedBytesForCurrentThread();
        EnbW3dDrawTask? copy=null;
        for(int i=0;i<1000;i++)copy=(first).ForkTask();
        long headerBytes=GC.GetAllocatedBytesForCurrentThread()-copyBefore;GC.KeepAlive(copy);
        Assert.Equal(meshName=="SHIP.HULL"?0:headerBytes,bytes);
        Assert.Equal(keys,cache.NameKeyDerivations);Assert.Equal(marks,cache.MarkSeenWrites);
        cache.EndFrame();cache.BeginFrame();cache.EndFrame();Assert.Equal(0,cache.LastSweepCandidateCount);
        Assert.Same(first.Material,Apply(raw).Material);
        obj=obj with {NameDecalApplyCount=2};Apply(raw);Assert.Equal(keys+1,cache.NameKeyDerivations);
        cache.Clear();Apply(raw);Assert.Equal(keys+2,cache.NameKeyDerivations);
        cache.ReleaseOwner((raw.RenderIdentity,raw.VisualGeneration));Apply(raw);Assert.Equal(keys+3,cache.NameKeyDerivations);
    }

    [Fact]
    public void NameTargetParsingDoesNotAllocateAQualifiedMeshSuffix()
    {
        string[] names=["SHIP.NAMEDECAL0","ship.namedecal","SHIP.BODY","NAMEDECAL.","SHIP."," ","SHIP.X.NAMEDECAL"];
        bool[] expected=[true,true,false,true,false,false,false];
        for(int i=0;i<names.Length;i++)Assert.Equal(expected[i],EnbCurrentRetailShipNameDecalRuntime.IsTarget(names[i]));
        int count=0;
        long before=GC.GetAllocatedBytesForCurrentThread();
        for(int repeat=0;repeat<100;repeat++)
            for(int i=0;i<names.Length;i++)if(EnbCurrentRetailShipNameDecalRuntime.IsTarget(names[i]))count++;
        long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
        Assert.Equal(300,count);
        Assert.Equal(0,allocated);
    }

    [Fact]
    public void LaterColorPacketWinsOverNameRgbAndShaderWithoutReplacingItsTexture()
    {
        static EnbSceneColorizationSnapshot Side(Vector3 rgb)
        {
            var bytes=new byte[12];
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bytes,rgb.X);
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(4),rgb.Y);
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(8),rgb.Z);
            return new(unchecked((uint)-1),bytes.ToImmutableArray());
        }
        var name=new EnbSceneNameDecalSnapshot("AB",new byte[]{65,66,0}.ToImmutableArray(),Vector3.UnitX);
        Assert.True(EnbCurrentRetailShipNameDecalRuntime.TryBuildTexture(name,CreateFontAtlas(true),"ordered-name",out var texture,out var error),error);
        var raw=CreatePacket("SHIP.NAMEDECAL0","~S1");
        var obj=Assert.Single(new EnbPresentationSnapshotBuilder().Build(EnbR11FlightTests.FlightWorld(),null,1000).Objects)
            with {NameDecal=name,NameDecalApplyCount=1,NameDecalColorizationApplyCount=0,
                ColorizationEntries=[Side(Vector3.UnitY),Side(Vector3.One)],ColorizationAudit=new(1,0,0,0,[],[])};
        var cache=new EnbShipMaterialCache();cache.BeginFrame();
        var beforeName=cache.ApplySurface(raw,obj,"root",0,EnbEnvironmentMapAxis.Y,true,null,null,out _);
        var result=cache.ApplyName(beforeName,obj,texture,0,EnbEnvironmentMapAxis.Y,true,out var receipt);
        Assert.True(receipt.Applied);
        var actual=Assert.Single(result.Material.Source.TriangleBindings);
        Assert.Equal(Vector3.UnitY,actual.Semantics!.Value.Uniforms.Diffuse);
        Assert.Same(texture.TextureState,actual.Textures[0]!.CurrentRetailTextureState);
        var sequential=EnbCurrentRetailShipNameDecalRuntime.Apply(raw,name,texture,out _);
        sequential=EnbCurrentRetailShipColorizationRuntime.Apply(sequential,obj.ColorizationEntries,0,EnbEnvironmentMapAxis.Y,out _);
        Assert.Equal(sequential.Material.Source.CurrentRetailUniformShader,result.Material.Source.CurrentRetailUniformShader);
        Assert.Same(result.Material.Source,cache.ApplyName(beforeName,obj,texture,0,EnbEnvironmentMapAxis.Y,true,out _).Material.Source);
        var latestName=obj with {NameDecalApplyCount=2,NameDecalColorizationApplyCount=1};
        var renamed=cache.ApplyName(beforeName,latestName,texture,0,EnbEnvironmentMapAxis.Y,true,out _);
        Assert.Equal(Vector3.UnitX,Assert.Single(renamed.Material.Source.TriangleBindings).Semantics!.Value.Uniforms.Diffuse);
        Assert.Equal(0xD441Bu,renamed.Material.Source.CurrentRetailUniformShader);
    }

    [Fact]
    public void FontGenerationIgnoresRgbAndMaterialSettersCopyRawWords()
    {
        var state = new EnbSceneNameDecalSnapshot("AB",new byte[]{65,66,0}.ToImmutableArray(),Vector3.One);
        var unusual = state with { Rgb=new(BitConverter.Int32BitsToSingle(0x7FC12345),float.PositiveInfinity,float.NegativeInfinity) };
        var font=CreateFontAtlas(true);
        Assert.True(EnbCurrentRetailShipNameDecalRuntime.TryBuildTexture(state,font,"finite",out var finite,out var error),error);
        Assert.True(EnbCurrentRetailShipNameDecalRuntime.TryBuildTexture(unusual,font,"raw",out var raw,out error),error);
        Assert.Equal(finite.TextWidth,raw.TextWidth);
        for(int y=0;y<64;y++)for(int x=0;x<64;x++)Assert.Equal(finite.Image.GetPixel(x,y),raw.Image.GetPixel(x,y));
        var result=EnbCurrentRetailShipNameDecalRuntime.Apply(CreatePacket("SHIP.NAMEDECAL0"),unusual,raw,out var receipt);
        Assert.True(receipt.Applied);
        var material=result.Material.Source.CurrentRetailUniformMaterial!.Value.Material;
        foreach(var rgb in new[]{material.CurrentRetailUniformOverride!.Value.Ambient,material.CurrentRetailUniformOverride.Value.Diffuse})
        {
            Assert.Equal(0x7FC12345,BitConverter.SingleToInt32Bits(rgb.X));
            Assert.Equal(float.PositiveInfinity,rgb.Y);Assert.Equal(float.NegativeInfinity,rgb.Z);
        }
    }

    [Fact]
    public void RawGlyphsPackIntoFourStripArgb4444Texture()
    {
        EnbTextureImage font = CreateFontAtlas(fullRetailHeight: true);
        var state = new EnbSceneNameDecalSnapshot(
            "A B",
            new byte[] { (byte)'A', (byte)' ', (byte)'B', 0 }.ToImmutableArray(),
            new Vector3(.2f, .4f, .6f));

        Assert.True(EnbCurrentRetailShipNameDecalRuntime.TryBuildTexture(
            state,
            font,
            "runtime.argb4444",
            out EnbCurrentRetailShipNameDecalTexture generated,
            out string? diagnostic), diagnostic);

        Assert.Equal(EnbTextureFormat.CurrentRetailRuntimeArgb4444, generated.Image.Format);
        Assert.Equal(64, generated.Image.Width);
        Assert.Equal(64, generated.Image.Height);
        Assert.Equal(16, generated.GlyphHeight);
        Assert.Equal(8, generated.TextWidth); // A(2)+gap + space(0)+gap + B(3)+gap.
        Assert.Equal(new EnbColor32(255, 136, 0, 255), generated.Image.GetPixel(0, 0));
        Assert.Equal(new EnbColor32(0, 0, 0, 0), generated.Image.GetPixel(2, 0));
        Assert.Equal(new EnbColor32(17, 34, 51, 255), generated.Image.GetPixel(4, 0));
        Assert.Equal(new EnbColor32(0, 0, 0, 0), generated.Image.GetPixel(7, 0));
        Assert.Equal(new EnbColor32(0, 0, 0, 0), generated.Image.GetPixel(0, 16));
    }

    [Fact]
    public void NameDecalReplacesStageZeroAndCopiesRgbToAmbientAndDiffuse()
    {
        EnbW3dDrawTask source = CreatePacket("SHIP.NAMEDECAL0");
        var state = new EnbSceneNameDecalSnapshot(
            "AB",
            new byte[] { (byte)'A', (byte)'B', 0 }.ToImmutableArray(),
            new Vector3(.2f, .4f, .6f));
        Assert.True(EnbCurrentRetailShipNameDecalRuntime.TryBuildTexture(
            state,
            CreateFontAtlas(fullRetailHeight: true),
            EnbCurrentRetailShipNameDecalRuntime.CreateLogicalTextureName(9, 1),
            out EnbCurrentRetailShipNameDecalTexture generated,
            out string? diagnostic), diagnostic);

        var oldPass=source.Material.Source;
        var oldTexture=oldPass.TextureStages[0].Textures[0] with {
            CurrentRetailResolvedTexture=new("old-hsv-image",EnbTextureResolutionStatus.Decoded,Image:generated.Image)};
        source=(source).ForkTask().SetMaterial(new((oldPass).Copy().SetTextureStages([(oldPass.TextureStages[0]).Copy().SetTextures([oldTexture]).SetCurrentRetailUniformTexture(oldTexture)]).SetTriangleBindings(oldPass.TriangleBindings.Select(t=>t with {Textures=[oldTexture]}).ToArray()),oldPass.SingleBindingSemantics));

        EnbW3dDrawTask effective = EnbCurrentRetailShipNameDecalRuntime.Apply(
            source,
            state,
            generated,
            out EnbCurrentRetailShipNameDecalReceipt receipt);

        Assert.Equal(EnbCurrentRetailShipNameDecalOutcome.Applied, receipt.Outcome);
        Assert.NotSame(source, effective);
        Assert.Equal(0xD441Bu, effective.Material.Source.TriangleBindings[0].Semantics!.Value.FixedFunction.PackedRetailState);
        Assert.Equal(0xD441Bu, Assert.Single(effective.Material.Source.Shaders).FixedFunction.PackedRetailState);
        Assert.Equal(0xD441Bu,effective.Material.Source.CurrentRetailUniformShader);
        Assert.Same(generated.Image,effective.Material.Source.TextureStages[0].CurrentRetailUniformTexture!.CurrentRetailResolvedTexture!.Image);
        Assert.Same(generated.TextureState,effective.Material.Source.TextureStages[0].CurrentRetailUniformTexture!.CurrentRetailTextureState);
        Assert.Same(generated.TextureState,Assert.Single(effective.CurrentRetailLoadedTextures!));
        Assert.Equal(7u,generated.TextureState.RetailCreationFormat);
        Assert.Equal(1u,generated.TextureState.RetailCreationMipLevels);
        Assert.Equal(EnbRenderTextureMinMagFilter.Linear,generated.TextureState.Sampling.CurrentRetailMinMagOverride);
        Assert.Same(effective.Material.Source.VertexMaterials[0].Material,effective.Material.Source.CurrentRetailUniformMaterial!.Value.Material);
        Assert.Same(effective.Material.Source.CurrentRetailUniformMaterial.Value.Material,Assert.Single(effective.CurrentRetailLoadedMaterials!));
        Assert.Same(effective.Material.Source.VertexMaterials[0].Material,effective.Material.Source.TriangleBindings[0].VertexMaterial!.Value.Material);
        Assert.Same(effective.Material.Source.TextureStages[0].Textures[0],effective.Material.Source.TextureStages[0].CurrentRetailUniformTexture);
        Assert.Same(effective.Material.Source.TextureStages[0].Textures[0],effective.Material.Source.TriangleBindings[0].Textures[0]);
        Assert.Equal(EnbW3dRenderBindingCardinality.Uniform, effective.Material.Source.ShaderCardinality);
        var shader = effective.Material.Source.SingleBindingSemantics!.Value;
        Assert.Equal(shader.FixedFunction.AlphaTest, shader.PermutationKey.AlphaTest);
        Assert.Equal(shader.FixedFunction.DepthWriteEnabled, shader.PermutationKey.DepthWriteEnabled);
        EnbW3dRenderTextureBinding texture = effective.Material.Source.TextureStages[0].Textures[0];
        Assert.Equal(generated.LogicalName, texture.LogicalName);
        Assert.True(texture.ResolveLogicalNameAtRuntime);
        Assert.Equal(EnbW3dRenderBindingSourceKind.PackedRetailState, texture.SourceKind);
        Assert.Equal((byte)1, texture.Sampling.RetailCreationSelector);
        Assert.Equal(EnbRenderTextureAddressMode.Wrap, texture.Sampling.AddressU);
        Assert.Equal(EnbRenderTextureAddressMode.Wrap, texture.Sampling.AddressV);
        Assert.Equal(EnbRenderTextureMinMagFilter.Linear, texture.Sampling.CurrentRetailMinMagOverride);

        EnbW3dTextureSamplerSemantics sampler = EnbW3dTextureSamplerCompiler.Compile(
            texture,
            new EnbRenderTextureRuntimeSettings(0, EnbRenderTextureFilterMode.Anisotropic),
            EnbRenderTextureBackendCapabilities.ModernDefault);
        Assert.Equal(EnbRenderTextureMinMagFilter.Linear, sampler.MinFilter);
        Assert.Equal(EnbRenderTextureMinMagFilter.Linear, sampler.MagFilter);
        Assert.Equal(EnbRenderTextureMipFilter.None, sampler.MipFilter);

        EnbW3dCompiledMaterialSemantics semantics = effective.Material.Source.TriangleBindings[0].Semantics!.Value;
        Assert.Equal(state.Rgb, semantics.Uniforms.Ambient);
        Assert.Equal(state.Rgb, semantics.Uniforms.Diffuse);
        Assert.Equal(1f, semantics.Uniforms.DiffuseAlpha);
        EnbW3dRenderMaterialUniformOverride material = Assert.IsType<EnbW3dRenderMaterialUniformOverride>(
            effective.Material.Source.VertexMaterials[0].Material.CurrentRetailUniformOverride);
        Assert.Equal(state.Rgb, material.Ambient);
        Assert.Equal(state.Rgb, material.Diffuse);
        Assert.Equal(1f, material.DiffuseAlpha);
    }

    [Fact]
    public void NonTargetAndWrongRefinedHeightRemainFailClosed()
    {
        var state = new EnbSceneNameDecalSnapshot(
            "A",
            new byte[] { (byte)'A', 0 }.ToImmutableArray(),
            Vector3.One);
        Assert.False(EnbCurrentRetailShipNameDecalRuntime.TryBuildTexture(
            state,
            CreateFontAtlas(fullRetailHeight: false),
            "runtime.argb4444",
            out _,
            out string? diagnostic));
        Assert.Contains("exactly 16", diagnostic, StringComparison.OrdinalIgnoreCase);

        EnbW3dDrawTask nonTarget = CreatePacket("SHIP.DECAL020");
        Assert.Same(nonTarget, EnbCurrentRetailShipNameDecalRuntime.Apply(
            nonTarget,
            state,
            generatedTexture: null,
            out EnbCurrentRetailShipNameDecalReceipt receipt));
        Assert.Equal(EnbCurrentRetailShipNameDecalOutcome.NotNameDecalRenderObject, receipt.Outcome);
    }

    [Fact]
    public void RepeatedNamePacketReplacesMaterialWhileRedrawRetainsIt()
    {
        var state = new EnbSceneNameDecalSnapshot("AB", [(byte)'A', (byte)'B', 0], Vector3.One);
        Assert.True(EnbCurrentRetailShipNameDecalRuntime.TryBuildTexture(state, CreateFontAtlas(true),
            "runtime.argb4444", out var texture, out var diagnostic), diagnostic);
        var packet = CreatePacket("SHIP.NAMEDECAL0");
        var cache = new EnbShipMaterialCache();
        cache.BeginFrame();
        var first = cache.ApplyName(packet, state, texture, out _, 1);
        Assert.Same(first.Material, cache.ApplyName(packet, state, texture, out _, 1).Material);
        var second = cache.ApplyName(packet, state, texture, out _, 2);
        Assert.NotSame(first.Material, second.Material);
        Assert.Equal(2, second.CurrentRetailNameDecalRevision);
        Assert.Equal(first.VisualGeneration, second.VisualGeneration);
        Assert.Same(second.Material, cache.ApplyName(packet, state, texture, out _, 2).Material);
        var unrelated = CreatePacket("SHIP.HULL");
        Assert.Equal(0, cache.ApplyName(unrelated, state, texture, out _, 2).CurrentRetailNameDecalRevision);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void NameMutationRejectsExpandedBindingsAndPreservesAdditionalPasses(int variant)
    {
        var name = new EnbSceneNameDecalSnapshot("AB", [(byte)'A', (byte)'B', 0], Vector3.One);
        Assert.True(EnbCurrentRetailShipNameDecalRuntime.TryBuildTexture(name, CreateFontAtlas(true),
            "runtime.argb4444", out var texture, out _));
        var source = CreatePacket("SHIP.NAMEDECAL0");
        var pass = source.Material.Source;
        pass = variant switch
        {
            0 => (pass).Copy().SetTextureStages([(pass.TextureStages[0]).Copy().SetBindingCardinality(EnbW3dRenderBindingCardinality.PerTriangle)]),
            1 => (pass).Copy().SetVertexMaterialCardinality(EnbW3dRenderBindingCardinality.PerVertex),
            2 => (pass).Copy().SetShaderCardinality(EnbW3dRenderBindingCardinality.PerTriangle),
            _ => (pass).Copy().SetOrdinal(1)
        };
        source = (source).ForkTask().SetMaterial(new(pass, pass.SingleBindingSemantics));
        var updated = EnbCurrentRetailShipNameDecalRuntime.Apply(source, name, texture, out var receipt);
        Assert.False(receipt.Applied);
        Assert.Same(source, updated);
        Assert.Equal(variant == 3 ? EnbCurrentRetailShipNameDecalOutcome.AdditionalMaterialPass : EnbCurrentRetailShipNameDecalOutcome.ExpandedBindings, receipt.Outcome);
        Assert.Same(pass.TextureStages[0], updated.Material.Source.TextureStages[0]);
        Assert.Same(pass.TriangleBindings[0].Textures[0], updated.Material.Source.TriangleBindings[0].Textures[0]);
    }
    [Fact]
    public void NameCacheRetainsPrivateTextureTableAndReplacesItOnNewGeneration()
    {
        var name=new EnbSceneNameDecalSnapshot("AB",[(byte)'A',(byte)'B',0],Vector3.One);
        var source=CreatePacket("SHIP.NAMEDECAL0");
        Assert.True(EnbCurrentRetailShipNameDecalRuntime.TryBuildTexture(name,CreateFontAtlas(true),"name",out var texture,out _));
        Assert.True(EnbCurrentRetailShipNameDecalRuntime.TryBuildTexture(name,CreateFontAtlas(true),"name",out var next,out _));
        Assert.NotSame(texture.TextureState,next.TextureState);
        var cache=new EnbShipMaterialCache(); cache.BeginFrame();
        var first=cache.ApplyName(source,name,texture,out _,1);
        var repeated=cache.ApplyName(source,name,texture,out _,1);
        Assert.Same(first.CurrentRetailLoadedTextures,repeated.CurrentRetailLoadedTextures);
        var changed=cache.ApplyName(source,name,next,out _,2);
        Assert.Same(next.TextureState,Assert.Single(changed.CurrentRetailLoadedTextures!));
        Assert.NotSame(first.CurrentRetailLoadedTextures![0],changed.CurrentRetailLoadedTextures![0]);
    }

    private static EnbTextureImage CreateFontAtlas(bool fullRetailHeight)
    {
        const int size = 256;
        var pixels = new EnbColor32[size * size];
        int height = fullRetailHeight ? 16 : 15;
        SetGlyph(pixels, size, (byte)'A', leftInset: 1, width: 2, height, new EnbColor32(255, 128, 1, 255));
        SetGlyph(pixels, size, (byte)'B', leftInset: 1, width: 3, height, new EnbColor32(17, 34, 51, 255));
        return EnbTextureImage.CreateCurrentRetailRuntimeArgb4444(size, size, pixels);
    }

    private static void SetGlyph(
        EnbColor32[] pixels,
        int atlasWidth,
        byte value,
        int leftInset,
        int width,
        int height,
        EnbColor32 color)
    {
        int cellX = (value & 0x0F) * 16;
        int cellY = (value >> 4) * 16;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            pixels[((cellY + y) * atlasWidth) + cellX + leftInset + x] = color;
    }

    private static EnbW3dDrawTask CreatePacket(string renderObjectName, string materialName = "~name")
    {
        var uniforms = new EnbW3dMaterialUniforms(
            new Vector3(.1f), new Vector3(.9f), new Vector3(.3f), new Vector3(.05f),
            8f, 1f, EnbW3dMapperUniforms.Identity, EnbW3dMapperUniforms.Identity);
        EnbW3dCompiledMaterialSemantics semantics = default(EnbW3dCompiledMaterialSemantics) with { Uniforms = uniforms };
        var material = new EnbW3dRenderVertexMaterial(
            0, materialName, 0,
            new EnbRenderPackedRgb8(26, 26, 26, 0),
            new EnbRenderPackedRgb8(230, 230, 230, 0),
            new EnbRenderPackedRgb8(77, 77, 77, 0),
            new EnbRenderPackedRgb8(13, 13, 13, 0),
            8f, 1f, 0f, null, null);
        var materialBinding = new EnbW3dRenderVertexMaterialBinding(0, material);
        var texture = new EnbW3dRenderTextureBinding(
            0, "authored.tga", new EnbW3dRenderTextureLocation("authored.tga", "test.mix", 0, 1));
        var stage = new EnbW3dRenderTextureStage(
            0, new[] { texture }, Array.Empty<Vector2>(), EnbW3dRenderBindingCardinality.Uniform){CurrentRetailUniformTexture=texture};
        var triangle = new EnbW3dRenderTriangleBinding(
            0, 0, materialBinding, null, new EnbW3dRenderTextureBinding?[] { texture }, semantics);
        var pass = new EnbW3dRenderMaterialPass(
            0,
            new[] { materialBinding },
            EnbW3dRenderBindingCardinality.Uniform,
            Array.Empty<EnbW3dRenderShaderBinding>(),
            EnbW3dRenderBindingCardinality.None,
            Array.Empty<EnbRenderColor32>(),
            Array.Empty<EnbRenderColor32>(),
            Array.Empty<EnbRenderColor32>(),
            new[] { stage },
            new[] { triangle },
            semantics){CurrentRetailUniformMaterial=materialBinding};
        var mesh = new EnbW3dRenderMesh(
            0, "NAMEDECAL0", "SHIP", 1f,
            new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY },
            new[] { Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ },
            Array.Empty<ushort>(),
            new uint[] { 0, 1, 2 },
            new[] { new EnbW3dRenderTriangleMetadata(0, Vector3.UnitZ, 0f) },
            new[] { material },
            new[] { pass });
        return new EnbW3dDrawTask(
            default, 9, 1, 9, Matrix4x4.Identity,
            new EnbW3dWorldBounds(Vector3.Zero, 1f),
            mesh, null, new EnbW3dDrawMaterialBinding(pass, semantics),
            EnbW3dLightEnvironment.BlackNoLights, 0, null, 0, renderObjectName);
    }
}
