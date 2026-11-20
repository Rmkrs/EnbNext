using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR4NGeometrySortOwnershipTests
{
    [Theory]
    [InlineData(0x0008_441Bu, EnbW3dRetailShaderSortClass.Class0)]
    [InlineData(0x000C_441Bu, EnbW3dRetailShaderSortClass.Class1)]
    [InlineData(0x000C_84BBu, EnbW3dRetailShaderSortClass.Class1)]
    [InlineData(0x0008_443Bu, EnbW3dRetailShaderSortClass.Class2)]
    [InlineData(0x0008_447Bu, EnbW3dRetailShaderSortClass.Class3)]
    [InlineData(0x0008_449Bu, EnbW3dRetailShaderSortClass.Class4)]
    public void RetailShaderSortClassifierMatchesFiniteCurrentFunction(
        uint packedRetailState,
        EnbW3dRetailShaderSortClass expected)
    {
        Assert.Equal(expected, EnbW3dRetailSortCompiler.ClassifyPackedShader(packedRetailState));
    }

    [Fact]
    public void ModernGeometrySortRequestComesOnlyFromPassZeroNonAlphaDestinationBlend()
    {
        const uint baseline = EnbW3dMaterialCompiler.RetailW3dShaderBaseline;
        uint passZeroClass2 = ReplaceBlend(baseline, sourceSelector: 1, destinationSelector: 1, alphaTest: false);
        uint sourceZeroDestinationZero = ReplaceBlend(baseline, sourceSelector: 0, destinationSelector: 0, alphaTest: false);
        uint alphaTestedDestinationBlend = ReplaceBlend(baseline, sourceSelector: 1, destinationSelector: 1, alphaTest: true);

        EnbW3dGeometrySortSemantics requested = EnbW3dRetailSortCompiler.CompileCurrentRetail(
            authoredSortLevel: 0,
            [Pass(0, passZeroClass2)]);
        Assert.True(requested.Requested);
        Assert.Equal(0x0000_0004u, requested.PassZeroShaderClassMask);
        Assert.Equal(0x0000_0004u, requested.AllPassShaderClassMask);

        EnbW3dGeometrySortSemantics sourceOnlyBlend = EnbW3dRetailSortCompiler.CompileCurrentRetail(
            authoredSortLevel: 0,
            [Pass(0, sourceZeroDestinationZero)]);
        Assert.False(sourceOnlyBlend.Requested);
        EnbW3dFixedFunctionState sourceOnlyState = Decode(sourceZeroDestinationZero);
        Assert.True(sourceOnlyState.BlendEnabled);
        Assert.Equal(EnbRenderBlendFactor.Zero, sourceOnlyState.DestinationBlend);

        Assert.False(EnbW3dRetailSortCompiler.CompileCurrentRetail(
            authoredSortLevel: 0,
            [Pass(0, alphaTestedDestinationBlend)]).Requested);

        Assert.False(EnbW3dRetailSortCompiler.CompileCurrentRetail(
            authoredSortLevel: 0,
            [Pass(0, baseline), Pass(1, passZeroClass2)]).Requested);

        Assert.False(EnbW3dRetailSortCompiler.CompileCurrentRetail(
            authoredSortLevel: 1,
            [Pass(0, passZeroClass2)]).Requested);
    }

    [Theory]
    [InlineData(0x0000_0001u, 0x0000_0010u, (sbyte)0)]
    [InlineData(0x0000_0002u, 0x0000_0003u, (sbyte)0)]
    [InlineData(0x0000_0004u, 0x0000_0004u, (sbyte)10)]
    [InlineData(0x0000_0008u, 0x0000_0008u, (sbyte)15)]
    [InlineData(0x0000_0010u, 0x0000_0010u, (sbyte)20)]
    [InlineData(0x0000_0006u, 0x0000_0006u, (sbyte)20)]
    public void AutoStaticSortFallbackMatchesCurrentRetailShaderMasks(
        uint passZeroClassMask,
        uint allPassClassMask,
        sbyte expectedRuntimeLevel)
    {
        var geometrySort = new EnbW3dGeometrySortSemantics(
            Requested: true,
            PassZeroShaderClassMask: passZeroClassMask,
            AllPassShaderClassMask: allPassClassMask);

        EnbW3dStaticSortSemantics result = EnbW3dStaticSortSemantics.ResolveCurrentRetail(0, geometrySort);

        Assert.Equal(expectedRuntimeLevel, result.RuntimeLevel);
        Assert.Equal(EnbW3dStaticSortLevelSource.CurrentRetailGeometrySortFallback, result.RuntimeLevelSource);
        Assert.Equal(
            expectedRuntimeLevel == 0
                ? EnbW3dStaticSortDisposition.Immediate
                : EnbW3dStaticSortDisposition.DeferredBucket,
            result.Disposition);
    }

    [Fact]
    public void AuthoredNonZeroSortLevelWinsAndCurrentApplicationDoesNotEnableLowLevelDepthSorter()
    {
        var geometrySort = new EnbW3dGeometrySortSemantics(
            Requested: true,
            PassZeroShaderClassMask: 0x0000_0010u,
            AllPassShaderClassMask: 0x0000_0010u);

        EnbW3dStaticSortSemantics authored = EnbW3dStaticSortSemantics.ResolveCurrentRetail(0x0000_0020u, geometrySort);

        Assert.Equal((sbyte)32, authored.RuntimeLevel);
        Assert.Equal(EnbW3dStaticSortLevelSource.AuthoredLowByte, authored.RuntimeLevelSource);
        Assert.True(authored.IsDeferred);
        Assert.False(EnbW3dGeometrySortSemantics.CurrentRetailLowLevelSortingRendererEnabled);
        Assert.True(EnbW3dGeometrySortSemantics.CurrentRetailAutoStaticSortFallbackEnabled);
        Assert.False(geometrySort.UsesCurrentRetailLowLevelSortingRenderer);
    }

    [Fact]
    public void MeshExposesEffectiveFallbackLevelInsteadOfTreatingAuthoredZeroAsFinal()
    {
        const uint class2Packed = 0x0008_443Bu;
        var mesh = new EnbW3dRenderMesh(
            0,
            "SORTED",
            "ROOT",
            1f,
            Array.Empty<System.Numerics.Vector3>(),
            Array.Empty<System.Numerics.Vector3>(),
            Array.Empty<ushort>(),
            Array.Empty<uint>(),
            Array.Empty<EnbW3dRenderTriangleMetadata>(),
            Array.Empty<EnbW3dRenderVertexMaterial>(),
            [Pass(0, class2Packed)])
        {
            AuthoredSortLevel = 0,
        };

        Assert.True(mesh.GeometrySort.Requested);
        Assert.Equal((sbyte)10, mesh.StaticSort.RuntimeLevel);
        Assert.Equal(EnbW3dStaticSortLevelSource.CurrentRetailGeometrySortFallback, mesh.StaticSort.RuntimeLevelSource);
        Assert.Equal(22, mesh.StaticSort.DeferredFlushOrdinal.GetValueOrDefault());
        Assert.True(mesh.StaticSort.DeferredFlushOrdinal.HasValue);
    }

    [Fact]
    public void RetainedSortFollowsReplacementWithoutChangingSharedModel()
    {
        var mesh = new EnbW3dRenderMesh(0,"SORTED","ROOT",1,[],[],[],[],[],[],[Pass(0,0x0008_443Bu)])
            { CurrentRetailUsesMeshRenderer=true };
        Assert.Equal((sbyte)10,mesh.StaticSort.RuntimeLevel);
        var materialReplacement=(mesh).Copy().SetMaterialPasses([Pass(0,EnbW3dMaterialCompiler.RetailW3dShaderBaseline)]);
        var authoredReplacement=(mesh).Copy().SetAuthoredSortLevel(7);
        var requestReplacement=(mesh).Copy().SetCurrentRetailGeometrySortRequestOverride(false);
        Assert.Equal((sbyte)0,materialReplacement.StaticSort.RuntimeLevel);
        Assert.Equal((sbyte)7,authoredReplacement.StaticSort.RuntimeLevel);
        Assert.False(requestReplacement.GeometrySort.Requested);
        Assert.Equal((sbyte)10,mesh.StaticSort.RuntimeLevel);
    }

    [Fact]
    public void R4NOrderingContractContainsNoInventedCameraDepthSort()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(root, "src", "EnbNext.Rendering", "EnbW3dDrawPackets.cs"));

        Assert.Contains("low-level SortingRenderer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("cameraDistance", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("depth-sort blended", source, StringComparison.OrdinalIgnoreCase);
    }

    private static EnbW3dRenderMaterialPass Pass(int ordinal, params uint[] packedStates)
    {
        EnbW3dRenderShaderBinding[] shaders = packedStates
            .Select((packed, index) => new EnbW3dRenderShaderBinding(
                (uint)index,
                default,
                Decode(packed)))
            .ToArray();

        EnbW3dRenderBindingCardinality shaderCardinality = shaders.Length switch
        {
            0 => EnbW3dRenderBindingCardinality.None,
            1 => EnbW3dRenderBindingCardinality.Uniform,
            _ => EnbW3dRenderBindingCardinality.PerTriangle,
        };

        return new EnbW3dRenderMaterialPass(
            ordinal,
            Array.Empty<EnbW3dRenderVertexMaterialBinding>(),
            EnbW3dRenderBindingCardinality.None,
            shaders,
            shaderCardinality,
            Array.Empty<EnbRenderColor32>(),
            Array.Empty<EnbRenderColor32>(),
            Array.Empty<EnbRenderColor32>(),
            Array.Empty<EnbW3dRenderTextureStage>(),
            Array.Empty<EnbW3dRenderTriangleBinding>(),
            null);
    }

    private static EnbW3dFixedFunctionState Decode(uint packedRetailState)
    {
        Assert.True(EnbW3dMaterialCompiler.TryDecodePackedRetailState(
            packedRetailState,
            out EnbW3dFixedFunctionState state,
            out string? diagnostic), diagnostic);
        return state;
    }

    private static uint ReplaceBlend(
        uint packedRetailState,
        int sourceSelector,
        int destinationSelector,
        bool alphaTest)
    {
        uint packed = packedRetailState & ~(0x0000_C000u | 0x0000_00E0u | 0x0004_0000u);
        packed |= (uint)sourceSelector << 14;
        packed |= (uint)destinationSelector << 5;
        if (alphaTest)
            packed |= 0x0004_0000u;
        return packed;
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
