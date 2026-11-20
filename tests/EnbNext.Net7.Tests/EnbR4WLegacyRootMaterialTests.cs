using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR4WLegacyRootMaterialTests
{
    [InstalledRetailTheory]
    [InlineData("TRUNK01.w3d",3)] [InlineData("TRUNK02.w3d",6)]
    public void InstalledLegacyTreesDecodeWithoutContainerHints(string name,int triangleCount)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        Assert.True(assets.FileResources.TryRead(name,out var bytes,out _,out _));
        Assert.True(EnbW3dModel.TryDecode(bytes,out var model));
        var mesh=Assert.Single(model.Meshes);
        Assert.Equal(triangleCount,mesh.Triangles.Count);Assert.Single(mesh.LegacyRootMaterials);
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(Resolve(bytes,model),out var resource,out var error),error);
        Assert.Single(Assert.Single(resource.Meshes).MaterialPasses);
    }

    [Theory]
    [InlineData(0u)] [InlineData(0x19u)] [InlineData(0x1eu)]
    public void LegacyMaterialAndTextureContainersDoNotRequireHint(uint textureKind)
    {
        byte[] original=BuildLegacyMesh(1,LegacyMaterials(LegacyMaterial("HINT",LegacyInfo(),
            textureKind==0?[]:[LegacyTexture(textureKind,"HINT.TGA")])));
        byte[] bytes=(byte[])original.Clone();
        void Clear(int start,int end)
        {
            for(int at=start;at<end;)
            {
                uint kind=BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
                uint size=BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at+4))&0x7fffffffu;
                if(kind is 0 or 0x15 or 0x16 or 0x19 or 0x1e)
                {BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at+4),size);Clear(at+8,at+8+(int)size);}
                at+=8+(int)size;
            }
        }
        Clear(0,bytes.Length);
        Assert.True(EnbW3dModel.TryDecode(original,out var control));
        Assert.True(EnbW3dModel.TryDecode(bytes,out var actual));
        var expected=Assert.Single(control.Meshes);var mesh=Assert.Single(actual.Meshes);
        Assert.Equivalent(expected.LegacyRootMaterials,mesh.LegacyRootMaterials,strict:true);
        Assert.Equivalent(expected.LegacyRootTextures,mesh.LegacyRootTextures,strict:true);
        Assert.Equal(expected.LegacyRootPackedShaders,mesh.LegacyRootPackedShaders);
        Assert.Equal(expected.CurrentRetailGeometrySortRequested,mesh.CurrentRetailGeometrySortRequested);
    }

    [Theory]
    [InlineData(0u, "LEGACY.TGA")]
    [InlineData(1u, "MODERN.TGA")]
    public void ModernStageSelectorIndexesTheSharedLoadedTextureTable(uint selector, string expected)
    {
        var bytes = BuildMixedMesh(1, 0, 0, 1,
            LegacyMaterials(LegacyMaterial("M", LegacyInfo(), LegacyTexture(0x19, "LEGACY.TGA"))),
            ContainerChunk(0x30, ContainerChunk(0x31, LeafChunk(0x32, Encoding.ASCII.GetBytes("MODERN.TGA\0")))),
            ContainerChunk(0x38, ContainerChunk(0x48), ContainerChunk(0x48, LeafChunk(0x49, UInt32Payload([selector])))));
        Assert.True(EnbW3dModel.TryDecode(bytes, out var model));
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(Resolve(bytes, model), out var resource, out var error), error);
        var pass = Assert.Single(Assert.Single(resource.Meshes).MaterialPasses);
        Assert.Equal(expected, Assert.Single(pass.TextureStages[1].Textures).LogicalName);
        Assert.Equal(expected, pass.TriangleBindings[0].Textures[1]!.LogicalName);
    }

    [Theory]
    [InlineData(0u, "LEGACY", true)]
    [InlineData(1u, "MODERN", false)]
    public void ModernMaterialSelectorIndexesTheSharedLoadedMaterialTable(uint selector, string expected, bool legacy)
    {
        var bytes = BuildMixedMesh(1, 1, 0, 0,
            LegacyMaterials(LegacyMaterial("LEGACY", LegacyInfo())),
            ContainerChunk(0x2A, ModernVertexMaterial("MODERN")),
            ContainerChunk(0x38),
            ContainerChunk(0x38, LeafChunk(0x39, UInt32Payload([selector]))));
        Assert.True(EnbW3dModel.TryDecode(bytes, out var model));
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(Resolve(bytes, model), out var resource, out var error), error);
        var mesh = Assert.Single(resource.Meshes);
        Assert.Equal(new[] { "LEGACY", "MODERN" }, mesh.CurrentRetailLoadedMaterials.Select(m => m.Name));
        var material = Assert.Single(mesh.MaterialPasses[1].VertexMaterials).Material;
        Assert.Equal(expected, material.Name);
        Assert.Equal(legacy, material.IsLegacyRootMaterial);
        Assert.Same(mesh.CurrentRetailLoadedMaterials[(int)selector], material);
    }

    [Fact]
    public void LegacyCrcSearchAfterModernMaterialsUsesNativeMaterialCountComparison()
    {
        var bytes = BuildMixedMesh(2, 1, 0, 0,
            ContainerChunk(0x2A, ModernVertexMaterial("MODERN")),
            LegacyMaterials(LegacyMaterial("LEGACY0", LegacyInfo()),
                LegacyMaterial("LEGACY1", LegacyInfo(diffuse: (50, 60, 70)))),
            LeafChunk(0x21, UInt16Payload([1])));
        Assert.True(EnbW3dModel.TryDecode(bytes, out var model));
        var decoded = Assert.Single(model.Meshes);
        Assert.Empty(decoded.LegacyRootMaterials);
        Assert.All(decoded.LegacyRootMaterialMappings, mapping => Assert.Equal(0, mapping.MaterialIndex));
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(Resolve(bytes, model), out var resource, out var error), error);
        var mesh = Assert.Single(resource.Meshes);
        Assert.Equal("MODERN", Assert.Single(mesh.CurrentRetailLoadedMaterials).Name);
        Assert.Equal("MODERN", Assert.Single(Assert.Single(mesh.MaterialPasses).VertexMaterials).Material.Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoadedTextureTableKeepsModernDuplicatesAndDeduplicatesLegacyAgainstPriorEntries(bool modernFirst)
    {
        var modern = ContainerChunk(0x30,
            ContainerChunk(0x31, LeafChunk(0x32, Encoding.ASCII.GetBytes("SAME.TGA\0"))),
            ContainerChunk(0x31, LeafChunk(0x32, Encoding.ASCII.GetBytes("SAME.TGA\0"))));
        var legacy = LegacyMaterials(LegacyMaterial("M", LegacyInfo(), LegacyTexture(0x19, "same.tga")));
        var bytes = BuildMixedMesh(1, 0, 0, 2, modernFirst ? [modern, legacy] : [legacy, modern]);
        Assert.True(EnbW3dModel.TryDecode(bytes, out var model));
        var mesh = Assert.Single(model.Meshes);
        Assert.Equal(modernFirst ? new[] { "SAME.TGA", "SAME.TGA" } : new[] { "same.tga", "SAME.TGA", "SAME.TGA" },
            mesh.CurrentRetailLoadedTextureNames);
        Assert.Equal(3, mesh.TextureAcquisitions.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TwoSidedMeshOverridesAuthoredShaderCulling(bool twoSided)
    {
        byte[] bytes = BuildLegacyMesh(1, LegacyMaterials(LegacyMaterial("sky", LegacyInfo())));
        // Outer chunk header, mesh-header chunk header, then version and attributes.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20, 4), twoSided ? 0x2000u : 0u);
        Assert.True(EnbW3dModel.TryDecode(bytes, out var model));
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(Resolve(bytes, model), out var resource,
            out var diagnostic), diagnostic);
        var pass = Assert.Single(Assert.Single(resource.Meshes).MaterialPasses);
        Assert.Equal(twoSided ? EnbRenderCullMode.None : EnbRenderCullMode.Clockwise,
            pass.SingleBindingSemantics!.Value.FixedFunction.CullMode);
        Assert.All(pass.TriangleBindings, binding => Assert.Equal(
            twoSided ? EnbRenderCullMode.None : EnbRenderCullMode.Clockwise,
            binding.Semantics!.Value.FixedFunction.CullMode));
    }

    [Fact]
    public void Legacy0015ConvertsExactFloatMaterialAndNoTextureMovesDiffuseToAmbient()
    {
        byte[] raw = LegacyInfo(
            attributes: 0,
            diffuse: (128, 64, 32),
            specular: (100, 200, 50),
            emissive: (7, 8, 9),
            ambient: (10, 20, 30),
            diffuseMultiplier: (64, 128, 255),
            specularMultiplier: (255, 128, 64),
            power: 7.5f,
            alpha: 0.625f);

        byte[] bytes = BuildLegacyMesh(
            legacyMaterialCount: 1,
            LegacyMaterials(LegacyMaterial("LEGACY_A", raw)));

        Assert.True(EnbW3dModel.TryDecode(bytes, out EnbW3dModel model));
        EnbW3dMesh mesh = Assert.Single(model.Meshes);
        EnbW3dLegacyRootMaterial material = Assert.Single(mesh.LegacyRootMaterials);

        Vector3 convertedDiffuse = MultiplyRgb((128, 64, 32), (64, 128, 255));
        Vector3 convertedSpecular = MultiplyRgb((100, 200, 50), (255, 128, 64));
        AssertVector(Vector3.Zero, material.Diffuse);
        AssertVector(convertedDiffuse, material.Ambient);
        AssertVector(convertedSpecular, material.Specular);
        AssertVector(Rgb((7, 8, 9)), material.Emissive);
        Assert.Equal(7.5f, material.Power);
        Assert.Equal(0.625f, material.DiffuseAlpha);
        // Current retail normalizes RGB by multiplying with the stored float
        // constant 0x3B808081, not by performing a fresh division by 255.
        // The one-ULP differences survive into the CRC-covered float backing.
        Assert.Equal(0x3622_7B33u, material.CurrentRetailCrc32);
        Assert.Equal(0x0008_441Bu, Assert.Single(mesh.LegacyRootPackedShaders));

        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(
            Resolve(bytes, model),
            out EnbW3dRenderResource resource,
            out string? diagnostic), diagnostic);

        EnbW3dRenderMesh renderMesh = Assert.Single(resource.Meshes);
        EnbW3dRenderMaterialPass pass = Assert.Single(renderMesh.MaterialPasses);
        Assert.Equal(EnbW3dRenderBindingCardinality.Uniform, pass.VertexMaterialCardinality);
        Assert.Equal(EnbW3dRenderBindingCardinality.Uniform, pass.ShaderCardinality);
        Assert.Empty(pass.TextureStages);
        EnbW3dRenderVertexMaterialBinding binding = Assert.Single(pass.VertexMaterials);
        Assert.Equal(EnbW3dRenderBindingSourceKind.LegacyRoot, binding.SourceKind);
        Assert.True(binding.Material.IsLegacyRootMaterial);
        Assert.Equal(EnbW3dRenderBindingSourceKind.PackedRetailState, Assert.Single(pass.Shaders).SourceKind);

        Assert.True(pass.SingleBindingSemantics.HasValue);
        EnbW3dCompiledMaterialSemantics semantics = pass.SingleBindingSemantics.Value;
        AssertVector(convertedDiffuse, semantics.Uniforms.Ambient);
        AssertVector(Vector3.Zero, semantics.Uniforms.Diffuse);
        AssertVector(convertedSpecular, semantics.Uniforms.Specular);
        AssertVector(Rgb((7, 8, 9)), semantics.Uniforms.Emissive);
        Assert.Equal(7.5f, semantics.Uniforms.Power);
        Assert.Equal(0.625f, semantics.Uniforms.DiffuseAlpha);
    }

    [Fact]
    public void LegacyTextureBranchesAndDedupUseCurrentRetailRules()
    {
        byte[] zeroDiffuse = LegacyInfo(diffuse: (0, 0, 0), diffuseMultiplier: (255, 255, 255));
        byte[] nonZeroDiffuse = LegacyInfo(diffuse: (50, 60, 70), diffuseMultiplier: (255, 255, 255));

        byte[] bytes = BuildLegacyMesh(
            legacyMaterialCount: 2,
            LegacyMaterials(
                LegacyMaterial(
                    "ZERO",
                    zeroDiffuse,
                    LegacyTexture(0x0019, "A.TGA"),
                    LegacyTexture(0x001E, "b.tga")),
                LegacyMaterial(
                    "NONZERO",
                    nonZeroDiffuse,
                    LegacyTexture(0x0019, "B.TGA"),
                    LegacyTexture(0x001E, "IGNORED.TGA"))));

        Assert.True(EnbW3dModel.TryDecode(bytes, out EnbW3dModel model));
        EnbW3dMesh mesh = Assert.Single(model.Meshes);

        EnbW3dLegacyRootTexture texture = Assert.Single(mesh.LegacyRootTextures);
        Assert.Equal("b.tga", texture.Name);
        Assert.True(texture.SelectedByAlternate001E);
        Assert.Equal(new[] { "A.TGA", "b.tga", "B.TGA" }, mesh.TextureAcquisitions.Select(value => value.LogicalName));
        Assert.All(mesh.TextureAcquisitions, value => Assert.Null(value.Info));
        Assert.Equal(new[] { "b.tga" }, mesh.CurrentRetailLoadedTextureNames);
        Assert.Equal([0x0009_403Bu, 0x0009_441Bu], mesh.LegacyRootPackedShaders);
        Assert.Equal(2, mesh.LegacyRootMaterialMappings.Count);
        Assert.All(mesh.LegacyRootMaterialMappings, mapping => Assert.Equal(0, mapping.TextureIndex));
        Assert.DoesNotContain(mesh.LegacyRootTextures, value =>
            string.Equals(value.Name, "IGNORED.TGA", StringComparison.OrdinalIgnoreCase));
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(Resolve(bytes, model), out var resource, out var error), error);
        var registry = new EnbCurrentRetailTextureRegistry();
        using var owner = new EnbCurrentRetailCompiledTextureOwner(Assert.Single(resource.Meshes), registry,
            definition => (definition.LogicalName.ToLowerInvariant(), EnbW3dTextureSemanticCompiler.Compile(definition.Info)));
        var retained = Assert.Single(owner.LoadedTextures).State;
        Assert.All(owner.Mesh.MaterialPasses.SelectMany(pass => pass.TriangleBindings).SelectMany(binding => binding.Textures),
            binding => Assert.Same(retained, binding!.CurrentRetailTextureState));
        Assert.Equal(1, registry.CollectUnused()); // Superseded A, not selected B.
        owner.Dispose();
        Assert.Equal(1, registry.CollectUnused());
    }

    [Fact]
    public void LegacyMaterialDedupIsCrcOnlyAndFirstMaterialOwnsTheName()
    {
        byte[] same = LegacyInfo(
            diffuse: (20, 40, 60),
            diffuseMultiplier: (255, 255, 255),
            alpha: 0.75f);

        byte[] bytes = BuildLegacyMesh(
            legacyMaterialCount: 2,
            LegacyMaterials(
                LegacyMaterial("FIRST", same, LegacyTexture(0x0019, "SAME.TGA")),
                LegacyMaterial("SECOND", same, LegacyTexture(0x0019, "same.tga"))));

        Assert.True(EnbW3dModel.TryDecode(bytes, out EnbW3dModel model));
        EnbW3dMesh mesh = Assert.Single(model.Meshes);
        EnbW3dLegacyRootMaterial material = Assert.Single(mesh.LegacyRootMaterials);
        Assert.Equal("FIRST", material.Name);
        Assert.Single(mesh.LegacyRootTextures);
        Assert.Equal(2, mesh.LegacyRootMaterialMappings.Count);
        Assert.All(mesh.LegacyRootMaterialMappings, mapping => Assert.Equal(0, mapping.MaterialIndex));
        Assert.All(mesh.LegacyRootMaterialMappings, mapping => Assert.Equal(0, mapping.TextureIndex));
    }

    [Fact]
    public void Selector0021ExpandsDedupTablesAndSharedVerticesUseLastTriangleWrite()
    {
        byte[] bytes = BuildLegacyMesh(
            legacyMaterialCount: 2,
            triangles: [(0u, 1u, 2u), (1u, 2u, 3u)],
            children:
            [
                LegacyMaterials(
                    LegacyMaterial(
                        "MAT0",
                        LegacyInfo(attributes: 0, diffuse: (30, 40, 50), diffuseMultiplier: (255, 255, 255)),
                        LegacyTexture(0x0019, "A.TGA")),
                    LegacyMaterial(
                        "MAT1",
                        LegacyInfo(attributes: 1, diffuse: (90, 100, 110), diffuseMultiplier: (255, 255, 255)),
                        LegacyTexture(0x0019, "B.TGA"))),
                LeafChunk(0x0021, UInt16Payload([0, 1])),
            ]);

        Assert.True(EnbW3dModel.TryDecode(bytes, out EnbW3dModel model));
        EnbW3dMesh mesh = Assert.Single(model.Meshes);
        EnbW3dPrimaryPass0MaterialDescriptor descriptor = Assert.IsType<EnbW3dPrimaryPass0MaterialDescriptor>(mesh.CurrentRetailPrimaryPass0MaterialDescriptor);

        Assert.Equal(4, descriptor.PerVertexMaterials.Count);
        Assert.Equal((uint)0, descriptor.PerVertexMaterials[0]!.Value.SourceIndex);
        Assert.Equal((uint)1, descriptor.PerVertexMaterials[1]!.Value.SourceIndex);
        Assert.Equal((uint)1, descriptor.PerVertexMaterials[2]!.Value.SourceIndex);
        Assert.Equal((uint)1, descriptor.PerVertexMaterials[3]!.Value.SourceIndex);
        Assert.All(descriptor.PerVertexMaterials, reference =>
            Assert.Equal(EnbW3dPrimaryMaterialSourceKind.LegacyRootMaterial, reference!.Value.SourceKind));

        Assert.Equal(2, descriptor.PerTriangleShaders.Count);
        Assert.Equal(0x0009_441Bu, descriptor.PerTriangleShaders[0].Value);
        Assert.Equal(0x0009_84B3u, descriptor.PerTriangleShaders[1].Value);
        Assert.Equal(2, descriptor.PerTriangleTexturesStage0.Count);
        Assert.Equal((uint)0, descriptor.PerTriangleTexturesStage0[0]!.Value.SourceIndex);
        Assert.Equal((uint)1, descriptor.PerTriangleTexturesStage0[1]!.Value.SourceIndex);

        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(
            Resolve(bytes, model, "A.TGA", "B.TGA"),
            out EnbW3dRenderResource resource,
            out string? diagnostic), diagnostic);

        EnbW3dRenderMaterialPass pass = Assert.Single(Assert.Single(resource.Meshes).MaterialPasses);
        Assert.Equal(EnbW3dRenderBindingCardinality.PerVertex, pass.VertexMaterialCardinality);
        Assert.Equal(EnbW3dRenderBindingCardinality.PerTriangle, pass.ShaderCardinality);
        Assert.Equal(EnbW3dRenderBindingCardinality.PerTriangle, Assert.Single(pass.TextureStages).BindingCardinality);
        Assert.Equal((uint)0, pass.TriangleBindings[0].VertexMaterial!.Value.SourceId);
        Assert.Equal((uint)1, pass.TriangleBindings[1].VertexMaterial!.Value.SourceId);
        Assert.Equal(EnbW3dRenderBindingSourceKind.LegacyRoot, pass.TriangleBindings[0].VertexMaterial!.Value.SourceKind);
        Assert.Equal(EnbW3dRenderBindingSourceKind.LegacyRoot, pass.TriangleBindings[1].VertexMaterial!.Value.SourceKind);
        Assert.Equal(EnbW3dRenderBindingSourceKind.PackedRetailState, pass.TriangleBindings[0].Shader!.Value.SourceKind);
        Assert.Equal("A.TGA", pass.TriangleBindings[0].Textures[0]!.LogicalName);
        Assert.Equal("B.TGA", pass.TriangleBindings[1].Textures[0]!.LogicalName);
    }

    [Fact]
    public void Selector0021IsIgnoredWhenHeaderDeclaresExactlyOneLegacyMaterial()
    {
        byte[] bytes = BuildLegacyMesh(
            legacyMaterialCount: 1,
            LegacyMaterials(LegacyMaterial("ONLY", LegacyInfo())),
            LeafChunk(0x0021, [0xFF]));

        Assert.True(EnbW3dModel.TryDecode(bytes, out EnbW3dModel model));
        EnbW3dPrimaryPass0MaterialDescriptor descriptor = Assert.IsType<EnbW3dPrimaryPass0MaterialDescriptor>(
            Assert.Single(model.Meshes).CurrentRetailPrimaryPass0MaterialDescriptor);
        Assert.Empty(descriptor.PerVertexMaterials);
        Assert.Empty(descriptor.PerTriangleShaders);
        Assert.Empty(descriptor.PerTriangleTexturesStage0);
        Assert.Equal(EnbW3dPrimaryMaterialSourceKind.LegacyRootMaterial, descriptor.UniformMaterial!.Value.SourceKind);
        Assert.Equal(0x0008_441Bu, descriptor.UniformShader.Value);
    }

    [Fact]
    public void RootBeforeModernTreatsLiteralRetailBaselineShaderAsOccupiedAndDivertsModernBindings()
    {
        byte[] bytes = BuildMixedMesh(
            legacyMaterialCount: 1,
            modernMaterialCount: 1,
            modernShaderCount: 1,
            modernTextureCount: 0,
            children:
            [
                LegacyMaterials(LegacyMaterial("ROOT", LegacyInfo())),
                LeafChunk(0x0029, ModernShaderRecord()),
                ContainerChunk(0x002A, ModernVertexMaterial("MODERN")),
                ContainerChunk(
                    0x0038,
                    LeafChunk(0x0039, UInt32Payload([0u])),
                    LeafChunk(0x003A, UInt32Payload([0u]))),
            ]);

        Assert.True(EnbW3dModel.TryDecode(bytes, out EnbW3dModel model));
        EnbW3dMesh mesh = Assert.Single(model.Meshes);
        EnbW3dMaterialPass pass = Assert.Single(mesh.MaterialPasses);
        Assert.Empty(pass.VertexMaterialIds);
        Assert.Empty(pass.ShaderIds);
        Assert.Equal([0x0039u, 0x003Au], mesh.SecondaryMaterialDescriptorChunks.Select(chunk => chunk.ChunkType));

        EnbW3dPrimaryPass0MaterialDescriptor descriptor = Assert.IsType<EnbW3dPrimaryPass0MaterialDescriptor>(mesh.CurrentRetailPrimaryPass0MaterialDescriptor);
        Assert.Equal(EnbW3dPrimaryMaterialSourceKind.LegacyRootMaterial, descriptor.UniformMaterial!.Value.SourceKind);
        Assert.Equal(EnbW3dPrimaryShaderSourceKind.PackedRetailState, descriptor.UniformShader.SourceKind);
        Assert.Equal(0x0008_441Bu, descriptor.UniformShader.Value);

        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(
            Resolve(bytes, model),
            out EnbW3dRenderResource resource,
            out string? diagnostic), diagnostic);
        EnbW3dRenderMaterialPass renderPass = Assert.Single(Assert.Single(resource.Meshes).MaterialPasses);
        Assert.Equal(EnbW3dRenderBindingSourceKind.LegacyRoot, Assert.Single(renderPass.VertexMaterials).SourceKind);
        Assert.Equal(EnbW3dRenderBindingSourceKind.PackedRetailState, Assert.Single(renderPass.Shaders).SourceKind);
    }

    [Fact]
    public void LegacySelectorAfterModernExpandedBindingsOverwritesTheExistingPrimaryArrays()
    {
        byte[] bytes = BuildMixedMesh(
            legacyMaterialCount: 2,
            modernMaterialCount: 1,
            modernShaderCount: 1,
            modernTextureCount: 0,
            triangles: [(0u, 1u, 2u), (1u, 2u, 3u)],
            children:
            [
                LeafChunk(0x0029, ModernShaderRecord()),
                ContainerChunk(0x002A, ModernVertexMaterial("MODERN")),
                ContainerChunk(
                    0x0038,
                    LeafChunk(0x0039, UInt32Payload([0u, 0u, 0u, 0u])),
                    LeafChunk(0x003A, UInt32Payload([0u, 0u]))),
                LegacyMaterials(
                    LegacyMaterial(
                        "ROOT0",
                        LegacyInfo(attributes: 0, diffuse: (20, 30, 40), diffuseMultiplier: (255, 255, 255)),
                        LegacyTexture(0x0019, "A.TGA")),
                    LegacyMaterial(
                        "ROOT1",
                        LegacyInfo(attributes: 1, diffuse: (80, 90, 100), diffuseMultiplier: (255, 255, 255)),
                        LegacyTexture(0x0019, "B.TGA"))),
                LeafChunk(0x0021, UInt16Payload([0, 1])),
            ]);

        Assert.True(EnbW3dModel.TryDecode(bytes, out EnbW3dModel model));
        EnbW3dPrimaryPass0MaterialDescriptor descriptor = Assert.IsType<EnbW3dPrimaryPass0MaterialDescriptor>(
            Assert.Single(model.Meshes).CurrentRetailPrimaryPass0MaterialDescriptor);

        Assert.All(descriptor.PerVertexMaterials.Where(static value => value is not null), reference =>
            Assert.Equal(EnbW3dPrimaryMaterialSourceKind.ModernVertexMaterial, reference!.Value.SourceKind));
        Assert.All(descriptor.PerTriangleShaders, reference =>
            Assert.Equal(EnbW3dPrimaryShaderSourceKind.PackedRetailState, reference.SourceKind));
        Assert.Equal((uint)0, descriptor.PerVertexMaterials[0]!.Value.SourceIndex);
        Assert.Equal((uint)0, descriptor.PerVertexMaterials[1]!.Value.SourceIndex);
        Assert.Equal((uint)0, descriptor.PerVertexMaterials[2]!.Value.SourceIndex);
        Assert.Equal((uint)0, descriptor.PerVertexMaterials[3]!.Value.SourceIndex);
    }

    [Fact]
    public void Alternate001EFinalBlendStateDoesNotRetroactivelyRequestGeometrySort()
    {
        byte[] bytes = BuildLegacyMesh(
            legacyMaterialCount: 1,
            LegacyMaterials(
                LegacyMaterial(
                    "ALT",
                    LegacyInfo(attributes: 0, diffuse: (0, 0, 0), diffuseMultiplier: (255, 255, 255)),
                    LegacyTexture(0x001E, "ALT.TGA"))));

        Assert.True(EnbW3dModel.TryDecode(bytes, out EnbW3dModel model));
        EnbW3dMesh mesh = Assert.Single(model.Meshes);
        Assert.Equal(0x0009_403Bu, Assert.Single(mesh.LegacyRootPackedShaders));
        Assert.Equal(0x20u, Assert.Single(mesh.LegacyRootPackedShaders) & 0xE0u);
        Assert.False(mesh.CurrentRetailGeometrySortRequestedByLegacyRoot);
        Assert.False(mesh.CurrentRetailGeometrySortRequested);

        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(
            Resolve(bytes, model, "ALT.TGA"),
            out EnbW3dRenderResource resource,
            out string? diagnostic), diagnostic);
        Assert.False(Assert.Single(resource.Meshes).GeometrySort.Requested);
    }

    [Fact]
    public void R4WBoundaryKeepsLegacyMaterialResolutionInAssetsAndBackendNeutralRendering()
    {
        string root = FindRepositoryRoot();
        string assets = File.ReadAllText(Path.Combine(root, "src", "EnbNext.Assets", "EnbW3dModel.cs"));
        string graph = File.ReadAllText(Path.Combine(root, "src", "EnbNext.Assets", "EnbRetailRslResourceGraph.cs"));
        string rendering = File.ReadAllText(Path.Combine(root, "src", "EnbNext.Rendering", "EnbW3dRenderResource.cs"));

        Assert.Contains("LegacyRootMaterials", assets, StringComparison.Ordinal);
        Assert.Contains("CurrentRetailPrimaryPass0MaterialDescriptor", assets, StringComparison.Ordinal);
        Assert.Contains("HasUniformShaderBinding", assets, StringComparison.Ordinal);
        Assert.Contains("mesh.LegacyRootTextures", graph, StringComparison.Ordinal);
        Assert.Contains("CurrentRetailUniformOverride", rendering, StringComparison.Ordinal);
        Assert.Contains("PackedRetailState", rendering, StringComparison.Ordinal);
        Assert.DoesNotContain("Veldrid", rendering, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Silk.NET", rendering, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OpenGL", rendering, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Vulkan", rendering, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MixedLegacyTextureShaderArraysRetainModernMaterialForSwatchCopy()
    {
        byte[] bytes = BuildMixedMesh(
            legacyMaterialCount: 2, modernMaterialCount: 1, modernShaderCount: 1, modernTextureCount: 0,
            triangles: [(0u, 1u, 2u), (1u, 2u, 3u)],
            children:
            [
                LeafChunk(0x0029, ModernShaderRecord()),
                ContainerChunk(0x002A, ModernVertexMaterial("RETAINED")),
                ContainerChunk(0x0038,
                    LeafChunk(0x0039, UInt32Payload([0u])),
                    LeafChunk(0x003A, UInt32Payload([0u]))),
                LegacyMaterials(
                    LegacyMaterial("EXPANDED0", LegacyInfo(attributes: 0, diffuse: (20, 30, 40), diffuseMultiplier: (255, 255, 255)),
                        LegacyTexture(0x0019, "A.TGA")),
                    LegacyMaterial("EXPANDED1", LegacyInfo(attributes: 1, diffuse: (80, 90, 100), diffuseMultiplier: (255, 255, 255)),
                        LegacyTexture(0x0019, "B.TGA"))),
                LeafChunk(0x0021, UInt16Payload([1, 0])),
            ]);
        Assert.True(EnbW3dModel.TryDecode(bytes, out var model));
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(Resolve(bytes, model, "A.TGA", "B.TGA"),
            out var resource, out var diagnostic), diagnostic);
        var pass = Assert.Single(Assert.Single(resource.Meshes).MaterialPasses);
        Assert.Equal("RETAINED", pass.CurrentRetailUniformMaterial!.Value.Material.Name);
        Assert.Equal(EnbW3dRenderBindingSourceKind.ModernAuthored, pass.CurrentRetailUniformMaterial.Value.SourceKind);
        Assert.NotNull(pass.CurrentRetailUniformLighting);
        Assert.Equal("RETAINED", pass.TriangleBindings[0].VertexMaterial!.Value.Material.Name);
        Assert.Equal("RETAINED", pass.TriangleBindings[1].VertexMaterial!.Value.Material.Name);
        Assert.Equal("A.TGA", Assert.Single(pass.TextureStages).CurrentRetailUniformTexture!.LogicalName);
        Assert.Equal("B.TGA", pass.TriangleBindings[0].Textures[0]!.LogicalName);
        Assert.Equal("A.TGA", pass.TriangleBindings[1].Textures[0]!.LogicalName);
        Assert.Equal(pass.TriangleBindings[1].Shader!.Value.FixedFunction.PackedRetailState, pass.CurrentRetailUniformShader);
        Assert.NotEqual(pass.TriangleBindings[0].Shader!.Value.FixedFunction.PackedRetailState, pass.CurrentRetailUniformShader);
        // Copying a two-pass descriptor preserves target pass-0 arrays but
        // does not transplant the source's expanded bindings into new pass 1.
        Assert.True(EnbW3dRenderResourceCompiler.TryReplaceUniformDescriptor(
            Assert.Single(resource.Meshes), [pass, (pass).Copy().SetOrdinal(1)],
            out var replaced, out diagnostic), diagnostic);
        Assert.Equal(2, replaced.Count);
        Assert.Equal(pass.VertexMaterials, replaced[0].VertexMaterials);
        Assert.Same(pass.Shaders, replaced[0].Shaders);
        Assert.Same(pass.TextureStages[0].Textures, replaced[0].TextureStages[0].Textures);
        var alternateCoordinates = (pass).Copy().SetCurrentRetailUniformMaterial(pass.CurrentRetailUniformMaterial!.Value with
                { CurrentRetailRuntimeCoordinates = new(7, 6) });
        Assert.True(EnbW3dRenderResourceCompiler.TryReplaceUniformDescriptor(
            Assert.Single(resource.Meshes), [alternateCoordinates, (alternateCoordinates).Copy().SetOrdinal(1)],
            out var coordinateReplacement, out diagnostic), diagnostic);
        Assert.Equal(7, coordinateReplacement[0].TextureStages[0].EffectiveTextureCoordinateSetIndex);
        Assert.Equal(7, coordinateReplacement[1].TextureStages[0].EffectiveTextureCoordinateSetIndex);
        Assert.Equal(6, coordinateReplacement[1].TextureStages[1].EffectiveTextureCoordinateSetIndex);
        Assert.Empty(coordinateReplacement[1].TextureStages[0].EffectiveRuntimeTexCoords);
        Assert.Equal(EnbW3dRenderBindingCardinality.Uniform, replaced[1].VertexMaterialCardinality);
        Assert.Equal(EnbW3dRenderBindingCardinality.Uniform, replaced[1].ShaderCardinality);
        Assert.All(replaced[1].TriangleBindings, triangle =>
        {
            Assert.Equal("RETAINED", triangle.VertexMaterial!.Value.Material.Name);
            Assert.Equal("A.TGA", triangle.Textures[0]!.LogicalName);
            Assert.Null(triangle.Textures[1]);
            Assert.Equal(pass.CurrentRetailUniformShader, triangle.Semantics!.Value.FixedFunction.PackedRetailState);
        });
        Assert.True(EnbW3dRenderResourceCompiler.TryReplaceUniformDescriptor(
            (Assert.Single(resource.Meshes)).Copy().SetMaterialPasses(replaced), [pass],
            out var reduced, out diagnostic), diagnostic);
        Assert.Single(reduced);
    }

    [Fact]
    public void UniformDescriptorRetainsMaterialTextureAndOptimizedShader()
    {
        byte[] bytes = BuildLegacyMesh(1, LegacyMaterials(
            LegacyMaterial("UNIFORM", LegacyInfo(), LegacyTexture(0x0019, "A.TGA"))));
        Assert.True(EnbW3dModel.TryDecode(bytes, out var model));
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(Resolve(bytes, model, "A.TGA"),
            out var resource, out var diagnostic), diagnostic);
        var pass = Assert.Single(Assert.Single(resource.Meshes).MaterialPasses);
        Assert.Equal(Assert.Single(pass.VertexMaterials), pass.CurrentRetailUniformMaterial);
        Assert.Equal(pass.SingleBindingSemantics!.Value.VertexLighting, pass.CurrentRetailUniformLighting);
        Assert.Equal(Assert.Single(pass.Shaders).FixedFunction.PackedRetailState, pass.CurrentRetailUniformShader);
        var stage = Assert.Single(pass.TextureStages);
        Assert.Equal(Assert.Single(stage.Textures), stage.CurrentRetailUniformTexture);
    }
    private static readonly Vector3[] DefaultVertices = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY];

    private static byte[] BuildLegacyMesh(int legacyMaterialCount, params byte[][] children)
        => BuildLegacyMesh(legacyMaterialCount, [(0u, 1u, 2u)], children);

    private static byte[] BuildLegacyMesh(
        int legacyMaterialCount,
        IReadOnlyList<(uint V0, uint V1, uint V2)> triangles,
        IReadOnlyList<byte[]> children)
        => BuildMixedMesh(
            legacyMaterialCount,
            modernMaterialCount: 0,
            modernShaderCount: 0,
            modernTextureCount: 0,
            triangles,
            children);

    private static byte[] BuildMixedMesh(
        int legacyMaterialCount,
        int modernMaterialCount,
        int modernShaderCount,
        int modernTextureCount,
        params byte[][] children)
        => BuildMixedMesh(
            legacyMaterialCount,
            modernMaterialCount,
            modernShaderCount,
            modernTextureCount,
            [(0u, 1u, 2u)],
            children);

    private static byte[] BuildMixedMesh(
        int legacyMaterialCount,
        int modernMaterialCount,
        int modernShaderCount,
        int modernTextureCount,
        IReadOnlyList<(uint V0, uint V1, uint V2)> triangles,
        IReadOnlyList<byte[]> children)
    {
        int vertexCount = checked((int)(triangles.SelectMany(static value => new[] { value.V0, value.V1, value.V2 }).Max() + 1));
        Vector3[] vertices = vertexCount == 3
            ? DefaultVertices
            : Enumerable.Range(0, vertexCount).Select(index => new Vector3(index & 1, index / 2, 0)).ToArray();

        var rootChildren = new List<byte[]>
        {
            LeafChunk(0x001F, BuildMeshHeader((uint)triangles.Count, (uint)vertexCount, (uint)legacyMaterialCount)),
            LeafChunk(0x0002, Vector3Payload(vertices)),
            LeafChunk(0x0020, Concat(triangles.Select(static value => TrianglePayload(value.V0, value.V1, value.V2)).ToArray())),
            LeafChunk(0x0028, UInt32Payload([
                checked((uint)children.Count(static child => IsChunkType(child, 0x0038))),
                (uint)modernMaterialCount,
                (uint)modernShaderCount,
                (uint)modernTextureCount,
            ])),
        };
        rootChildren.AddRange(children);
        return ContainerChunk(0x0000, rootChildren.ToArray());
    }

    private static byte[] LegacyMaterials(params byte[][] records)
        => ContainerChunk(0x0015, records);

    private static byte[] LegacyMaterial(string name, byte[] info, params byte[][] optionalChildren)
        => ContainerChunk(
            0x0016,
            new[]
            {
                LeafChunk(0x0017, Encoding.ASCII.GetBytes(name + "\0")),
                LeafChunk(0x0018, info),
            }.Concat(optionalChildren).ToArray());

    private static byte[] LegacyTexture(uint type, string name)
        => ContainerChunk(
            type,
            LeafChunk(0x001A, Encoding.ASCII.GetBytes(name + "\0")),
            LeafChunk(0x001B, new byte[8]));

    private static byte[] LegacyInfo(
        byte attributes = 0,
        (byte R, byte G, byte B) diffuse = default,
        (byte R, byte G, byte B) specular = default,
        (byte R, byte G, byte B) emissive = default,
        (byte R, byte G, byte B) ambient = default,
        (byte R, byte G, byte B) diffuseMultiplier = default,
        (byte R, byte G, byte B) specularMultiplier = default,
        float power = 1f,
        float alpha = 1f)
    {
        byte[] info = new byte[0x2C];
        info[0] = attributes;
        WriteRgb(info.AsSpan(0x04, 3), diffuse);
        WriteRgb(info.AsSpan(0x08, 3), specular);
        WriteRgb(info.AsSpan(0x0C, 3), emissive);
        WriteRgb(info.AsSpan(0x10, 3), ambient);
        WriteRgb(info.AsSpan(0x14, 3), diffuseMultiplier);
        WriteRgb(info.AsSpan(0x18, 3), specularMultiplier);
        WriteSingle(info.AsSpan(0x1C, 4), power);
        WriteSingle(info.AsSpan(0x20, 4), alpha);
        return info;
    }

    private static byte[] ModernVertexMaterial(string name)
    {
        byte[] info = new byte[32];
        new byte[] { 255, 255, 255, 0 }.CopyTo(info, 4);
        new byte[] { 80, 90, 100, 0 }.CopyTo(info, 8);
        new byte[] { 20, 30, 40, 0 }.CopyTo(info, 12);
        new byte[] { 0, 0, 0, 0 }.CopyTo(info, 16);
        WriteSingle(info.AsSpan(20, 4), 1f);
        WriteSingle(info.AsSpan(24, 4), 1f);
        return ContainerChunk(
            0x002B,
            LeafChunk(0x002C, Encoding.ASCII.GetBytes(name + "\0")),
            LeafChunk(0x002D, info));
    }

    private static byte[] ModernShaderRecord()
        => [3, 1, 0, 0, 2, 1, 0, 1, 1, 2, 0, 2, 0, 2, 0, 2];

    private static EnbResolvedW3dResource Resolve(byte[] bytes, EnbW3dModel model, params string[] textureNames)
    {
        string archivePath = Path.Combine(Path.GetTempPath(), "r4w_mixfile_art.mix");
        EnbResolvedTextureReference[] textures = textureNames
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select((name, index) => new EnbResolvedTextureReference(
                name,
                new EnbMixResourceLocation(name, archivePath, 4096 + index * 256, 128)))
            .ToArray();
        return new EnbResolvedW3dResource(
            "R4W.W3D",
            "R4W_ROOT",
            new EnbMixResourceLocation("R4W.W3D", archivePath, 1024, bytes.Length),
            model,
            textures);
    }

    private static Vector3 Rgb((byte R, byte G, byte B) value)
        => new(value.R / 255f, value.G / 255f, value.B / 255f);

    private static Vector3 MultiplyRgb(
        (byte R, byte G, byte B) left,
        (byte R, byte G, byte B) right)
        => Vector3.Multiply(Rgb(left), Rgb(right));

    private static void AssertVector(Vector3 expected, Vector3 actual)
        => Assert.InRange(Vector3.Distance(expected, actual), 0f, 1e-7f);

    private static bool IsChunkType(byte[] bytes, uint type)
        => bytes.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4)) == type;

    private static byte[] BuildMeshHeader(uint triangleCount, uint vertexCount, uint legacyMaterialCount)
    {
        byte[] header = new byte[116];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), 0x0004_0002u);
        WriteFixedAscii(header.AsSpan(8, 16), "R4W_MESH");
        WriteFixedAscii(header.AsSpan(24, 16), "R4W_ROOT");
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40, 4), triangleCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(44, 4), vertexCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(48, 4), legacyMaterialCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(68, 4), EnbW3dVertexChannels.Location);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(72, 4), EnbW3dFaceChannels.Face);
        WriteSingle(header.AsSpan(112, 4), 2f);
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

    private static byte[] UInt16Payload(IEnumerable<ushort> values)
    {
        ushort[] snapshot = values.ToArray();
        byte[] bytes = new byte[snapshot.Length * 2];
        for (int index = 0; index < snapshot.Length; index++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(index * 2, 2), snapshot[index]);
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

    private static void WriteRgb(Span<byte> destination, (byte R, byte G, byte B) value)
    {
        destination[0] = value.R;
        destination[1] = value.G;
        destination[2] = value.B;
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
