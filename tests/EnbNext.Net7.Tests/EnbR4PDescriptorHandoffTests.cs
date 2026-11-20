using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR4PDescriptorHandoffTests
{
    [Fact]
    public void FirstRetailTwoPassOptimizationTransfersStage0TextureAndCoordinateSetIntoPass0()
    {
        byte[] bytes = BuildTwoPassMesh();
        Assert.True(EnbW3dModel.TryDecode(bytes, out EnbW3dModel model));
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(
            Resolve(bytes, model),
            out EnbW3dRenderResource resource,
            out string? diagnostic), diagnostic);

        EnbW3dRenderMesh mesh = Assert.Single(resource.Meshes);
        Assert.Equal(EnbW3dRetailShaderOptimizationKind.TwoPassTextureHandoff, mesh.RetailPostLoadShaderOptimization);
        Assert.Equal(2, mesh.RetailTextureCoordinateSets.Count);
        // The pool hashes the canonical runtime-space float bytes, not the
        // mathematically ideal decimal values. In particular, the third V is
        // produced by the retail-compatible single-precision operation
        // 1.0f - 0.60f, whose exact result bits are 0x3ECCCCCC rather than the
        // nearest source literal 0.40f bits 0x3ECCCCCD.
        Assert.Equal(0xC507_BE26u, mesh.RetailTextureCoordinateSets[0].RetailCrc32);

        EnbW3dRenderMaterialPass pass0 = mesh.MaterialPasses[0];
        EnbW3dRenderMaterialPass pass1 = mesh.MaterialPasses[1];
        EnbW3dRenderTextureStage pass0Stage0 = Assert.Single(pass0.TextureStages);
        EnbW3dRenderTextureStage pass1Stage0 = Assert.Single(pass1.TextureStages);

        // Primary descriptor provenance is retained for pass 0, while the
        // effective current-retail stage-0 texture-coordinate index state comes
        // from pass 1 after FUN_009BCE00.
        Assert.Equal(0, pass0Stage0.RetailDescriptorCoordinateSetIndex);
        Assert.Equal(1, pass1Stage0.RetailDescriptorCoordinateSetIndex);
        Assert.Equal(pass1Stage0.EffectiveTextureCoordinateSetIndex, pass0Stage0.EffectiveTextureCoordinateSetIndex);
        Assert.Equal(mesh.RetailTextureCoordinateSets[1].Coordinates, pass0Stage0.EffectiveRuntimeTexCoords);

        EnbW3dRenderTextureBinding pass0Texture = Assert.Single(pass0Stage0.Textures);
        Assert.Equal((uint)1, pass0Texture.SourceId);
        Assert.Equal("R4P_B.TGA", pass0Texture.LogicalName);

        Assert.NotNull(pass0.SingleBindingSemantics);
        Assert.NotNull(pass1.SingleBindingSemantics);
        Assert.True(pass0.SingleBindingSemantics!.Value.FixedFunction.TextureCombiners.TextureStateEnabled);
        Assert.Equal(EnbW3dFogMode.SceneColor, pass0.SingleBindingSemantics.Value.FixedFunction.FogMode);
        Assert.Equal(EnbW3dFogMode.Black, pass1.SingleBindingSemantics!.Value.FixedFunction.FogMode);
        Assert.Equal(EnbRenderBlendFactor.One, pass1.SingleBindingSemantics.Value.FixedFunction.DestinationBlend);

        Assert.Equal((uint)1, Assert.Single(pass0.TriangleBindings).Textures[0]!.SourceId);
    }

    [Fact]
    public void RetailCoordinatePoolDeduplicatesByCrcAndUsesFirstSeenSlot()
    {
        byte[] bytes = BuildTwoPassMesh(useSameCoordinates: true);
        Assert.True(EnbW3dModel.TryDecode(bytes, out EnbW3dModel model));
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(
            Resolve(bytes, model),
            out EnbW3dRenderResource resource,
            out string? diagnostic), diagnostic);

        EnbW3dRenderMesh mesh = Assert.Single(resource.Meshes);
        Assert.Single(mesh.RetailTextureCoordinateSets);
        Assert.Equal(0, mesh.MaterialPasses[0].TextureStages[0].RetailDescriptorCoordinateSetIndex);
        Assert.Equal(0, mesh.MaterialPasses[1].TextureStages[0].RetailDescriptorCoordinateSetIndex);
        Assert.Equal(0, mesh.MaterialPasses[0].TextureStages[0].EffectiveTextureCoordinateSetIndex);
    }

    [Fact]
    public void EffectiveRenderResourceCarriesPostLoadFogRatherThanOnlyAuthoredCompactState()
    {
        byte[] bytes = BuildTwoPassMesh();
        Assert.True(EnbW3dModel.TryDecode(bytes, out EnbW3dModel model));
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(
            Resolve(bytes, model),
            out EnbW3dRenderResource resource,
            out string? diagnostic), diagnostic);

        EnbW3dRenderMesh mesh = Assert.Single(resource.Meshes);
        EnbW3dRenderMaterialPass pass0 = mesh.MaterialPasses[0];

        Assert.Empty(pass0.Shaders); // authored descriptor used retail baseline
        Assert.Equal(EnbW3dFogMode.SceneColor, pass0.SingleBindingSemantics!.Value.PermutationKey.FogMode);
        Assert.Equal(
            EnbW3dRetailSortCompiler.ClassifyPackedShader(EnbW3dMaterialCompiler.RetailW3dShaderBaseline),
            EnbW3dRetailSortCompiler.ClassifyPackedShader(pass0.SingleBindingSemantics.Value.FixedFunction.PackedRetailState));
    }

    [Fact]
    public void R4PBoundaryRemainsBackendNeutral()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(root, "src", "EnbNext.Rendering", "EnbW3dRenderResource.cs"));

        Assert.Contains("RetailTextureCoordinateSets", source, StringComparison.Ordinal);
        Assert.Contains("EffectiveTextureCoordinateSetIndex", source, StringComparison.Ordinal);
        Assert.Contains("TryApplyRetailPostLoadOptimization", source, StringComparison.Ordinal);
        Assert.Contains("MaximumCoordinateSets = 8", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Veldrid", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Silk.NET", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OpenGL", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Vulkan", source, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] BuildTwoPassMesh(bool useSameCoordinates = false)
    {
        Vector3[] vertices = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY];
        Vector3[] normals = Enumerable.Repeat(Vector3.UnitZ, vertices.Length).ToArray();

        // Current compact W3D translation yields source selector 2,
        // destination selector 2 and texture-state enabled.
        byte[] pass1Shader =
        [
            3, 1, 0, 2,
            0, 1, 0, 2,
            1, 2, 0, 0,
            0, 0, 0, 0,
        ];

        Vector2[] uvA = [new(0.10f, 0.20f), new(0.30f, 0.40f), new(0.50f, 0.60f)];
        Vector2[] uvB = useSameCoordinates
            ? uvA
            : [new(0.70f, 0.15f), new(0.20f, 0.35f), new(0.90f, 0.55f)];

        byte[] pass0 = ContainerChunk(
            0x0038,
            LeafChunk(0x0039, UInt32Payload([0u])),
            ContainerChunk(
                0x0048,
                LeafChunk(0x0049, UInt32Payload([0u])),
                LeafChunk(0x004A, TexCoordPayload(uvA))));

        byte[] pass1 = ContainerChunk(
            0x0038,
            LeafChunk(0x0039, UInt32Payload([1u])),
            LeafChunk(0x003A, UInt32Payload([0u])),
            ContainerChunk(
                0x0048,
                LeafChunk(0x0049, UInt32Payload([1u])),
                LeafChunk(0x004A, TexCoordPayload(uvB))));

        return ContainerChunk(
            0x0000,
            LeafChunk(0x001F, BuildMeshHeader("R4P_MESH", "R4P_ROOT", 1, 3, 2f)),
            LeafChunk(0x0002, Vector3Payload(vertices)),
            LeafChunk(0x0003, Vector3Payload(normals)),
            LeafChunk(0x0020, TrianglePayload(0, 1, 2)),
            LeafChunk(0x0028, UInt32Payload([2u, 2u, 1u, 2u])),
            LeafChunk(0x0029, pass1Shader),
            ContainerChunk(
                0x002A,
                VertexMaterial("MAT0", (40, 50, 60)),
                VertexMaterial("MAT1", (80, 90, 100))),
            ContainerChunk(
                0x0030,
                ContainerChunk(0x0031, LeafChunk(0x0032, Encoding.ASCII.GetBytes("R4P_A.TGA\0"))),
                ContainerChunk(0x0031, LeafChunk(0x0032, Encoding.ASCII.GetBytes("R4P_B.TGA\0")))),
            pass0,
            pass1);
    }

    private static EnbResolvedW3dResource Resolve(byte[] bytes, EnbW3dModel model)
    {
        string archivePath = Path.Combine(Path.GetTempPath(), "r4p_mixfile_art.mix");
        return new EnbResolvedW3dResource(
            "R4P.W3D",
            "R4P_ROOT",
            new EnbMixResourceLocation("R4P.W3D", archivePath, 1024, bytes.Length),
            model,
            [
                new EnbResolvedTextureReference("R4P_A.TGA", new EnbMixResourceLocation("R4P_A.DDS", archivePath, 4096, 128)),
                new EnbResolvedTextureReference("R4P_B.TGA", new EnbMixResourceLocation("R4P_B.DDS", archivePath, 8192, 128)),
            ]);
    }

    private static byte[] VertexMaterial(string name, (byte R, byte G, byte B) diffuse)
    {
        byte[] info = new byte[32];
        new byte[] { 255, 255, 255, 0 }.CopyTo(info, 4);
        new byte[] { diffuse.R, diffuse.G, diffuse.B, 0 }.CopyTo(info, 8);
        WriteSingle(info.AsSpan(20, 4), 1f);
        WriteSingle(info.AsSpan(24, 4), 1f);
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
        throw new DirectoryNotFoundException("Unable to locate EnbNext repository root.");
    }
}
