using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR5KParticleBufferBudgetTests
{
    [Fact]
    public void PrimaryParticleBufferUsesSeventeenLevelsStartingAtLogicalSixteen()
    {
        Assert.Equal(17, EnbParticleBufferDetailBudgetSemantics.CurrentRetailPrimaryLevelCount);
        Assert.Equal(16, EnbParticleBufferDetailBudgetSemantics.CurrentRetailPrimaryInitialLevelOrdinal);
    }

    [Theory]
    [InlineData(0u, 0)]
    [InlineData(1u, 1)]
    [InlineData(8u, 8)]
    [InlineData(16u, 16)]
    [InlineData(17u, 17)]
    [InlineData(99u, 17)]
    public void CopyConstructorLevelCountCapsAtSeventeen(uint countLikeValue, int expected)
    {
        Assert.Equal(expected, EnbParticleBufferDetailBudgetSemantics.ResolveCopiedLevelCount(countLikeValue));
    }

    [Theory]
    [InlineData(0, 2f, 32f)]
    [InlineData(1, 4f, 64f)]
    [InlineData(2, 3.9375f, 63f)]
    [InlineData(3, 8f, 128f)]
    [InlineData(4, 16f, 256f)]
    public void CostModesUseExactCurrentRetailLinearSteps(int costMode, float expectedStep, float expectedLast)
    {
        var costs = EnbParticleBufferDetailBudgetSemantics.BuildLevelCosts(
            countLikeValue: 32,
            costMode);

        Assert.Equal(17, costs.Length);
        Assert.Equal(1e-6f, costs[0]);
        Assert.Equal(expectedStep, costs[1]);
        Assert.Equal(expectedLast, costs[16]);
    }

    [Fact]
    public void UnknownCostModePreservesRetailZeroStepFallback()
    {
        var costs = EnbParticleBufferDetailBudgetSemantics.BuildLevelCosts(
            countLikeValue: 32,
            costMode: 99);

        Assert.All(costs, cost => Assert.Equal(1e-6f, cost));

        EnbRenderDetailOwner candidate =
            EnbParticleBufferDetailBudgetSemantics.CreateCandidate(
                "particle",
                currentLevelOrdinal: 8,
                countLikeValue: 32,
                costMode: 99,
                smoothedProjectedDetail: 100f);

        Assert.Equal(float.MaxValue, candidate.TransitionPriorities[0]);
        Assert.All(candidate.TransitionPriorities.Skip(1).SkipLast(1), priority => Assert.Equal(0f, priority));
        Assert.Equal(-1f, candidate.TransitionPriorities[^1]);
    }

    [Fact]
    public void PositiveLevelPriorityUsesGuardedCurrentRetailFormula()
    {
        const float projectedDetail = 75f;
        const float detailScale = 1.25f;

        EnbRenderDetailOwner candidate =
            EnbParticleBufferDetailBudgetSemantics.CreateCandidate(
                "particle",
                currentLevelOrdinal: 8,
                countLikeValue: 32,
                costMode: 0,
                smoothedProjectedDetail: projectedDetail,
                detailScale: detailScale);

        Assert.Equal(float.MaxValue, candidate.TransitionPriorities[0]);
        Assert.Equal(-1f, candidate.TransitionPriorities[^1]);

        int transition = 5;
        float cost = candidate.LevelCosts[transition];
        float expected =
            (1f - (0.5f / (cost * cost))) * detailScale * projectedDetail / cost;
        Assert.Equal(expected, candidate.TransitionPriorities[transition]);
    }

    [Fact]
    public void CustomProjectedDetailUsesBoundedParticleAreaAndTenPercentSmoothing()
    {
        EnbResolvedRenderCamera camera = Camera();
        var bounds = new EnbW3dWorldBounds(new Vector3(0f, 0f, 10f), 2f);

        Assert.True(EnbParticleBufferDetailBudgetSemantics.TryComputeProjectedDetail(
            camera,
            bounds,
            countLikeValue: 4,
            perElementRadius: 0.5f,
            previousSmoothedProjectedDetail: 100f,
            out EnbParticleBufferProjectedDetail result));

        // 90-degree horizontal FOV with a 1000x500 viewport gives 500 pixels
        // per projection unit on both axes. At distance 10, the whole-sphere
        // term is 0.04 while 4 * (0.5/10)^2 is 0.01, so the particle term wins.
        float expectedSample = 0.01f * 500f * 500f * MathF.PI;
        float expectedSmoothed = 100f * 0.9f + expectedSample * 0.1f;

        Assert.InRange(result.Sample, expectedSample - 0.1f, expectedSample + 0.1f);
        Assert.InRange(result.Smoothed, expectedSmoothed - 0.1f, expectedSmoothed + 0.1f);
    }

    [Fact]
    public void CameraCoincidentCenterLeavesNewSampleAtZeroButDecaysHistory()
    {
        EnbResolvedRenderCamera camera = Camera();
        var bounds = new EnbW3dWorldBounds(Vector3.Zero, 2f);

        Assert.True(EnbParticleBufferDetailBudgetSemantics.TryComputeProjectedDetail(
            camera,
            bounds,
            countLikeValue: 4,
            perElementRadius: 0.5f,
            previousSmoothedProjectedDetail: 100f,
            out EnbParticleBufferProjectedDetail result));

        Assert.Equal(0f, result.Sample);
        Assert.Equal(90f, result.Smoothed);
    }

    [Fact]
    public void ParticleCandidateParticipatesInSharedAllocatorDownToEpsilonLevelZero()
    {
        EnbRenderDetailOwner particle =
            EnbParticleBufferDetailBudgetSemantics.CreateCandidate(
                "particle",
                EnbParticleBufferDetailBudgetSemantics.CurrentRetailPrimaryInitialLevelOrdinal,
                countLikeValue: 32,
                costMode: 0,
                smoothedProjectedDetail: 1f);

        EnbSharedRenderDetailBudgetAllocation result =
            EnbRenderDetailBudgetAllocator.AllocateShared(
                budget: 0f,
                [particle]);

        Assert.Equal(1e-6f, result.FinalRenderCost);
        Assert.Equal(0, Assert.Single(result.Selections).LevelOrdinal);
    }

    [Fact]
    public void R5KSemanticsRemainBackendNeutralAndDoNotReusePlainHlodProjection()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "EnbNext.Rendering",
            "EnbParticleBufferDetailBudgetSemantics.cs"));

        Assert.Contains("ParticleBufferClass", source, StringComparison.Ordinal);
        Assert.Contains("0095EAD0", source, StringComparison.Ordinal);
        Assert.Contains("0096F0C0/0096F180", source, StringComparison.Ordinal);
        Assert.Contains("CurrentRetailPreviousSampleWeight", source, StringComparison.Ordinal);
        Assert.DoesNotContain("EnbCurrentRetailProjectedDetail.TryCompute", source, StringComparison.Ordinal);
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
