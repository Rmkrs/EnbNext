using System.Buffers.Binary;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR4VLegacyRootVertexStreamTests
{
    private static readonly Vector3[] Vertices = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY];
    private static readonly Vector2[] RootUv = [new(0.10f, 0.20f), new(0.30f, 0.40f), new(0.50f, 0.60f)];
    private static readonly Vector2[] ModernUv = [new(0.70f, 0.15f), new(0.20f, 0.35f), new(0.90f, 0.55f)];

    [Theory]
    [InlineData(0x004Au)]
    [InlineData(0x0005u)]
    public void Root0005BeforeModernOwnsPrimaryPass0Stage0AndDivertsModernCoordinates(uint modernChunkType)
    {
        byte[] bytes = BuildMesh(
            LeafChunk(0x0005, TexCoordPayload(RootUv)),
            MaterialPassWithCoordinates(modernChunkType, ModernUv));

        Assert.True(EnbW3dModel.TryDecode(bytes, out EnbW3dModel model));
        EnbW3dMesh mesh = Assert.Single(model.Meshes);

        EnbW3dPrimaryTextureCoordinateBinding binding = Assert.Single(mesh.CurrentRetailPrimaryTextureCoordinateBindings);
        Assert.Equal(0, binding.MaterialPassOrdinal);
        Assert.Equal(0, binding.TextureStageOrdinal);
        Assert.Equal(0, binding.CoordinateSetIndex);
        Assert.Single(mesh.CurrentRetailPrimaryTextureCoordinateSets);
        Assert.Equal(mesh.LegacyRootTextureCoordinates, mesh.CurrentRetailPrimaryTextureCoordinateSets[0].Coordinates);

        EnbW3dTextureStage stage = Assert.Single(Assert.Single(mesh.MaterialPasses).TextureStages);
        Assert.Empty(stage.RuntimeTexCoords);
        EnbW3dSecondaryMaterialDescriptorChunk secondary = Assert.Single(mesh.SecondaryMaterialDescriptorChunks);
        Assert.Equal(modernChunkType, secondary.ChunkType);
        Assert.Equal(0, secondary.MaterialPassOrdinal);
        Assert.Equal(0, secondary.TextureStageOrdinal);
    }

    [Fact]
    public void Root0005AfterModernRebindsPrimaryWhileRetainingBothCrcSetsAndRendererUsesResolvedBinding()
    {
        byte[] bytes = BuildMesh(
            MaterialPassWithCoordinates(0x004A, ModernUv),
            LeafChunk(0x0005, TexCoordPayload(RootUv)));

        Assert.True(EnbW3dModel.TryDecode(bytes, out EnbW3dModel model));
        EnbW3dMesh sourceMesh = Assert.Single(model.Meshes);
        Assert.Equal(2, sourceMesh.CurrentRetailPrimaryTextureCoordinateSets.Count);
        Assert.Equal(1, Assert.Single(sourceMesh.CurrentRetailPrimaryTextureCoordinateBindings).CoordinateSetIndex);
        Assert.Equal(Canonical(ModernUv), Assert.Single(Assert.Single(sourceMesh.MaterialPasses).TextureStages).RuntimeTexCoords);
        Assert.Equal(Canonical(RootUv), sourceMesh.CurrentRetailPrimaryTextureCoordinateSets[1].Coordinates);

        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(
            Resolve(bytes, model),
            out EnbW3dRenderResource resource,
            out string? diagnostic), diagnostic);

        EnbW3dRenderTextureStage stage = Assert.Single(Assert.Single(Assert.Single(resource.Meshes).MaterialPasses).TextureStages);
        Assert.Equal(1, stage.RetailDescriptorCoordinateSetIndex);
        Assert.Equal(sourceMesh.CurrentRetailPrimaryTextureCoordinateSets[1].Coordinates, stage.EffectiveRuntimeTexCoords);
    }

    [Fact]
    public void Root0005UsesExactUOneMinusVCanonicalizationAndRejectsMalformedCardinality()
    {
        byte[] valid = BuildMesh(LeafChunk(0x0005, TexCoordPayload(RootUv)));
        Assert.True(EnbW3dModel.TryDecode(valid, out EnbW3dModel validModel));
        Assert.Equal(Canonical(RootUv), Assert.Single(validModel.Meshes).LegacyRootTextureCoordinates);

        byte[] malformed = BuildMesh(LeafChunk(0x0005, TexCoordPayload(RootUv[..2])));
        Assert.False(EnbW3dModel.TryDecode(malformed, out _));
    }

    [Fact]
    public void Root000DForcesOpaqueAlphaOccupiesPass0AndDivertsLater003B()
    {
        byte[] rootColors =
        [
            10, 20, 30, 1,
            40, 50, 60, 2,
            70, 80, 90, 3,
        ];
        byte[] modernColors =
        [
            1, 2, 3, 4,
            5, 6, 7, 8,
            9, 10, 11, 12,
        ];
        byte[] bytes = BuildMesh(
            LeafChunk(0x000D, rootColors),
            MaterialPass(LeafChunk(0x003B, modernColors)));

        Assert.True(EnbW3dModel.TryDecode(bytes, out EnbW3dModel model));
        EnbW3dMesh mesh = Assert.Single(model.Meshes);
        Assert.Equal(
            [new EnbColor32(10, 20, 30, 255), new EnbColor32(40, 50, 60, 255), new EnbColor32(70, 80, 90, 255)],
            mesh.LegacyRootColor1);

        EnbW3dMaterialPass pass = Assert.Single(mesh.MaterialPasses);
        Assert.True(pass.HasPrimaryColorSource);
        Assert.Empty(pass.DiffuseColors);
        EnbW3dSecondaryMaterialDescriptorChunk secondary = Assert.Single(mesh.SecondaryMaterialDescriptorChunks);
        Assert.Equal(0x003Bu, secondary.ChunkType);
    }

    [Fact]
    public void Root000DAfterMaterialPassStillUpdatesEffectivePass0ColorSourceOwnership()
    {
        byte[] bytes = BuildMesh(
            MaterialPass(),
            LeafChunk(0x000D,
            [
                10, 20, 30, 0,
                40, 50, 60, 0,
                70, 80, 90, 0,
            ]));

        Assert.True(EnbW3dModel.TryDecode(bytes, out EnbW3dModel model));
        EnbW3dMesh mesh = Assert.Single(model.Meshes);
        Assert.True(Assert.Single(mesh.MaterialPasses).HasPrimaryColorSource);
        Assert.Equal(3, mesh.LegacyRootColor1.Count);
    }

    [Fact]
    public void Root000DAfterModernPrimaryColorDoesNotReplaceExistingColor1()
    {
        byte[] modernColors =
        [
            1, 2, 3, 4,
            5, 6, 7, 8,
            9, 10, 11, 12,
        ];
        byte[] bytes = BuildMesh(
            MaterialPass(LeafChunk(0x003B, modernColors)),
            LeafChunk(0x000D,
            [
                10, 20, 30, 0,
                40, 50, 60, 0,
                70, 80, 90, 0,
            ]));

        Assert.True(EnbW3dModel.TryDecode(bytes, out EnbW3dModel model));
        EnbW3dMesh mesh = Assert.Single(model.Meshes);
        EnbW3dMaterialPass pass = Assert.Single(mesh.MaterialPasses);
        Assert.True(pass.HasPrimaryColorSource);
        Assert.Empty(mesh.LegacyRootColor1);
        Assert.Equal(3, pass.DiffuseColors.Count);
        Assert.Equal(new EnbColor32(1, 2, 3, 4), pass.DiffuseColors[0]);
    }

    [Fact]
    public void Root000DDoesNotDivertFirst003CWhichCanOperateOnExistingColor1()
    {
        byte[] rootColors =
        [
            100, 110, 120, 7,
            130, 140, 150, 8,
            160, 170, 180, 9,
        ];
        byte[] illumination =
        [
            255, 255, 255, 1,
            255, 255, 255, 2,
            255, 255, 255, 3,
        ];
        byte[] bytes = BuildMesh(
            LeafChunk(0x000D, rootColors),
            MaterialPass(LeafChunk(0x003C, illumination)));

        Assert.True(EnbW3dModel.TryDecode(bytes, out EnbW3dModel model));
        EnbW3dMesh mesh = Assert.Single(model.Meshes);
        EnbW3dMaterialPass pass = Assert.Single(mesh.MaterialPasses);
        Assert.Equal(3, pass.DiffuseIllumination.Count);
        Assert.Empty(mesh.SecondaryMaterialDescriptorChunks);

        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(
            Resolve(bytes, model),
            out EnbW3dRenderResource resource,
            out string? diagnostic), diagnostic);
        Assert.Equal(
            [new EnbRenderColor32(100, 110, 120, 255), new EnbRenderColor32(130, 140, 150, 255), new EnbRenderColor32(160, 170, 180, 255)],
            Assert.Single(resource.Meshes).EffectiveColor1);
    }

    [Fact]
    public void R4VBoundaryKeepsResolvedDescriptorOwnershipInAssetsAndBackendNeutralRendering()
    {
        string root = FindRepositoryRoot();
        string assets = File.ReadAllText(Path.Combine(root, "src", "EnbNext.Assets", "EnbW3dModel.cs"));
        string rendering = File.ReadAllText(Path.Combine(root, "src", "EnbNext.Rendering", "EnbW3dRenderResource.cs"));

        Assert.Contains("LegacyRootTextureCoordinates", assets, StringComparison.Ordinal);
        Assert.Contains("LegacyRootColor1", assets, StringComparison.Ordinal);
        Assert.Contains("CurrentRetailPrimaryTextureCoordinateBindings", assets, StringComparison.Ordinal);
        Assert.Contains("mesh.CurrentRetailPrimaryTextureCoordinateSets", rendering, StringComparison.Ordinal);
        Assert.DoesNotContain("ComputeRetailCrc32", rendering, StringComparison.Ordinal);
        Assert.DoesNotContain("Veldrid", rendering, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Silk.NET", rendering, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OpenGL", rendering, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Vulkan", rendering, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] BuildMesh(params byte[][] authoredChildren)
    {
        bool hasPass = authoredChildren.Any(static child => IsChunkType(child, 0x0038));
        var children = new List<byte[]>
        {
            LeafChunk(0x001F, BuildMeshHeader((uint)Vertices.Length)),
            LeafChunk(0x0002, Vector3Payload(Vertices)),
            LeafChunk(0x0020, TrianglePayload(0, 1, 2)),
            LeafChunk(0x0028, UInt32Payload(hasPass ? [1u, 0u, 0u, 0u] : [0u, 0u, 0u, 0u])),
        };
        children.AddRange(authoredChildren);
        return ContainerChunk(0x0000, children.ToArray());
    }

    private static byte[] MaterialPass(params byte[][] children)
        => ContainerChunk(0x0038, children);

    private static byte[] MaterialPassWithCoordinates(uint chunkType, IEnumerable<Vector2> coordinates)
        => MaterialPass(ContainerChunk(0x0048, LeafChunk(chunkType, TexCoordPayload(coordinates))));

    private static EnbResolvedW3dResource Resolve(byte[] bytes, EnbW3dModel model)
    {
        string archivePath = Path.Combine(Path.GetTempPath(), "r4v_mixfile_art.mix");
        return new EnbResolvedW3dResource(
            "R4V.W3D",
            "R4V_ROOT",
            new EnbMixResourceLocation("R4V.W3D", archivePath, 1024, bytes.Length),
            model,
            []);
    }

    private static IReadOnlyList<Vector2> Canonical(IEnumerable<Vector2> values)
        => values.Select(static value => new Vector2(value.X, 1.0f - value.Y)).ToArray();

    private static bool IsChunkType(byte[] bytes, uint type)
        => bytes.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4)) == type;

    private static byte[] BuildMeshHeader(uint vertexCount)
    {
        byte[] header = new byte[116];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), 0x0004_0002u);
        WriteFixedAscii(header.AsSpan(8, 16), "R4V_MESH");
        WriteFixedAscii(header.AsSpan(24, 16), "R4V_ROOT");
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40, 4), 1u);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(44, 4), vertexCount);
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

    private static byte[] UInt32Payload(IEnumerable<uint> values)
    {
        uint[] snapshot = values.ToArray();
        byte[] bytes = new byte[snapshot.Length * 4];
        for (int index = 0; index < snapshot.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * 4, 4), snapshot[index]);
        return bytes;
    }

    private static byte[] TexCoordPayload(IEnumerable<Vector2> values)
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
        byte[] payload = Concat(children.Where(static child => child.Length != 0).ToArray());
        byte[] bytes = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), type);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), (uint)payload.Length | 0x8000_0000u);
        payload.CopyTo(bytes, 8);
        return bytes;
    }

    private static byte[] Concat(params byte[][] arrays)
    {
        byte[] result = new byte[arrays.Sum(static array => array.Length)];
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
        System.Text.Encoding.ASCII.GetBytes(value.AsSpan(), destination);
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
        throw new DirectoryNotFoundException("Unable to locate EnbNext repository root.");
    }
}
