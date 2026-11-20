using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR5JSphereRingBudgetTests
{
    [Fact]
    public void SpherePrototypeUsesExactCurrentRetailSharedCosts()
    {
        float[] expected =
        [
            0f,
            98f,
            128f,
            162f,
            200f,
            242f,
            288f,
            338f,
            392f,
            450f,
            512f,
        ];

        Assert.Equal(11, EnbSphereRingDetailBudgetSemantics.SphereLevelCosts.Length);
        Assert.Equal(10, EnbSphereRingDetailBudgetSemantics.SphereInitialLevelOrdinal);
        Assert.Equal(expected, EnbSphereRingDetailBudgetSemantics.SphereLevelCosts.ToArray());
    }

    [Fact]
    public void RingPrototypeUsesExactCurrentRetailSharedCosts()
    {
        float[] expected =
        [
            0f,
            20f,
            24f,
            28f,
            32f,
            36f,
            40f,
            44f,
            48f,
            52f,
            56f,
            60f,
            64f,
            68f,
            72f,
            76f,
            80f,
            84f,
            88f,
            92f,
            96f,
        ];

        Assert.Equal(21, EnbSphereRingDetailBudgetSemantics.RingLevelCosts.Length);
        Assert.Equal(20, EnbSphereRingDetailBudgetSemantics.RingInitialLevelOrdinal);
        Assert.Equal(expected, EnbSphereRingDetailBudgetSemantics.RingLevelCosts.ToArray());
    }

    [Fact]
    public void SpherePriorityUsesCurrentRetailFormulaAndBoundarySentinels()
    {
        const float projectedDetail = 75f;
        const float detailScale = 1.25f;

        EnbRenderDetailOwner candidate =
            EnbSphereRingDetailBudgetSemantics.CreateSphereCandidate(
                "sphere",
                currentLevelOrdinal: 4,
                projectedDetail,
                detailScale);

        Assert.Equal(float.MaxValue, candidate.TransitionPriorities[0]);
        Assert.Equal(-1f, candidate.TransitionPriorities[^1]);

        float cost = candidate.LevelCosts[1];
        float expected =
            (1f - (0.5f / (cost * cost))) * detailScale * projectedDetail / cost;
        Assert.Equal(expected, candidate.TransitionPriorities[1]);
    }

    [Fact]
    public void RingPriorityUsesCurrentRetailFormulaAndBoundarySentinels()
    {
        const float projectedDetail = 48f;
        const float detailScale = 0.75f;

        EnbRenderDetailOwner candidate =
            EnbSphereRingDetailBudgetSemantics.CreateRingCandidate(
                "ring",
                currentLevelOrdinal: 7,
                projectedDetail,
                detailScale);

        Assert.Equal(float.MaxValue, candidate.TransitionPriorities[0]);
        Assert.Equal(-1f, candidate.TransitionPriorities[^1]);

        int transition = 12;
        float cost = candidate.LevelCosts[transition];
        float expected =
            (1f - (0.5f / (cost * cost))) * detailScale * projectedDetail / cost;
        Assert.Equal(expected, candidate.TransitionPriorities[transition]);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(6, 6)]
    [InlineData(10, 10)]
    [InlineData(99, 10)]
    public void SphereCurrentLevelClampsLikeRetailSetter(int requested, int expected)
    {
        EnbRenderDetailOwner candidate =
            EnbSphereRingDetailBudgetSemantics.CreateSphereCandidate(
                "sphere",
                requested,
                projectedDetail: 1f);

        Assert.Equal(expected, candidate.CurrentLevelOrdinal);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(13, 13)]
    [InlineData(20, 20)]
    [InlineData(99, 20)]
    public void RingCurrentLevelClampsLikeRetailSetter(int requested, int expected)
    {
        EnbRenderDetailOwner candidate =
            EnbSphereRingDetailBudgetSemantics.CreateRingCandidate(
                "ring",
                requested,
                projectedDetail: 1f);

        Assert.Equal(expected, candidate.CurrentLevelOrdinal);
    }

    [Fact]
    public void SphereAndRingCandidatesParticipateInCommonAllocatorDownToZeroCostLevel()
    {
        EnbRenderDetailOwner sphere =
            EnbSphereRingDetailBudgetSemantics.CreateSphereCandidate(
                "sphere",
                EnbSphereRingDetailBudgetSemantics.SphereInitialLevelOrdinal,
                projectedDetail: 1f);
        EnbRenderDetailOwner ring =
            EnbSphereRingDetailBudgetSemantics.CreateRingCandidate(
                "ring",
                EnbSphereRingDetailBudgetSemantics.RingInitialLevelOrdinal,
                projectedDetail: 1f);

        EnbSharedRenderDetailBudgetAllocation result =
            EnbRenderDetailBudgetAllocator.AllocateShared(
                budget: 0f,
                [sphere, ring]);

        Assert.Equal(0f, result.FinalRenderCost);
        Assert.All(result.Selections, selection => Assert.Equal(0, selection.LevelOrdinal));
    }

    [Fact]
    public void R5JSemanticsRemainBackendNeutral()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "EnbNext.Rendering",
            "EnbSphereRingDetailBudgetSemantics.cs"));

        Assert.Contains("SpherePrototypeClass", source, StringComparison.Ordinal);
        Assert.Contains("RingPrototypeClass", source, StringComparison.Ordinal);
        Assert.Contains("0096F0C0/0096F180", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Direct3D", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OpenGL", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Vulkan", source, StringComparison.OrdinalIgnoreCase);
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
