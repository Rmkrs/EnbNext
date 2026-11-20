using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR9AoCurrentRetailBaseAssetOverbrightTests
{
    [Fact]
    public void RetainedNameBindingsPreserveLoaderOrderAliasesAndReplacement()
    {
        var first=(CreatePacket("body",0x00C9451Bu).Mesh).Copy().SetName("SHIP.LEAF").SetContainerName("");
        var second=(first).Copy().SetName("LEAF").SetContainerName("SHIP");
        EnbW3dRenderMesh[] meshes=[first,second];
        foreach(string name in new[]{"leaf","SHIP.LEAF","ship.ship.leaf","missing"})
            Assert.Same(meshes.FirstOrDefault(m=>EnbW3dRenderObjectNameSemantics.MatchesMesh(m,name)),
                EnbW3dRenderObjectNameSemantics.FindFirstMesh(meshes,name));
        Assert.Same(first,EnbW3dRenderObjectNameSemantics.FindFirstMesh(meshes,"ship.leaf"));
        Assert.Same(second,EnbW3dRenderObjectNameSemantics.FindFirstMesh(new[]{second,first},"ship.leaf"));
    }

    [Theory]
    [InlineData(0x00C9451Bu, 0x00C9491Bu, 0x00C9551Bu, 0x00C9491Bu)]
    [InlineData(0x00C9491Bu, 0x00C9451Bu, 0x00C9491Bu, 0x00C9551Bu)]
    public void RetainedUniformSlotAndExpandedTrianglesMutateIndependently(uint uniform,uint triangle,uint expectedUniform,uint expectedTriangle)
    {
        var source=CreatePacket("body",triangle);
        source=(source).ForkTask().SetMaterial(new((source.Material.Source).Copy().SetCurrentRetailUniformShader(uniform),null));
        var result=EnbCurrentRetailBaseAssetOverbrightRuntime.Apply(source,false,out var receipt);
        Assert.True(receipt.Applied);
        Assert.Equal(expectedUniform,result.Material.Source.CurrentRetailUniformShader);
        Assert.Equal(expectedTriangle,Assert.Single(result.Material.Source.TriangleBindings).Shader!.Value.FixedFunction.PackedRetailState);
        Assert.Equal(uniform,source.Material.Source.CurrentRetailUniformShader);
        Assert.Same(result,EnbCurrentRetailBaseAssetOverbrightRuntime.Apply(result,false,out var repeated));
        Assert.Equal(EnbCurrentRetailBaseAssetOverbrightOutcome.NoModulateShader,repeated.Outcome);
    }

    [Fact]
    public void EmptyExpandedArrayStillRewritesRetainedUniformSlot()
    {
        var source=CreatePacket("empty");
        source=(source).ForkTask().SetMaterial(new((source.Material.Source).Copy().SetCurrentRetailUniformShader(0x00C9451B),null));
        var result=EnbCurrentRetailBaseAssetOverbrightRuntime.Apply(source,false,out var receipt);
        Assert.True(receipt.Applied);
        Assert.Equal(0,receipt.AppliedTriangles);
        Assert.Equal(0x00C9551Bu,result.Material.Source.CurrentRetailUniformShader);
        Assert.Empty(result.Material.Source.TriangleBindings);
        Assert.Empty(result.Material.Source.Shaders);
        Assert.Same(source,EnbCurrentRetailBaseAssetOverbrightRuntime.Apply(source,true,out _));
    }

    [Fact]
    public void PostLoadCacheKeepsMaterialIdentityAcrossPoseChangesAndHonorsOptOutAndClear()
    {
        var source = CreatePacket("body", 0x00C9_451Bu);
        var cache = new EnbCurrentRetailBaseAssetOverbrightCache();
        var first = cache.Apply(source, false, out var receipt);
        var moved = (source).ForkTask().SetWorldTransform(Matrix4x4.CreateTranslation(42,7,9)).SetFrameTimeMilliseconds(123);
        var next = cache.Apply(moved, false, out var nextReceipt);
        Assert.Same(first.Material, next.Material);
        Assert.Equal(receipt, nextReceipt);
        Assert.Equal(moved.WorldTransform, next.WorldTransform);
        Assert.Equal(123, next.FrameTimeMilliseconds);
        Assert.Same(moved, cache.Apply(moved, true, out _));
        cache.Clear();
        Assert.NotSame(first.Material, cache.Apply(source, false, out _).Material);
    }

    [Fact]
    public void EnabledBaseAssetRewritesOnlyStageZeroModulateTriangles()
    {
        const uint modulate = 0x00C9_451Bu;
        uint add = (modulate & 0xFFFF_E3FFu) | 0x0000_0800u;
        EnbW3dDrawTask source = CreatePacket("body", modulate, add);

        EnbW3dDrawTask effective = EnbCurrentRetailBaseAssetOverbrightRuntime.Apply(
            source,
            overbrightOff: false,
            out EnbCurrentRetailBaseAssetOverbrightReceipt receipt);

        Assert.Equal(EnbCurrentRetailBaseAssetOverbrightOutcome.Applied, receipt.Outcome);
        Assert.Equal(1, receipt.TargetTriangles);
        Assert.Equal(1, receipt.AppliedTriangles);
        Assert.NotSame(source, effective);

        uint expected = (modulate & 0xFFFF_F7FFu) | 0x0000_1400u;
        Assert.Equal(expected, effective.Material.Source.TriangleBindings[0].Semantics!.Value.FixedFunction.PackedRetailState);
        Assert.Equal(expected, effective.Material.Source.TriangleBindings[0].Shader!.Value.FixedFunction.PackedRetailState);
        Assert.Equal(expected, effective.Material.Source.Shaders[0].FixedFunction.PackedRetailState);
        Assert.Equal((byte)5, effective.Material.Source.TriangleBindings[0].Semantics!.Value.FixedFunction.TextureCombiners.Selector0);

        Assert.Equal(add, effective.Material.Source.TriangleBindings[1].Semantics!.Value.FixedFunction.PackedRetailState);
        Assert.Equal(add, effective.Material.Source.Shaders[1].FixedFunction.PackedRetailState);
        Assert.Equal((byte)2, effective.Material.Source.TriangleBindings[1].Semantics!.Value.FixedFunction.TextureCombiners.Selector0);

        Assert.Equal(modulate, source.Material.Source.TriangleBindings[0].Semantics!.Value.FixedFunction.PackedRetailState);
    }

    [Fact]
    public void AuthoredOverbrightOffIsAStrictOptOut()
    {
        EnbW3dDrawTask source = CreatePacket("body", 0x00C9_451Bu);

        EnbW3dDrawTask effective = EnbCurrentRetailBaseAssetOverbrightRuntime.Apply(
            source,
            overbrightOff: true,
            out EnbCurrentRetailBaseAssetOverbrightReceipt receipt);

        Assert.Same(source, effective);
        Assert.Equal(EnbCurrentRetailBaseAssetOverbrightOutcome.DisabledByBaseAsset, receipt.Outcome);
        Assert.Equal(0, receipt.TargetTriangles);
        Assert.Equal(0, receipt.AppliedTriangles);
    }

    [Fact]
    public void OverbrightThenShipColorizationPreservesBothExactSelectorMutations()
    {
        EnbW3dDrawTask source = CreatePacket("~s1b", 0x00C9_451Bu);
        EnbW3dDrawTask overbright = EnbCurrentRetailBaseAssetOverbrightRuntime.Apply(
            source,
            overbrightOff: false,
            out EnbCurrentRetailBaseAssetOverbrightReceipt overbrightReceipt);
        Assert.True(overbrightReceipt.Applied);

        EnbW3dDrawTask colored = EnbCurrentRetailShipColorizationRuntime.Apply(
            overbright,
            [Side(-1, new Vector3(.47f, .025f, .025f)), Side(0, Vector3.Zero)],
            0,
            EnbEnvironmentMapAxis.Z,
            out EnbCurrentRetailShipColorizationReceipt colorizationReceipt);

        Assert.True(colorizationReceipt.Applied);
        EnbW3dFixedFunctionState state =
            Assert.Single(colored.Material.Source.TriangleBindings).Semantics!.Value.FixedFunction;
        Assert.Equal((byte)5, state.TextureCombiners.Selector0);
        Assert.Equal((byte)0x0C, state.TextureCombiners.Selector1);
    }

    private static EnbSceneColorizationSnapshot Side(int selector, Vector3 rgb)
    {
        byte[] bytes = new byte[12];
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(0, 4), rgb.X);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(4, 4), rgb.Y);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(8, 4), rgb.Z);
        return new EnbSceneColorizationSnapshot(unchecked((uint)selector), bytes.ToImmutableArray());
    }

    private static EnbW3dDrawTask CreatePacket(string materialName, params uint[] packedStates)
    {
        var material = new EnbW3dRenderVertexMaterial(
            0,
            materialName,
            0,
            new EnbRenderPackedRgb8(113, 0, 0, 0),
            new EnbRenderPackedRgb8(113, 0, 0, 0),
            new EnbRenderPackedRgb8(64, 64, 64, 0),
            new EnbRenderPackedRgb8(0, 0, 0, 0),
            8f,
            1f,
            0f,
            null,
            null);
        var materialBinding = new EnbW3dRenderVertexMaterialBinding(0, material);
        var shaders = new EnbW3dRenderShaderBinding[packedStates.Length];
        var triangles = new EnbW3dRenderTriangleBinding[packedStates.Length];
        for (int index = 0; index < packedStates.Length; index++)
        {
            uint packed = packedStates[index];
            Assert.True(EnbW3dMaterialCompiler.TryDecodePackedRetailState(
                packed,
                out EnbW3dFixedFunctionState fixedFunction,
                out string? decodeDiagnostic), decodeDiagnostic);
            shaders[index] = new EnbW3dRenderShaderBinding(
                (uint)index,
                new EnbW3dRenderShaderRecord(packed, 0, 0, 0),
                fixedFunction);
            Assert.True(EnbW3dMaterialCompiler.TryCompilePackedRetailState(
                packed,
                material,
                2,
                out EnbW3dCompiledMaterialSemantics semantics,
                out string? compileDiagnostic), compileDiagnostic);
            triangles[index] = new EnbW3dRenderTriangleBinding(
                index,
                0,
                materialBinding,
                shaders[index],
                new EnbW3dRenderTextureBinding?[] { null, null },
                semantics);
        }

        EnbW3dRenderTextureStage[] stages =
        [
            new EnbW3dRenderTextureStage(0, [], [], EnbW3dRenderBindingCardinality.None),
            new EnbW3dRenderTextureStage(1, [], [], EnbW3dRenderBindingCardinality.None),
        ];
        var pass = new EnbW3dRenderMaterialPass(
            0,
            [materialBinding],
            EnbW3dRenderBindingCardinality.Uniform,
            shaders,
            EnbW3dRenderBindingCardinality.PerTriangle,
            [],
            [],
            [],
            stages,
            triangles,
            null);
        uint[] indices = Enumerable.Range(0, packedStates.Length)
            .SelectMany(static _ => new uint[] { 0, 1, 2 })
            .ToArray();
        EnbW3dRenderTriangleMetadata[] metadata = Enumerable.Range(0, packedStates.Length)
            .Select(static index => new EnbW3dRenderTriangleMetadata((uint)index, Vector3.UnitZ, 0f))
            .ToArray();
        var mesh = new EnbW3dRenderMesh(
            0,
            "SHIP_SURFACE",
            "SHIP",
            1f,
            [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
            [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
            [],
            indices,
            metadata,
            [material],
            [pass]) { CurrentRetailLoadedMaterials = [material] };
        return new EnbW3dDrawTask(
            default,
            1,
            1,
            1,
            Matrix4x4.Identity,
            new EnbW3dWorldBounds(Vector3.Zero, 1f),
            mesh,
            null,
            new EnbW3dDrawMaterialBinding(pass, null),
            EnbW3dLightEnvironment.BlackNoLights,
            0,
            null,
            0,
            "SHIP.SURFACE");
    }
}
