using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR5LSegLineBudgetTests
{
    [Fact]
    public void NativeBoundsIncludeWidthAndSubdividedMidpointNoise()
    {
        Vector3[] points=[Vector3.Zero,new(10,0,0)];
        var plain=EnbSegLineDetailBudgetSemantics.Bounds(points,2,4,0);
        var noisy=EnbSegLineDetailBudgetSemantics.Bounds(points,2,4,7);
        Assert.Equal(new Vector3(5,0,0),plain.Center);
        Assert.Equal(MathF.Sqrt(38),plain.Radius);
        Assert.Equal(plain.Center,noisy.Center);
        Assert.Equal(MathF.Sqrt(243),noisy.Radius);
        var scaled=EnbSegLineDetailBudgetSemantics.Bounds(points.Select(p=>p*2).ToArray(),4,8,7);
        Assert.Equal(noisy.Center*2,scaled.Center);Assert.Equal(noisy.Radius*2,scaled.Radius);
    }
    [Theory]
    [InlineData(0u, 0)]
    [InlineData(1u, 1)]
    [InlineData(6u, 6)]
    [InlineData(7u, 7)]
    [InlineData(8u, 7)]
    [InlineData(uint.MaxValue, 7)]
    public void AuthoredSubDivsClampToCurrentRetailZeroThroughSeven(uint requested, int expected)
    {
        Assert.Equal(expected, EnbSegLineDetailBudgetSemantics.ClampMaximumLevel(requested));
    }

    [Theory]
    [InlineData(-9, 4, 0)]
    [InlineData(0, 4, 0)]
    [InlineData(3, 4, 3)]
    [InlineData(4, 4, 4)]
    [InlineData(99, 4, 4)]
    public void CurrentLevelSetterClampsToZeroThroughObjectMaximum(int requested, int maximum, int expected)
    {
        Assert.Equal(expected, EnbSegLineDetailBudgetSemantics.ClampCurrentLevel(requested, maximum));
    }

    [Fact]
    public void LevelCostDoublesPerSubdivisionFromTwoCostsPerSegment()
    {
        var costs = EnbSegLineDetailBudgetSemantics.BuildLevelCosts(
            pointCount: 4,
            maximumLevelOrdinal: 3);

        Assert.Equal(new[] { 6f, 12f, 24f, 48f }, costs);
    }

    [Fact]
    public void ZeroSubDivsUsesFixedCostAndDoesNotBecomeSharedCandidate()
    {
        Assert.Equal(6f, EnbSegLineDetailBudgetSemantics.FixedRenderCost(pointCount: 4));

        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            EnbSegLineDetailBudgetSemantics.CreateCandidate(
                "line",
                pointCount: 4,
                requestedSubDivisions: 0,
                currentLevelOrdinal: 0,
                projectedDetail: 100f));
        Assert.Contains("fixed render cost", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CandidateUsesTargetLevelCostForExactTransitionPriority()
    {
        const float projectedDetail = 125f;

        EnbRenderDetailOwner candidate =
            EnbSegLineDetailBudgetSemantics.CreateCandidate(
                "line",
                pointCount: 4,
                requestedSubDivisions: 3,
                currentLevelOrdinal: 0,
                projectedDetail: projectedDetail);

        Assert.Equal(new[] { 6f, 12f, 24f, 48f }, candidate.LevelCosts);
        Assert.Equal(float.MaxValue, candidate.TransitionPriorities[0]);
        Assert.Equal(-1f, candidate.TransitionPriorities[^1]);

        for (int level = 1; level < candidate.LevelCosts.Count; level++)
        {
            float cost = candidate.LevelCosts[level];
            float expected = (1f - (0.5f / (cost * cost))) * projectedDetail / cost;
            Assert.Equal(expected, candidate.TransitionPriorities[level]);
        }
    }

    [Fact]
    public void SegLineUsesTheCommonCurrentRetailProjectedDetailMetric()
    {
        EnbResolvedRenderCamera camera = Camera();
        var bounds = new EnbW3dWorldBounds(new Vector3(0f, 0f, 10f), 2f);

        Assert.True(EnbSegLineDetailBudgetSemantics.TryComputeProjectedDetail(
            camera,
            bounds,
            out float lineProjectedDetail));
        Assert.True(EnbCurrentRetailProjectedDetail.TryCompute(
            camera,
            bounds,
            out float commonProjectedDetail));

        Assert.Equal(commonProjectedDetail, lineProjectedDetail);
    }

    [Fact]
    public void CandidateParticipatesInSharedHeapUsingTheCurrentLevelCostLadder()
    {
        EnbRenderDetailOwner line =
            EnbSegLineDetailBudgetSemantics.CreateCandidate(
                "line",
                pointCount: 2,
                requestedSubDivisions: 2,
                currentLevelOrdinal: 0,
                projectedDetail: 100f);

        EnbSharedRenderDetailBudgetAllocation result =
            EnbRenderDetailBudgetAllocator.AllocateShared(
                budget: 4f,
                [line]);

        Assert.Equal(4f, result.FinalRenderCost);
        Assert.Equal(1, Assert.Single(result.Selections).LevelOrdinal);
    }

    [Fact]
    public void CurrentRetailLineConfigValidDomainRequiresAtLeastTwoPoints()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EnbSegLineDetailBudgetSemantics.BuildLevelCosts(
                pointCount: 1,
                maximumLevelOrdinal: 1));
    }

    [Fact]
    public void R5LSemanticsRemainBackendNeutralAndKeepRetailAuthoredInputsExplicit()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "EnbNext.Rendering",
            "EnbSegLineDetailBudgetSemantics.cs"));

        Assert.Contains("SegLineRObj", source, StringComparison.Ordinal);
        Assert.Contains("segline_create.cpp", source, StringComparison.Ordinal);
        Assert.Contains("SubDivs", source, StringComparison.Ordinal);
        Assert.Contains("00969A90", source, StringComparison.Ordinal);
        Assert.Contains("0096F0C0/0096F180", source, StringComparison.Ordinal);
        Assert.Contains("EnbCurrentRetailProjectedDetail.TryCompute", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Direct3D", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OpenGL", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Vulkan", source, StringComparison.OrdinalIgnoreCase);
    }

    private static EnbResolvedRenderCamera Camera()
    {
        Assert.True(EnbRenderViewport.TryFromLogicalSize(1000, 500, 1, out EnbRenderViewport viewport));
        Assert.True(EnbResolvedRenderCamera.TryCreate(
            Vector3.Zero,
            Quaternion.Identity,
            viewport,
            Math.PI / 2,
            new EnbRenderClipRange(1f, 10_000f),
            out EnbResolvedRenderCamera camera));
        return camera;
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

        throw new DirectoryNotFoundException("Unable to locate EnbNext repository root from test output directory.");
    }
}
