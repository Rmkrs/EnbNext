using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR4GW3dVertexLightingTests
{
    [Fact]
    public void MissingAuthoredMaterialAndShaderUseRetailDescriptorDefaults()
    {
        byte[] bytes = BuildSinglePassMesh(material: null, meshAttributes: 0u);
        EnbW3dRenderResource resource = Compile(bytes);
        EnbW3dRenderMesh mesh = Assert.Single(resource.Meshes);
        EnbW3dRenderMaterialPass pass = Assert.Single(mesh.MaterialPasses);

        Assert.Equal(EnbW3dRenderBindingCardinality.None, pass.VertexMaterialCardinality);
        Assert.Equal(EnbW3dRenderBindingCardinality.None, pass.ShaderCardinality);
        Assert.Empty(pass.VertexMaterials);
        Assert.Empty(pass.Shaders);
        Assert.Empty(mesh.EffectiveColor1);

        EnbW3dRenderTriangleBinding triangle = Assert.Single(pass.TriangleBindings);
        Assert.True(triangle.UsesImplicitVertexMaterial);
        Assert.True(triangle.UsesImplicitShader);
        Assert.Null(triangle.VertexMaterial);
        Assert.Null(triangle.Shader);

        EnbW3dCompiledMaterialSemantics semantics = RequireSemantics(triangle);
        Assert.Equal(
            EnbW3dRetailShaderOptimizer.OptimizeGeneralFog(EnbW3dMaterialCompiler.RetailW3dShaderBaseline),
            semantics.FixedFunction.PackedRetailState);
        Assert.Equal(EnbRenderDepthCompare.LessOrEqual, semantics.FixedFunction.DepthCompare);
        Assert.True(semantics.FixedFunction.DepthWriteEnabled);
        Assert.Equal(EnbRenderCullMode.Clockwise, semantics.FixedFunction.CullMode);
        Assert.Equal(EnbRenderBlendFactor.One, semantics.FixedFunction.SourceBlend);
        Assert.Equal(EnbRenderBlendFactor.Zero, semantics.FixedFunction.DestinationBlend);
        Assert.False(semantics.FixedFunction.BlendEnabled);
        Assert.Equal(Vector3.One, semantics.Uniforms.Ambient);
        Assert.Equal(Vector3.One, semantics.Uniforms.Diffuse);
        Assert.Equal(Vector3.Zero, semantics.Uniforms.Specular);
        Assert.Equal(Vector3.Zero, semantics.Uniforms.Emissive);
        Assert.Equal(0f, semantics.Uniforms.Power);
        Assert.Equal(1f, semantics.Uniforms.DiffuseAlpha);
        Assert.Equal(EnbW3dVertexLightingSemantics.RetailDefault, semantics.VertexLighting);
        Assert.Equal(semantics.VertexLighting, semantics.PermutationKey.VertexLighting);
        Assert.NotNull(pass.SingleBindingSemantics);
    }

    [Fact]
    public void DiffuseColorThenIlluminationBuildsMeshGlobalColor1AndFoldsDiffuseMaterial()
    {
        byte[] bytes = BuildSinglePassMesh(
            material: new MaterialFixture(
                Ambient: (0, 0, 0),
                Diffuse: (128, 64, 255),
                Specular: (0, 0, 0),
                Emissive: (0, 0, 0),
                Opacity: 0.5f),
            meshAttributes: 0u,
            colorChunks:
            [
                (0x003Bu, new EnbRenderColor32(200, 100, 50, 128)),
                (0x003Cu, new EnbRenderColor32(128, 255, 64, 17)),
            ]);

        Assert.True(EnbW3dModel.TryDecode(bytes, out EnbW3dModel model));
        EnbW3dMaterialPass sourcePass = Assert.Single(Assert.Single(model.Meshes).MaterialPasses);
        Assert.Equal(
            [EnbW3dMaterialPassColorChunkKind.DiffuseColor, EnbW3dMaterialPassColorChunkKind.DiffuseIllumination],
            sourcePass.ColorChunkOrder);

        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(Resolve(bytes, model), out EnbW3dRenderResource resource, out string? diagnostic), diagnostic);
        EnbW3dRenderMesh mesh = Assert.Single(resource.Meshes);
        EnbW3dRenderMaterialPass pass = Assert.Single(mesh.MaterialPasses);
        Assert.Equal(3, mesh.EffectiveColor1.Count);
        Assert.All(mesh.EffectiveColor1, color => Assert.Equal(new EnbRenderColor32(50, 25, 12, 64), color));

        EnbW3dVertexLightingSemantics lighting = RequireSemantics(Assert.Single(pass.TriangleBindings)).VertexLighting;
        Assert.True(lighting.LightingEnabled);
        Assert.Equal(EnbW3dMaterialColorSource.Color1, lighting.AmbientSource);
        Assert.Equal(EnbW3dMaterialColorSource.Material, lighting.DiffuseSource);
        Assert.Equal(EnbW3dMaterialColorSource.Material, lighting.SpecularSource);
        Assert.Equal(EnbW3dMaterialColorSource.Material, lighting.EmissiveSource);
    }

    [Fact]
    public void IlluminationThenDiffuseColorRoutesLaterDiffuseToSecondaryDescriptor()
    {
        EnbRenderColor32 illumination = new(10, 20, 30, 7);
        EnbRenderColor32 laterDiffuse = new(200, 210, 220, 9);
        byte[] bytes = BuildSinglePassMesh(
            material: null,
            meshAttributes: 0u,
            colorChunks:
            [
                (0x003Cu, illumination),
                (0x003Bu, laterDiffuse),
            ]);

        Assert.True(EnbW3dModel.TryDecode(bytes, out EnbW3dModel model));
        EnbW3dMesh sourceMesh = Assert.Single(model.Meshes);
        EnbW3dMaterialPass sourcePass = Assert.Single(sourceMesh.MaterialPasses);
        Assert.Equal([EnbW3dMaterialPassColorChunkKind.DiffuseIllumination], sourcePass.ColorChunkOrder);
        Assert.Empty(sourcePass.DiffuseColors);

        EnbW3dSecondaryMaterialDescriptorChunk overflow = Assert.Single(sourceMesh.SecondaryMaterialDescriptorChunks);
        Assert.Equal(0, overflow.MaterialPassOrdinal);
        Assert.Equal(0x003Bu, overflow.ChunkType);
        Assert.Equal(ColorPayload(3, laterDiffuse), overflow.Payload.ToArray());

        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(Resolve(bytes, model), out EnbW3dRenderResource resource, out string? diagnostic), diagnostic);
        EnbW3dRenderMesh mesh = Assert.Single(resource.Meshes);
        EnbW3dRenderMaterialPass pass = Assert.Single(mesh.MaterialPasses);

        // 0x003C creates the primary descriptor COLO with alpha forced to 255.
        // Because that also occupies the pass's primary color-source slot, the
        // later 0x003B belongs to the retained secondary descriptor and cannot
        // replace normal-path output.
        Assert.All(mesh.EffectiveColor1, color => Assert.Equal(new EnbRenderColor32(10, 20, 30, 255), color));
        EnbW3dVertexLightingSemantics lighting = RequireSemantics(Assert.Single(pass.TriangleBindings)).VertexLighting;
        Assert.Equal(EnbW3dMaterialColorSource.Color1, lighting.AmbientSource);
        Assert.Equal(EnbW3dMaterialColorSource.Color1, lighting.DiffuseSource);
    }

    [Fact]
    public void SharedAuthoredMaterialKeepsFinalSourceStateAcrossPasses()
    {
        var material = new MaterialFixture(
            Ambient: (255, 255, 255),
            Diffuse: (255, 255, 255),
            Specular: (0, 0, 0),
            Emissive: (0, 0, 0),
            Opacity: 1f);
        byte[] bytes = BuildTwoPassMesh(
            material,
            pass0UsesMaterial: true,
            pass1UsesMaterial: true,
            pass0ColorChunks: [(0x003Bu, new EnbRenderColor32(90, 80, 70, 255))]);

        EnbW3dRenderMesh mesh = Assert.Single(Compile(bytes).Meshes);
        Assert.All(mesh.EffectiveColor1, color => Assert.Equal(new EnbRenderColor32(90, 80, 70, 255), color));
        Assert.Equal(2, mesh.MaterialPasses.Count);

        EnbW3dCompiledMaterialSemantics first = RequireSemantics(Assert.Single(mesh.MaterialPasses[0].TriangleBindings));
        EnbW3dCompiledMaterialSemantics second = RequireSemantics(Assert.Single(mesh.MaterialPasses[1].TriangleBindings));

        // Pass 0 folds COLO and sets ambient+diffuse to COLO. Pass 1 then
        // resets diffuse/emissive on the same retail material object but does
        // not reset ambient. Rendering happens after preprocessing is complete,
        // so both passes observe this final shared state.
        Assert.Equal(EnbW3dMaterialColorSource.Color1, first.VertexLighting.AmbientSource);
        Assert.Equal(EnbW3dMaterialColorSource.Material, first.VertexLighting.DiffuseSource);
        Assert.Equal(first.VertexLighting, second.VertexLighting);
    }

    [Fact]
    public void ImplicitDefaultMaterialsArePassLocalEvenWithSharedMeshColor1()
    {
        byte[] bytes = BuildTwoPassMesh(
            material: null,
            pass0UsesMaterial: false,
            pass1UsesMaterial: false,
            pass0ColorChunks: [(0x003Bu, new EnbRenderColor32(40, 50, 60, 255))]);

        EnbW3dRenderMesh mesh = Assert.Single(Compile(bytes).Meshes);
        EnbW3dCompiledMaterialSemantics first = RequireSemantics(Assert.Single(mesh.MaterialPasses[0].TriangleBindings));
        EnbW3dCompiledMaterialSemantics second = RequireSemantics(Assert.Single(mesh.MaterialPasses[1].TriangleBindings));

        Assert.Equal(EnbW3dMaterialColorSource.Color1, first.VertexLighting.AmbientSource);
        Assert.Equal(EnbW3dMaterialColorSource.Color1, first.VertexLighting.DiffuseSource);
        Assert.Equal(EnbW3dVertexLightingSemantics.RetailDefault, second.VertexLighting);
        Assert.NotEqual(first.PermutationKey, second.PermutationKey);
        var firstDefault = mesh.MaterialPasses[0].CurrentRetailUniformMaterial!.Value.Material;
        var secondDefault = mesh.MaterialPasses[1].CurrentRetailUniformMaterial!.Value.Material;
        Assert.NotSame(firstDefault, secondDefault);
        Assert.Empty(mesh.VertexMaterials); // Defaults are descriptor-owned, not loaded-table entries.
        Assert.Equal(first.VertexLighting, mesh.MaterialPasses[0].CurrentRetailUniformLighting);
        Assert.Equal(second.VertexLighting, mesh.MaterialPasses[1].CurrentRetailUniformLighting);
        Assert.True(EnbW3dRenderResourceCompiler.TryReplaceUniformDescriptor(
            mesh, [mesh.MaterialPasses[1]], out var copied, out var error), error);
        Assert.Same(secondDefault, copied[0].CurrentRetailUniformMaterial!.Value.Material);
        Assert.Equal(second.VertexLighting, copied[0].SingleBindingSemantics!.Value.VertexLighting);
    }

    [Fact]
    public void EmissiveOnlyColor1PathDisablesLightingWhenRetailOptimizationRemainsEligible()
    {
        byte[] bytes = BuildSinglePassMesh(
            material: new MaterialFixture(
                Ambient: (0, 0, 0),
                Diffuse: (0, 0, 0),
                Specular: (0, 0, 0),
                Emissive: (128, 64, 32),
                Opacity: 1f),
            meshAttributes: 0u,
            colorChunks: [(0x003Bu, new EnbRenderColor32(200, 100, 50, 255))]);

        EnbW3dRenderMesh mesh = Assert.Single(Compile(bytes).Meshes);
        EnbW3dCompiledMaterialSemantics semantics = RequireSemantics(Assert.Single(Assert.Single(mesh.MaterialPasses).TriangleBindings));
        Assert.False(semantics.VertexLighting.LightingEnabled);
        Assert.Equal(EnbW3dMaterialColorSource.Material, semantics.VertexLighting.AmbientSource);
        Assert.Equal(EnbW3dMaterialColorSource.Color1, semantics.VertexLighting.DiffuseSource);
        Assert.Equal(EnbW3dMaterialColorSource.Material, semantics.VertexLighting.EmissiveSource);
        Assert.All(mesh.EffectiveColor1, color => Assert.Equal(new EnbRenderColor32(100, 25, 6, 255), color));
    }

    [Theory]
    [InlineData(0x0000_2000, true)]
    [InlineData(0x0200_0000, false)]
    [InlineData(0x0400_0000, true)]
    [InlineData(0x0600_0000, true)]
    public void CurrentRetailMeshAttributeTranslationControlsInitialLighting(int rawAttributes, bool expectedLightingEnabled)
    {
        uint authoredAttributes = unchecked((uint)rawAttributes);
        byte[] bytes = BuildSinglePassMesh(material: null, meshAttributes: authoredAttributes);
        EnbW3dRenderMesh mesh = Assert.Single(Compile(bytes).Meshes);
        EnbW3dCompiledMaterialSemantics semantics = RequireSemantics(Assert.Single(Assert.Single(mesh.MaterialPasses).TriangleBindings));

        Assert.Equal(authoredAttributes, mesh.Attributes);
        Assert.Equal(expectedLightingEnabled, semantics.VertexLighting.LightingEnabled);
    }

    [InstalledRetailFact]
    public void DuplicateDiffuseColorIsPreservedAsSecondaryDescriptorProvenanceButPrimaryStillRenders()
    {
        EnbRenderColor32 primary = new(1, 2, 3, 4);
        EnbRenderColor32 secondary = new(5, 6, 7, 8);
        byte[] bytes = BuildSinglePassMesh(
            material: null,
            meshAttributes: 0u,
            colorChunks:
            [
                (0x003Bu, primary),
                (0x003Bu, secondary),
            ]);

        Assert.True(EnbW3dModel.TryDecode(bytes, out EnbW3dModel model));
        EnbW3dMesh sourceMesh = Assert.Single(model.Meshes);
        EnbW3dMaterialPass sourcePass = Assert.Single(sourceMesh.MaterialPasses);
        Assert.Equal([EnbW3dMaterialPassColorChunkKind.DiffuseColor], sourcePass.ColorChunkOrder);
        Assert.All(sourcePass.DiffuseColors, color => Assert.Equal(new EnbColor32(primary.R, primary.G, primary.B, primary.A), color));

        EnbW3dSecondaryMaterialDescriptorChunk overflow = Assert.Single(sourceMesh.SecondaryMaterialDescriptorChunks);
        Assert.Equal(0, overflow.MaterialPassOrdinal);
        Assert.Null(overflow.TextureStageOrdinal);
        Assert.Equal(0x003Bu, overflow.ChunkType);
        Assert.Equal(ColorPayload(3, secondary), overflow.Payload.ToArray());

        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(Resolve(bytes, model), out EnbW3dRenderResource resource, out string? diagnostic), diagnostic);
        EnbW3dRenderMesh renderMesh = Assert.Single(resource.Meshes);
        Assert.All(renderMesh.EffectiveColor1, color => Assert.Equal(primary, color));
        EnbW3dRenderSecondaryMaterialDescriptorChunk renderOverflow = Assert.Single(renderMesh.SecondaryMaterialDescriptorChunks);
        Assert.Equal(0x003Bu, renderOverflow.ChunkType);
        Assert.Equal(ColorPayload(3, secondary), renderOverflow.Payload.ToArray());
        var textures = EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!).Textures;
        var adjusted = EnbCurrentRetailEffectMeshHsvRuntime.Apply(resource, textures, new(120, .1f, 0));
        var adjustedMesh = Assert.Single(adjusted.Meshes);
        Assert.Same(renderMesh.SecondaryMaterialDescriptorChunks, adjustedMesh.SecondaryMaterialDescriptorChunks);
        var controlBytes = BuildSinglePassMesh(null, 0u, [(0x003Bu, primary)]);
        var control = EnbCurrentRetailEffectMeshHsvRuntime.Apply(Compile(controlBytes), textures, new(120, .1f, 0));
        Assert.Equal(Assert.Single(control.Meshes).EffectiveColor1, adjustedMesh.EffectiveColor1);
        Assert.Equal(renderOverflow.Payload.ToArray(), Assert.Single(adjustedMesh.SecondaryMaterialDescriptorChunks).Payload.ToArray());
    }

    [Fact]
    public void DiffuseIlluminationConflictIsDescriptorGlobalAcrossMaterialPasses()
    {
        EnbRenderColor32 primary = new(10, 20, 30, 99);
        EnbRenderColor32 secondary = new(40, 50, 60, 77);
        byte[] bytes = BuildTwoPassMesh(
            material: null,
            pass0UsesMaterial: false,
            pass1UsesMaterial: false,
            pass0ColorChunks: [(0x003Cu, primary)],
            pass1ColorChunks: [(0x003Cu, secondary)]);

        Assert.True(EnbW3dModel.TryDecode(bytes, out EnbW3dModel model));
        EnbW3dMesh sourceMesh = Assert.Single(model.Meshes);
        Assert.Equal([EnbW3dMaterialPassColorChunkKind.DiffuseIllumination], sourceMesh.MaterialPasses[0].ColorChunkOrder);
        Assert.Empty(sourceMesh.MaterialPasses[1].ColorChunkOrder);
        EnbW3dSecondaryMaterialDescriptorChunk overflow = Assert.Single(sourceMesh.SecondaryMaterialDescriptorChunks);
        Assert.Equal(1, overflow.MaterialPassOrdinal);
        Assert.Equal(0x003Cu, overflow.ChunkType);

        EnbW3dRenderMesh renderMesh = Assert.Single(Compile(bytes).Meshes);
        Assert.All(renderMesh.EffectiveColor1, color => Assert.Equal(new EnbRenderColor32(10, 20, 30, 255), color));
    }

    [Fact]
    public void ConflictingBindingAndTextureFieldsRouteToSecondaryDescriptorProvenance()
    {
        MaterialFixture material = new(
            Ambient: (255, 255, 255),
            Diffuse: (255, 255, 255),
            Specular: (0, 0, 0),
            Emissive: (0, 0, 0),
            Opacity: 1f);
        byte[] stage = ContainerChunk(
            0x0048,
            LeafChunk(0x0049, UInt32Payload([0u])),
            LeafChunk(0x0049, UInt32Payload([1u])),
            LeafChunk(0x004A, Vector2Payload([Vector2.Zero, Vector2.UnitX, Vector2.UnitY])),
            LeafChunk(0x004A, Vector2Payload([Vector2.One, Vector2.One, Vector2.One])));
        byte[] pass = ContainerChunk(
            0x0038,
            LeafChunk(0x0039, UInt32Payload([0u])),
            LeafChunk(0x0039, UInt32Payload([1u])),
            LeafChunk(0x003A, UInt32Payload([0u])),
            LeafChunk(0x003A, UInt32Payload([1u])),
            stage);
        byte[] bytes = BuildMesh(0u, [material, material], [pass]);

        Assert.True(EnbW3dModel.TryDecode(bytes, out EnbW3dModel model));
        EnbW3dMesh mesh = Assert.Single(model.Meshes);
        Assert.Equal(4, mesh.SecondaryMaterialDescriptorChunks.Count);
        Assert.Collection(
            mesh.SecondaryMaterialDescriptorChunks,
            chunk =>
            {
                Assert.Equal(0x0039u, chunk.ChunkType);
                Assert.Null(chunk.TextureStageOrdinal);
            },
            chunk =>
            {
                Assert.Equal(0x003Au, chunk.ChunkType);
                Assert.Null(chunk.TextureStageOrdinal);
            },
            chunk =>
            {
                Assert.Equal(0x0049u, chunk.ChunkType);
                Assert.Equal(0, chunk.TextureStageOrdinal);
            },
            chunk =>
            {
                Assert.Equal(0x004Au, chunk.ChunkType);
                Assert.Equal(0, chunk.TextureStageOrdinal);
            });
    }

    [Fact]
    public void R4GStateRemainsBackendNeutral()
    {
        string root = FindRepositoryRoot();
        string resourceSource = File.ReadAllText(Path.Combine(root, "src", "EnbNext.Rendering", "EnbW3dRenderResource.cs"));
        string materialSource = File.ReadAllText(Path.Combine(root, "src", "EnbNext.Rendering", "EnbW3dMaterialSemantics.cs"));

        Assert.Contains("EffectiveColor1", resourceSource, StringComparison.Ordinal);
        Assert.Contains("EnbW3dVertexLightingSemantics", materialSource, StringComparison.Ordinal);
        Assert.Contains("RetailW3dShaderBaseline", materialSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Veldrid", resourceSource, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Silk.NET", resourceSource, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OpenGL", resourceSource, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Vulkan", resourceSource, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RetainedCoordinatesFollowMaterialSharingAcrossPasses(bool shared)
    {
        var material = new MaterialFixture((255,255,255), (255,255,255), (0,0,0), (0,0,0), 1f);
        byte[] Pass(uint id, Vector2 uv) => ContainerChunk(0x0038,
            LeafChunk(0x0039, UInt32Payload([id])),
            ContainerChunk(0x0048, LeafChunk(0x004A, Vector2Payload([uv,uv,uv]))));
        var mesh = Assert.Single(Compile(BuildMesh(0, [material,material],
            [Pass(0,Vector2.Zero),Pass(shared ? 0u : 1u,Vector2.One)])).Meshes);
        var first = Assert.Single(mesh.MaterialPasses[0].TriangleBindings).VertexMaterial!.Value;
        var second = Assert.Single(mesh.MaterialPasses[1].TriangleBindings).VertexMaterial!.Value;
        Assert.Equal(new EnbW3dMaterialRuntimeCoordinates(shared ? 1 : 0, 0), first.CurrentRetailRuntimeCoordinates);
        Assert.Equal(new EnbW3dMaterialRuntimeCoordinates(1, 0), second.CurrentRetailRuntimeCoordinates);
    }

    private static EnbW3dCompiledMaterialSemantics RequireSemantics(EnbW3dRenderTriangleBinding binding)
        => binding.Semantics ?? throw new Xunit.Sdk.XunitException("Expected effective current-retail material semantics.");

    private static EnbW3dRenderResource Compile(byte[] bytes)
    {
        Assert.True(EnbW3dModel.TryDecode(bytes, out EnbW3dModel model));
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(Resolve(bytes, model), out EnbW3dRenderResource resource, out string? diagnostic), diagnostic);
        return resource;
    }

    private static EnbResolvedW3dResource Resolve(byte[] bytes, EnbW3dModel model)
        => new(
            "R4G.W3D",
            "R4G_ROOT",
            new EnbMixResourceLocation("R4G.W3D", Path.Combine(Path.GetTempPath(), "r4g_mixfile_art.mix"), 1024, bytes.Length),
            model,
            []);

    private static byte[] BuildSinglePassMesh(
        MaterialFixture? material,
        uint meshAttributes,
        IReadOnlyList<(uint Type, EnbRenderColor32 Color)>? colorChunks = null)
    {
        byte[] pass = BuildPass(material is null ? null : 0u, colorChunks);
        return BuildMesh(
            meshAttributes,
            material is null ? [] : [material],
            [pass]);
    }

    private static byte[] BuildTwoPassMesh(
        MaterialFixture? material,
        bool pass0UsesMaterial,
        bool pass1UsesMaterial,
        IReadOnlyList<(uint Type, EnbRenderColor32 Color)>? pass0ColorChunks = null,
        IReadOnlyList<(uint Type, EnbRenderColor32 Color)>? pass1ColorChunks = null)
    {
        MaterialFixture[] materials = material is null ? [] : [material];
        byte[] pass0 = BuildPass(pass0UsesMaterial ? 0u : null, pass0ColorChunks);
        byte[] pass1 = BuildPass(pass1UsesMaterial ? 0u : null, pass1ColorChunks);
        return BuildMesh(0u, materials, [pass0, pass1]);
    }

    private static byte[] BuildMesh(
        uint meshAttributes,
        IReadOnlyList<MaterialFixture> materials,
        IReadOnlyList<byte[]> passes)
    {
        Vector3[] vertices = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY];
        Vector3[] normals = Enumerable.Repeat(Vector3.UnitZ, vertices.Length).ToArray();
        var meshChildren = new List<byte[]>
        {
            LeafChunk(0x001F, BuildMeshHeader("R4G_MESH", "R4G_ROOT", 1, 3, 1f, meshAttributes)),
            LeafChunk(0x0002, Vector3Payload(vertices)),
            LeafChunk(0x0003, Vector3Payload(normals)),
            LeafChunk(0x0020, TrianglePayload(0, 1, 2)),
            LeafChunk(0x0028, UInt32Payload([(uint)passes.Count, (uint)materials.Count, 0u, 0u])),
        };

        if (materials.Count != 0)
        {
            meshChildren.Add(ContainerChunk(
                0x002A,
                materials.Select((material, index) => VertexMaterial($"MAT{index}", material)).ToArray()));
        }

        meshChildren.AddRange(passes);
        return ContainerChunk(0x0000, meshChildren.ToArray());
    }

    private static byte[] BuildPass(
        uint? materialId,
        IReadOnlyList<(uint Type, EnbRenderColor32 Color)>? colorChunks)
    {
        var children = new List<byte[]>();
        if (materialId is uint id)
            children.Add(LeafChunk(0x0039, UInt32Payload([id])));
        foreach ((uint type, EnbRenderColor32 color) in colorChunks ?? [])
            children.Add(LeafChunk(type, ColorPayload(3, color)));
        return ContainerChunk(0x0038, children.ToArray());
    }

    private static byte[] VertexMaterial(string name, MaterialFixture fixture)
    {
        byte[] info = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(0, 4), 0u);
        WriteRgb(info.AsSpan(4, 4), fixture.Ambient);
        WriteRgb(info.AsSpan(8, 4), fixture.Diffuse);
        WriteRgb(info.AsSpan(12, 4), fixture.Specular);
        WriteRgb(info.AsSpan(16, 4), fixture.Emissive);
        WriteSingle(info.AsSpan(20, 4), 0f);
        WriteSingle(info.AsSpan(24, 4), fixture.Opacity);
        WriteSingle(info.AsSpan(28, 4), 0f);
        return ContainerChunk(
            0x002B,
            LeafChunk(0x002C, Encoding.ASCII.GetBytes(name + "\0")),
            LeafChunk(0x002D, info));
    }

    private static byte[] BuildMeshHeader(
        string name,
        string container,
        uint triangleCount,
        uint vertexCount,
        float radius,
        uint attributes)
    {
        byte[] header = new byte[116];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), 0x0004_0002u);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), attributes);
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

    private static byte[] ColorPayload(int count, EnbRenderColor32 color)
    {
        byte[] bytes = new byte[count * 4];
        for (int index = 0; index < count; index++)
        {
            int offset = index * 4;
            bytes[offset] = color.R;
            bytes[offset + 1] = color.G;
            bytes[offset + 2] = color.B;
            bytes[offset + 3] = color.A;
        }
        return bytes;
    }

    private static byte[] Vector2Payload(IEnumerable<Vector2> values)
    {
        Vector2[] snapshot = values.ToArray();
        byte[] bytes = new byte[snapshot.Length * 8];
        for (int index = 0; index < snapshot.Length; index++)
        {
            WriteSingle(bytes.AsSpan(index * 8, 4), snapshot[index].X);
            WriteSingle(bytes.AsSpan((index * 8) + 4, 4), snapshot[index].Y);
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

    private static void WriteRgb(Span<byte> destination, (byte R, byte G, byte B) rgb)
    {
        destination[0] = rgb.R;
        destination[1] = rgb.G;
        destination[2] = rgb.B;
        destination[3] = 0;
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

    private sealed record MaterialFixture(
        (byte R, byte G, byte B) Ambient,
        (byte R, byte G, byte B) Diffuse,
        (byte R, byte G, byte B) Specular,
        (byte R, byte G, byte B) Emissive,
        float Opacity);
}
