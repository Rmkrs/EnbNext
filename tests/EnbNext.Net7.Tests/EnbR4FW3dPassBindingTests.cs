using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR4FW3dPassBindingTests
{
    [Fact]
    public void CompiledMeshBorrowsOnlyDecoderOwnedImmutableVertexStream()
    {
        var bytes = BuildMesh([0u], [0u], [0u], false);
        Assert.True(EnbW3dModel.TryDecode(bytes, out var model));
        var authored = Assert.Single(model.Meshes);
        Assert.IsType<System.Collections.Immutable.ImmutableArray<Vector3>>(authored.Vertices);
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(Resolve(bytes, model), out var resource, out var why), why);
        Assert.Same(authored.Vertices, Assert.Single(resource.Meshes).Positions);
        Assert.Equal(authored.Vertices.ToArray(), Assert.Single(resource.Meshes).Positions.ToArray());
    }

    [Fact]
    public void AuthoredNonfiniteUvSurvivesRetailImportAndDescriptorCompilation()
    {
        byte[] coordinates=UInt32Payload([0x7fc12345u,0x3e800000u,0xff800000u,0x7f800000u,0u,0xffc00000u,0x3f800000u,0u]);
        var bytes=BuildMesh([0u],[0u],[0u],false,uvPayload:coordinates);
        Assert.True(EnbW3dModel.TryDecode(bytes,out var model));
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(Resolve(bytes,model),out var resource,out var why),why);
        var uv=Assert.Single(Assert.Single(resource.Meshes).MaterialPasses).TextureStages[0].EffectiveRuntimeTexCoords;
        Assert.Equal(4,uv.Count);
        Assert.Equal(0x7fc12345u,BitConverter.SingleToUInt32Bits(uv[0].X));Assert.Equal(.75f,uv[0].Y);
        Assert.Equal(float.NegativeInfinity,uv[1].X);Assert.Equal(float.NegativeInfinity,uv[1].Y);
        Assert.True(float.IsNaN(uv[2].Y));Assert.Equal(Vector2.One,uv[3]);
    }

    [Theory]
    [InlineData("GST08.w3d",6)] [InlineData("NPC15A.w3d",1)]
    public void InstalledNonfiniteUvDoesNotEraseMeshPrototypes(string fileName,int expectedMeshes)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        Assert.True(assets.FileResources.TryRead(fileName,out var bytes,out _,out _));
        Assert.True(EnbCurrentRetailW3dPrototypeFile.TryDecode(bytes,out var file,out var why),why);
        Assert.False(file.Model.HasIncompleteRoots);Assert.False(file.HasUnrepresentedRenderPrototypes);
        int affected=file.Model.Meshes.Count(m=>m.MaterialPasses.Any(p=>p.TextureStages.Any(s=>s.RuntimeTexCoords.Any(uv=>!float.IsFinite(uv.X)||!float.IsFinite(uv.Y)))));
        Assert.Equal(expectedMeshes,affected);
    }

    [Fact]
    public void MeshHsvSkipsAuthoredSpecularChunkAndPreservesItsBytes()
    {
        var textures = EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!).Textures;
        EnbW3dRenderResource Adjust(bool includeSpecular)
        {
            var bytes = BuildMesh([0u, 1u, 1u, 0u], [0u, 1u], [0u, uint.MaxValue], includeSpecular, "gaspocket01.tga");
            Assert.True(EnbW3dModel.TryDecode(bytes, out var model));
            Assert.True(EnbW3dRenderResourceCompiler.TryCompile(Resolve(bytes, model), out var source, out var error), error);
            var provenance = Assert.Single(Assert.Single(source.Meshes).MaterialPasses).SpecularColors;
            var adjusted = EnbCurrentRetailEffectMeshHsvRuntime.Apply(source, textures, new(120, .1f, 0));
            Assert.Same(provenance, Assert.Single(Assert.Single(adjusted.Meshes).MaterialPasses).SpecularColors);
            return adjusted;
        }
        var ignored = Assert.Single(Adjust(true).Meshes);
        var absent = Assert.Single(Adjust(false).Meshes);
        Assert.Equal(4, ignored.MaterialPasses[0].SpecularColors.Count);
        Assert.Empty(absent.MaterialPasses[0].SpecularColors);
        Assert.Equal(ignored.EffectiveColor1, absent.EffectiveColor1);
        Assert.Equal(ignored.Positions, absent.Positions);
        Assert.Equal(ignored.Normals, absent.Normals);
        Assert.Equal(ignored.VertexMaterials.Select(m => m.CurrentRetailUniformOverride),
            absent.VertexMaterials.Select(m => m.CurrentRetailUniformOverride));
        Assert.Equal(ignored.MaterialPasses[0].TriangleBindings.Select(t => t.Semantics),
            absent.MaterialPasses[0].TriangleBindings.Select(t => t.Semantics));
        Assert.Equal(ignored.MaterialPasses[0].SingleBindingSemantics, absent.MaterialPasses[0].SingleBindingSemantics);
    }

    [Fact]
    public void ModelCopyRemapsExpandedPointersAndPreservesHiddenUniformSlots()
    {
        var bytes = BuildMesh([0u, 1u, 1u, 0u], [0u, 1u], [0u, uint.MaxValue], false);
        Assert.True(EnbW3dModel.TryDecode(bytes, out var model));
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(Resolve(bytes, model), out var resource, out var error), error);
        var mesh = Assert.Single(resource.Meshes);
        var original = Assert.Single(mesh.MaterialPasses);
        var loaded = original.VertexMaterials[0].Material;
        using var source = new EnbCurrentRetailMeshModelOwner([(loaded, null, null)], loadedTextures: [new object()]);
        using var copy = source.CloneRenderObject();
        copy.EnsureUniqueModel(100);
        Assert.True(EnbW3dRenderResourceCompiler.TryCopyModelDescriptor(mesh, source, copy, out var passes, out error), error);
        var pass = Assert.Single(passes);
        Assert.Equal(EnbW3dRenderBindingCardinality.PerVertex, pass.VertexMaterialCardinality);
        Assert.Same(copy.MaterialIdentity(loaded), pass.VertexMaterials[0].Material);
        Assert.True(pass.VertexMaterials[1].IsUnbound);
        Assert.Null(pass.TriangleBindings[1].VertexMaterial);
        Assert.True(pass.TriangleBindings[1].CurrentRetailNullMaterial);
        Assert.False(pass.TriangleBindings[0].CurrentRetailNullMaterial);
        Assert.Equal(EnbW3dVertexLightingSemantics.RetailNullMaterial, pass.TriangleBindings[1].Semantics!.Value.VertexLighting);
        Assert.Equal(1f, pass.TriangleBindings[1].Semantics!.Value.Uniforms.Power);
        Assert.Equal(original.CurrentRetailUniformMaterial, pass.CurrentRetailUniformMaterial);
        Assert.Same(original.TextureStages[0].CurrentRetailUniformTexture, pass.TextureStages[0].CurrentRetailUniformTexture);
        Assert.All(pass.TextureStages[0].Textures, texture => Assert.True(texture.IsUnbound));
        Assert.All(pass.TriangleBindings, triangle => Assert.All(triangle.Textures, Assert.Null));
        Assert.False(original.VertexMaterials[1].IsUnbound);
        Assert.NotNull(original.TriangleBindings[1].VertexMaterial);
        Assert.NotSame(original.VertexMaterials, pass.VertexMaterials);
        Assert.NotSame(original.Shaders, pass.Shaders);
        Assert.Same(original.DiffuseColors, pass.DiffuseColors);
        // Uniform replacement must not revive the null expanded entry.
        var copiedMesh = (mesh).Copy().SetMaterialPasses(passes);
        Assert.True(EnbW3dRenderResourceCompiler.TryReplaceUniformDescriptor(copiedMesh, [original], out var replaced, out error), error);
        Assert.Null(replaced[0].TriangleBindings[1].VertexMaterial);
        Assert.Equal(EnbW3dVertexLightingSemantics.RetailNullMaterial, replaced[0].TriangleBindings[1].Semantics!.Value.VertexLighting);

        using var empty = new EnbCurrentRetailMeshModelOwner([]);
        using var emptyCopy = empty.CloneRenderObject();
        emptyCopy.EnsureUniqueModel(100);
        Assert.True(EnbW3dRenderResourceCompiler.TryCopyModelDescriptor(mesh, empty, emptyCopy, out passes, out error), error);
        Assert.Same(loaded, passes[0].VertexMaterials[0].Material);
        Assert.False(passes[0].VertexMaterials[1].IsUnbound);
        Assert.Same(original.TextureStages[0].Textures[0], passes[0].TextureStages[0].Textures[0]);
    }

    [Fact]
    public void NullDescriptorMaterialDisablesLightingWithoutChangingShaderOrExpandedMaterials()
    {
        byte[] bytes = BuildMesh([0u], [0u], [0u], false);
        Assert.True(EnbW3dModel.TryDecode(bytes, out var model));
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(Resolve(bytes, model), out var resource, out var error), error);
        var mesh = Assert.Single(resource.Meshes);
        var original = Assert.Single(mesh.MaterialPasses);
        var incoming = (original).Copy().SetCurrentRetailUniformMaterial(null);
        Assert.True(EnbW3dRenderResourceCompiler.TryReplaceUniformDescriptor(mesh, [incoming], out var replaced, out error), error);
        var pass = Assert.Single(replaced);
        Assert.Null(pass.CurrentRetailUniformMaterial);
        Assert.False(pass.SingleBindingSemantics!.Value.VertexLighting.LightingEnabled);
        Assert.Equal(1f, pass.SingleBindingSemantics.Value.Uniforms.Power);
        Assert.All(pass.TriangleBindings, triangle =>
        {
            Assert.Null(triangle.VertexMaterial);
            Assert.True(triangle.UsesImplicitVertexMaterial);
            Assert.Equal(EnbW3dVertexLightingSemantics.RetailNullMaterial, triangle.Semantics!.Value.VertexLighting);
            Assert.Equal(original.CurrentRetailUniformShader, triangle.Semantics.Value.FixedFunction.PackedRetailState);
        });
        Assert.Equal(new[] { 0, 1 }, pass.TextureStages.Select(stage => stage.EffectiveTextureCoordinateSetIndex));

        bytes = BuildMesh([0u, 1u, 1u, 0u], [0u], [0u], false);
        Assert.True(EnbW3dModel.TryDecode(bytes, out model));
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(Resolve(bytes, model), out resource, out error), error);
        mesh = Assert.Single(resource.Meshes);
        original = Assert.Single(mesh.MaterialPasses);
        Assert.True(EnbW3dRenderResourceCompiler.TryReplaceUniformDescriptor(mesh, [incoming], out replaced, out error), error);
        Assert.Equal(original.TriangleBindings.Select(t => t.VertexMaterial), replaced[0].TriangleBindings.Select(t => t.VertexMaterial));
        Assert.Equal(original.TriangleBindings.Select(t => t.Semantics!.Value.VertexLighting), replaced[0].TriangleBindings.Select(t => t.Semantics!.Value.VertexLighting));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(uint.MaxValue)]
    public void RepeatedTextureIdsRetainOneDefinitionWithoutCollapsingExpandedCardinality(uint id)
    {
        byte[] bytes=BuildMesh([0u],[0u],[id,id],false);
        Assert.True(EnbW3dModel.TryDecode(bytes,out var model));
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(Resolve(bytes,model),out var resource,out var error),error);
        var pass=Assert.Single(Assert.Single(resource.Meshes).MaterialPasses);
        var stage=Assert.Single(pass.TextureStages);
        Assert.Equal(EnbW3dRenderBindingCardinality.PerTriangle,stage.BindingCardinality);
        Assert.Equal(2,stage.Textures.Count);
        Assert.Same(stage.Textures[0],stage.Textures[1]);
        Assert.Equal(id,stage.Textures[0].SourceId);
        Assert.Equal(2,pass.TriangleBindings.Count);
        Assert.Equal(new[]{0,1},pass.TriangleBindings.Select(t=>t.TriangleOrdinal));
        if(id==0)Assert.Same(pass.TriangleBindings[0].Textures[0],pass.TriangleBindings[1].Textures[0]);
    }

    [Fact]
    public void ExpandedBindingsResolveMaterialFromTriangleFirstVertexAndShaderTextureFromTriangleIndex()
    {
        byte[] bytes = BuildMesh(
            materialIds: [0u, 1u, 1u, 0u],
            shaderIds: [0u, 1u],
            textureIds: [0u, uint.MaxValue],
            includeSpecularColorChunk: true);

        Assert.True(EnbW3dModel.TryDecode(bytes, out EnbW3dModel model));
        EnbResolvedW3dResource source = Resolve(bytes, model);
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(source, out EnbW3dRenderResource resource, out string? diagnostic), diagnostic);

        EnbW3dRenderMaterialPass pass = Assert.Single(Assert.Single(resource.Meshes).MaterialPasses);
        Assert.Equal(EnbW3dRenderBindingCardinality.PerVertex, pass.VertexMaterialCardinality);
        Assert.Equal(EnbW3dRenderBindingCardinality.PerTriangle, pass.ShaderCardinality);
        EnbW3dRenderTextureStage stage = Assert.Single(pass.TextureStages);
        Assert.Equal(EnbW3dRenderBindingCardinality.PerTriangle, stage.BindingCardinality);

        Assert.Equal(2, pass.TriangleBindings.Count);

        EnbW3dRenderTriangleBinding first = pass.TriangleBindings[0];
        Assert.Equal(0, first.TriangleOrdinal);
        Assert.Equal(0, first.MaterialVertexIndex);
        Assert.Equal((uint)0, first.VertexMaterial!.Value.SourceId);
        Assert.Equal((uint)0, first.Shader!.Value.SourceId);
        EnbW3dRenderTextureBinding firstTexture = Assert.IsType<EnbW3dRenderTextureBinding>(Assert.Single(first.Textures));
        Assert.Equal((uint)0, firstTexture.SourceId);
        Assert.False(firstTexture.IsUnbound);
        Assert.False(firstTexture.IsMissing);
        Assert.NotNull(first.Semantics);

        // Triangle 1 starts at vertex 1, so current retail resolves its expanded
        // vertex-material stream through materialIds[1], while shader/texture
        // streams are indexed directly by triangle ordinal.
        EnbW3dRenderTriangleBinding second = pass.TriangleBindings[1];
        Assert.Equal(1, second.TriangleOrdinal);
        Assert.Equal(1, second.MaterialVertexIndex);
        Assert.Equal((uint)1, second.VertexMaterial!.Value.SourceId);
        Assert.Equal((uint)1, second.Shader!.Value.SourceId);
        EnbW3dRenderTextureBinding secondTexture = Assert.IsType<EnbW3dRenderTextureBinding>(Assert.Single(second.Textures));
        Assert.True(secondTexture.IsUnbound);
        Assert.False(secondTexture.IsMissing);
        Assert.NotNull(second.Semantics);

        // 0x003E is preserved as source provenance, but the active current-retail
        // pass loader skips it. Effective material semantics therefore continue
        // to come from the selected vertex material rather than this color stream.
        Assert.Equal(4, pass.SpecularColors.Count);
        Vector3 expectedMaterialSpecular = new(7f / 255f, 8f / 255f, 9f / 255f);
        Assert.InRange(
            Vector3.Distance(expectedMaterialSpecular, first.Semantics!.Value.Uniforms.Specular), 0f, 1e-7f);
    }

    [Fact]
    public void UniformBindingsRemainUniformEvenForSingleTriangleGeometry()
    {
        byte[] bytes = BuildSingleTriangleMesh();
        Assert.True(EnbW3dModel.TryDecode(bytes, out EnbW3dModel model));
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(Resolve(bytes, model), out EnbW3dRenderResource resource, out string? diagnostic), diagnostic);

        EnbW3dRenderMaterialPass pass = Assert.Single(Assert.Single(resource.Meshes).MaterialPasses);
        Assert.Equal(EnbW3dRenderBindingCardinality.Uniform, pass.VertexMaterialCardinality);
        Assert.Equal(EnbW3dRenderBindingCardinality.Uniform, pass.ShaderCardinality);
        Assert.Equal(EnbW3dRenderBindingCardinality.Uniform, Assert.Single(pass.TextureStages).BindingCardinality);
        Assert.NotNull(pass.SingleBindingSemantics);
        Assert.NotNull(Assert.Single(pass.TriangleBindings).Semantics);
    }

    [Theory]
    [InlineData(2, 1, 1)] // material IDs must be one or vertex-count (4)
    [InlineData(1, 3, 1)] // shader IDs must be one or triangle-count (2)
    [InlineData(1, 1, 3)] // texture IDs must be one or triangle-count (2)
    public void DecoderRejectsExpandedBindingStreamsWithNonRetailCardinality(
        int materialIdCount,
        int shaderIdCount,
        int textureIdCount)
    {
        uint[] materialIds = Enumerable.Repeat(0u, materialIdCount).ToArray();
        uint[] shaderIds = Enumerable.Repeat(0u, shaderIdCount).ToArray();
        uint[] textureIds = Enumerable.Repeat(0u, textureIdCount).ToArray();

        byte[] bytes = BuildMesh(materialIds, shaderIds, textureIds, includeSpecularColorChunk: false);
        Assert.False(EnbW3dModel.TryDecode(bytes, out _));
    }

    [Fact]
    public void UniformTextureMinusOneIsRejectedButExpandedPerTriangleMinusOneIsExplicitlyUnbound()
    {
        Assert.False(EnbW3dModel.TryDecode(
            BuildMesh([0u], [0u], [uint.MaxValue], includeSpecularColorChunk: false),
            out _));

        byte[] expanded = BuildMesh([0u], [0u], [0u, uint.MaxValue], includeSpecularColorChunk: false);
        Assert.True(EnbW3dModel.TryDecode(expanded, out EnbW3dModel model));
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(Resolve(expanded, model), out EnbW3dRenderResource resource, out string? diagnostic), diagnostic);

        EnbW3dRenderMaterialPass pass = Assert.Single(Assert.Single(resource.Meshes).MaterialPasses);
        Assert.False(pass.TriangleBindings[0].Textures[0]!.IsUnbound);
        Assert.True(pass.TriangleBindings[1].Textures[0]!.IsUnbound);
    }

    [Fact]
    public void R4FBindingLayerRemainsBackendNeutral()
    {
        string root = FindRepositoryRoot();
        string resourceSource = File.ReadAllText(Path.Combine(root, "src", "EnbNext.Rendering", "EnbW3dRenderResource.cs"));
        string packetSource = File.ReadAllText(Path.Combine(root, "src", "EnbNext.Rendering", "EnbW3dDrawPackets.cs"));

        Assert.Contains("EnbW3dRenderTriangleBinding", resourceSource, StringComparison.Ordinal);
        Assert.Contains("MaterialVertexIndex", resourceSource, StringComparison.Ordinal);
        Assert.Contains("TriangleBindings", packetSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Veldrid", resourceSource, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Silk.NET", resourceSource, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OpenGL", resourceSource, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Vulkan", resourceSource, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] BuildMesh(
        IReadOnlyList<uint> materialIds,
        IReadOnlyList<uint> shaderIds,
        IReadOnlyList<uint> textureIds,
        bool includeSpecularColorChunk,
        string textureName = "R4F.TGA",byte[]? uvPayload=null)
    {
        Vector3[] vertices =
        [
            new(0, 0, 0),
            new(1, 0, 0),
            new(1, 1, 0),
            new(0, 1, 0),
        ];
        Vector3[] normals = Enumerable.Repeat(Vector3.UnitZ, vertices.Length).ToArray();

        byte[] shader = [3, 1, 0, 0, 2, 1, 0, 1, 1, 2, 0, 2, 0, 2, 0, 2];
        byte[] shaderRecords = Concat(shader, shader);

        byte[] pass = ContainerChunk(
            0x0038,
            LeafChunk(0x0039, UInt32Payload(materialIds)),
            LeafChunk(0x003A, UInt32Payload(shaderIds)),
            includeSpecularColorChunk
                ? LeafChunk(0x003E, ColorPayload(vertices.Length, 240, 10, 20, 255))
                : Array.Empty<byte>(),
            ContainerChunk(0x0048, LeafChunk(0x0049, UInt32Payload(textureIds)),uvPayload is null?[]:LeafChunk(0x004a,uvPayload)));

        return ContainerChunk(
            0x0000,
            LeafChunk(0x001F, BuildMeshHeader("R4F_MESH", "R4F_ROOT", 2, 4, 2f)),
            LeafChunk(0x0002, Vector3Payload(vertices)),
            LeafChunk(0x0003, Vector3Payload(normals)),
            LeafChunk(0x0020, Concat(
                TrianglePayload(0, 1, 2),
                TrianglePayload(1, 2, 3))),
            LeafChunk(0x0028, UInt32Payload([1u, 2u, 2u, 1u])),
            LeafChunk(0x0029, shaderRecords),
            ContainerChunk(
                0x002A,
                VertexMaterial("MAT0", diffuse: (20, 30, 40)),
                VertexMaterial("MAT1", diffuse: (80, 90, 100))),
            ContainerChunk(
                0x0030,
                ContainerChunk(0x0031, LeafChunk(0x0032, Encoding.ASCII.GetBytes(textureName + "\0")))),
            pass);
    }

    private static byte[] BuildSingleTriangleMesh()
    {
        Vector3[] vertices = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY];
        Vector3[] normals = Enumerable.Repeat(Vector3.UnitZ, vertices.Length).ToArray();
        byte[] shader = [3, 1, 0, 0, 2, 1, 0, 1, 1, 2, 0, 2, 0, 2, 0, 2];

        return ContainerChunk(
            0x0000,
            LeafChunk(0x001F, BuildMeshHeader("R4F_ONE", "R4F_ROOT", 1, 3, 1f)),
            LeafChunk(0x0002, Vector3Payload(vertices)),
            LeafChunk(0x0003, Vector3Payload(normals)),
            LeafChunk(0x0020, TrianglePayload(0, 1, 2)),
            LeafChunk(0x0028, UInt32Payload([1u, 1u, 1u, 1u])),
            LeafChunk(0x0029, shader),
            ContainerChunk(0x002A, VertexMaterial("MAT0", diffuse: (20, 30, 40))),
            ContainerChunk(0x0030, ContainerChunk(0x0031, LeafChunk(0x0032, Encoding.ASCII.GetBytes("R4F.TGA\0")))),
            ContainerChunk(
                0x0038,
                LeafChunk(0x0039, UInt32Payload([0u])),
                LeafChunk(0x003A, UInt32Payload([0u])),
                ContainerChunk(0x0048, LeafChunk(0x0049, UInt32Payload([0u])))));
    }

    private static EnbResolvedW3dResource Resolve(byte[] bytes, EnbW3dModel model)
    {
        string archivePath = Path.Combine(Path.GetTempPath(), "r4f_mixfile_art.mix");
        return new EnbResolvedW3dResource(
            "R4F.W3D",
            "R4F_ROOT",
            new EnbMixResourceLocation("R4F.W3D", archivePath, 1024, bytes.Length),
            model,
            [new EnbResolvedTextureReference(
                "R4F.TGA",
                new EnbMixResourceLocation("R4F.DDS", archivePath, 4096, 128))]);
    }

    private static byte[] VertexMaterial(string name, (byte R, byte G, byte B) diffuse)
    {
        byte[] info = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(0, 4), 0u);
        new byte[] { 1, 2, 3, 0 }.CopyTo(info, 4);
        new byte[] { diffuse.R, diffuse.G, diffuse.B, 0 }.CopyTo(info, 8);
        new byte[] { 7, 8, 9, 0 }.CopyTo(info, 12);
        new byte[] { 10, 11, 12, 0 }.CopyTo(info, 16);
        WriteSingle(info.AsSpan(20, 4), 1f);
        WriteSingle(info.AsSpan(24, 4), 1f);
        WriteSingle(info.AsSpan(28, 4), 0f);
        return ContainerChunk(
            0x002B,
            LeafChunk(0x002C, Encoding.ASCII.GetBytes(name + "\0")),
            LeafChunk(0x002D, info));
    }

    private static byte[] BuildMeshHeader(string name, string container, uint triangleCount, uint vertexCount, float radius)
    {
        byte[] header = new byte[116];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), 0x0004_0002u);
        WriteFixedAscii(header.AsSpan(8, 16), name);
        WriteFixedAscii(header.AsSpan(24, 16), container);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40, 4), triangleCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(44, 4), vertexCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(68, 4), EnbW3dVertexChannels.Location | EnbW3dVertexChannels.Normal);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(72, 4), EnbW3dFaceChannels.Face);
        WriteSingle(header.AsSpan(112, 4), radius);
        return header;
    }

    private static byte[] TrianglePayload(uint v0, uint v1, uint v2)
    {
        byte[] bytes = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), v0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), v1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), v2);
        WriteSingle(bytes.AsSpan(24, 4), 1f);
        return bytes;
    }

    private static byte[] UInt32Payload(IEnumerable<uint> values)
    {
        uint[] snapshot = values.ToArray();
        byte[] bytes = new byte[snapshot.Length * 4];
        for (int index = 0; index < snapshot.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * 4, 4), snapshot[index]);
        return bytes;
    }

    private static byte[] ColorPayload(int count, byte r, byte g, byte b, byte a)
    {
        byte[] bytes = new byte[count * 4];
        for (int index = 0; index < count; index++)
        {
            int offset = index * 4;
            bytes[offset] = r;
            bytes[offset + 1] = g;
            bytes[offset + 2] = b;
            bytes[offset + 3] = a;
        }
        return bytes;
    }

    private static byte[] Vector3Payload(IEnumerable<Vector3> values)
    {
        Vector3[] snapshot = values.ToArray();
        byte[] bytes = new byte[snapshot.Length * 12];
        for (int index = 0; index < snapshot.Length; index++)
        {
            WriteSingle(bytes.AsSpan(index * 12, 4), snapshot[index].X);
            WriteSingle(bytes.AsSpan((index * 12) + 4, 4), snapshot[index].Y);
            WriteSingle(bytes.AsSpan((index * 12) + 8, 4), snapshot[index].Z);
        }
        return bytes;
    }

    private static byte[] LeafChunk(uint type, byte[] payload)
    {
        if (payload.Length == 0)
            return Array.Empty<byte>();
        byte[] bytes = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), type);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), (uint)payload.Length);
        payload.CopyTo(bytes, 8);
        return bytes;
    }

    private static byte[] ContainerChunk(uint type, params byte[][] children)
    {
        byte[] payload = Concat(children.Where(static child => child.Length != 0).ToArray());
        byte[] bytes = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), type);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), (uint)payload.Length | 0x8000_0000u);
        payload.CopyTo(bytes, 8);
        return bytes;
    }

    private static byte[] Concat(params byte[][] arrays)
    {
        int length = arrays.Sum(static array => array.Length);
        byte[] result = new byte[length];
        int offset = 0;
        foreach (byte[] array in arrays)
        {
            array.CopyTo(result, offset);
            offset += array.Length;
        }
        return result;
    }

    private static void WriteSingle(Span<byte> destination, float value)
        => BinaryPrimitives.WriteInt32LittleEndian(destination, BitConverter.SingleToInt32Bits(value));

    private static void WriteFixedAscii(Span<byte> destination, string value)
    {
        destination.Clear();
        Encoding.ASCII.GetBytes(value.AsSpan(), destination);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EnbNext.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate EnbNext.slnx from the test output directory.");
    }
}
