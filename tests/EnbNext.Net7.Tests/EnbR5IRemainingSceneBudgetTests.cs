using System.Collections.Immutable;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR5IRemainingSceneBudgetTests
{
    [Fact]
    public void SharedAllocatorAcceptsPolymorphicNonHlodCandidateFamilies()
    {
        var sphere = Candidate(
            "SpherePrototypeClass",
            current: 0,
            costs: [10f, 20f],
            priorities: [float.MaxValue, 5f, -1f]);
        var particle = Candidate(
            "ParticleBufferClass",
            current: 0,
            costs: [5f, 15f],
            priorities: [float.MaxValue, 2f, -1f]);

        EnbSharedRenderDetailBudgetAllocation result =
            EnbRenderDetailBudgetAllocator.AllocateShared(
                budget: 25f,
                [sphere, particle]);

        Assert.Equal(25f, result.FinalRenderCost);
        Assert.Equal(1, Selection(result, "SpherePrototypeClass").LevelOrdinal);
        Assert.Equal(0, Selection(result, "ParticleBufferClass").LevelOrdinal);
    }

    [Fact]
    public void SharedAllocatorCarriesFixedSceneCostIndependentlyOfCandidateFamily()
    {
        EnbSharedRenderDetailBudgetAllocation result =
            EnbRenderDetailBudgetAllocator.AllocateShared(
                budget: 100f,
                Array.Empty<EnbRenderDetailOwner>(),
                fixedRenderCost: 17f);

        Assert.Equal(17f, result.FinalRenderCost);
        Assert.Empty(result.Selections);
    }

    [Fact]
    public void HlodCompatibilityFacadeUsesSamePolymorphicSharedAllocator()
    {
        var hlod = new EnbHlodDetailOwner(
            "SHIPROOT",
            CurrentLevelOrdinal: 0,
            MinimumLevelOrdinal: 0,
            ImmutableArray.Create(10f, 20f),
            ImmutableArray.Create(float.MaxValue, 5f, -1f),
            ProjectedDetail: 1f);

        EnbRenderDetailBudgetAllocation hlodResult =
            EnbRenderDetailBudgetAllocator.Allocate(20f, [hlod]);
        EnbSharedRenderDetailBudgetAllocation sharedResult =
            EnbRenderDetailBudgetAllocator.AllocateShared(
                20f,
                [Candidate(
                    "SHIPROOT",
                    current: 0,
                    costs: [10f, 20f],
                    priorities: [float.MaxValue, 5f, -1f])]);

        Assert.Equal(sharedResult.FinalRenderCost, hlodResult.FinalRenderCost);
        Assert.Equal(
            Assert.Single(sharedResult.Selections).LevelOrdinal,
            Assert.Single(hlodResult.HlodSelections).LevelOrdinal);
    }

    [Fact]
    public void SharedCandidateRequiresCurrentRetailBoundarySentinels()
    {
        var missingDowngradeSentinel = Candidate(
            "bad",
            current: 0,
            costs: [1f, 2f],
            priorities: [0f, 1f, -1f]);

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            EnbRenderDetailBudgetAllocator.AllocateShared(
                2f,
                [missingDowngradeSentinel]));

        Assert.Contains("no-downgrade sentinel", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void R5ISharedAllocatorRemainsBackendNeutralAndNotHlodLocked()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "EnbNext.Rendering",
            "EnbRenderDetailBudgetAllocator.cs"));

        Assert.Contains("EnbRenderDetailOwner", source, StringComparison.Ordinal);
        Assert.Contains("AllocateShared", source, StringComparison.Ordinal);
        Assert.Contains("sphere prototypes", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("particle buffers", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Direct3D", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OpenGL", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Vulkan", source, StringComparison.OrdinalIgnoreCase);
    }

    private static EnbRenderDetailOwner Candidate(
        string name,
        int current,
        float[] costs,
        float[] priorities)
        => new(
            name,
            current,
            costs.ToImmutableArray(),
            priorities.ToImmutableArray());

    private static EnbSharedRenderDetailBudgetSelection Selection(
        EnbSharedRenderDetailBudgetAllocation result,
        string name)
        => Assert.Single(result.Selections.Where(selection => selection.Name == name));

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
