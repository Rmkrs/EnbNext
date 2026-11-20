using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Rendering;
using EnbNext.Presentation;

namespace EnbNext.Net7.Tests;

public sealed class EnbR5BSharedHlodBudgetTests
{
    [Fact]
    public void UndoneUpgradeStillChangesNativeSceneRegistrationOrder()
    {
        //Actual96F180 ->98EB60/98EC30 ->965E00/965E90/9661C0:
        //detail-transition-events-native records31 ->13, [-1,2,-2,1],
        //although both final HLOD levels are still zero.
        var scene=new EnbCurrentRetailSceneCallbackList<int>();
        scene.Register(1);scene.Register(3);
        var before=new List<int>();scene.Visit(before.Add);
        Assert.Equal(new[]{3,1},before);
        var events=new List<int>();
        int[] levels=[0,0];
        var result=EnbRenderDetailBudgetAllocator.AllocateShared(35,
            [new("A",0,[10,30],[float.MaxValue,10,-1]),new("B",0,[10,20],[float.MaxValue,5,-1])],
            levelChanged:(index,level)=>
            {
                int old=index*2+levels[index]+1,current=index*2+level+1;
                scene.Remove(old);events.Add(-old);
                scene.Register(current);events.Add(current);levels[index]=level;
            });
        var after=new List<int>();scene.Visit(after.Add);
        Assert.Equal(new[]{-1,2,-2,1},events);
        Assert.Equal(new[]{1,3},after);
        Assert.All(result.Selections,s=>Assert.Equal(0,s.LevelOrdinal));
        Assert.Equal(20,result.FinalRenderCost);
    }

    [Theory]
    [InlineData(0.5f, 1f, 1)]
    [InlineData(1f, 2f, 0)]
    [InlineData(2f, 0.5f, 0)]
    [InlineData(-1f, 0.5f, 2)]
    public void NewHlodUsesAuthoredThresholdsAtUnitDetail(float first, float second, int expected)
    {
        var hlod = new EnbW3dRenderHlod("initial", "",
            new[] { first, second, float.MaxValue }.Select((threshold, ordinal) =>
                new EnbW3dRenderHlodLevel(ordinal, threshold,
                    BitConverter.SingleToUInt32Bits(threshold), Array.Empty<EnbW3dRenderHlodMember>())).ToArray());
        Assert.Equal(expected, EnbW3dHlodBudgetCompiler.CurrentRetailInitialLevel(hlod));
    }

    [Fact]
    public void ProjectedDetailUsesEuclideanSphereDistanceAndStoredViewportScale()
    {
        EnbResolvedRenderCamera camera = Camera();
        var bounds = new EnbW3dWorldBounds(new Vector3(100f, 0f, 0f), 10f);

        Assert.True(EnbCurrentRetailProjectedDetail.TryCompute(camera, bounds, out float projected));

        // Stored viewport0..1, projection(-1,-.5)..(1,.5): scale .5*1.
        Assert.Equal(0.0157079641f, projected);

        // FUN_00941AF0 leaves the radius/distance term at zero when the
        // bounding-sphere center is exactly at the camera position.
        Assert.True(EnbCurrentRetailProjectedDetail.TryCompute(
            camera,
            new EnbW3dWorldBounds(Vector3.Zero, 10f),
            out projected));
        Assert.Equal(0f, projected);
    }

    [Fact]
    public void AuthoredThresholdCreatesMinimumDetailAndCurrentLevelIsPromotedToIt()
    {
        EnbResolvedRenderCamera camera = Camera();
        var hlod = new EnbW3dRenderHlod(
            "SHIPROOT",
            "",
            new EnbW3dRenderHlodLevel[]
            {
                new(0, .002f, BitConverter.SingleToUInt32Bits(.002f), Array.Empty<EnbW3dRenderHlodMember>()),
                new(1, .02f, BitConverter.SingleToUInt32Bits(.02f), Array.Empty<EnbW3dRenderHlodMember>()),
                new(2, float.MaxValue, BitConverter.SingleToUInt32Bits(float.MaxValue), Array.Empty<EnbW3dRenderHlodMember>()),
            });

        Assert.True(EnbW3dHlodBudgetCompiler.TryCreateCandidate(
            hlod,
            currentLevelOrdinal: 0,
            camera,
            new EnbW3dWorldBounds(new Vector3(100f, 0f, 0f), 10f),
            new[] { 10, 20, 30 },
            lodScale: 1f,
            out EnbHlodDetailOwner candidate,
            out string? diagnostic), diagnostic);

        Assert.Equal(1, candidate.MinimumLevelOrdinal);
        Assert.Equal(1, candidate.CurrentLevelOrdinal);
        Assert.Equal(float.MaxValue, candidate.TransitionPriorities[0]);
        Assert.Equal(float.MaxValue, candidate.TransitionPriorities[1]);
        Assert.True(candidate.TransitionPriorities[2] > 0f);
        Assert.Equal(-1f, candidate.TransitionPriorities[3]);
    }

    [Fact]
    public void MaxThresholdsLeaveAllocatorFloorAtLevelZeroAndZeroCostUsesRetailFallback()
    {
        EnbResolvedRenderCamera camera = Camera();
        var hlod = Hlod("ZERO", float.MaxValue, float.MaxValue);

        Assert.True(EnbW3dHlodBudgetCompiler.TryCreateCandidate(
            hlod,
            currentLevelOrdinal: -50,
            camera,
            new EnbW3dWorldBounds(new Vector3(100f, 0f, 0f), 10f),
            new[] { 0, 20 },
            lodScale: 0f,
            out EnbHlodDetailOwner candidate,
            out string? diagnostic), diagnostic);

        Assert.Equal(0, candidate.MinimumLevelOrdinal);
        Assert.Equal(0, candidate.CurrentLevelOrdinal);
        Assert.Equal(EnbW3dHlodBudgetCompiler.CurrentRetailZeroCostFallback, candidate.LevelCosts[0]);
        Assert.Equal(float.MaxValue, candidate.DowngradePriority);
        Assert.Equal(0f, candidate.UpgradePriority);
    }

    [Fact]
    public void NullSentinelLevelZeroRefinesThroughEveryVisibleLevelWhenSharedBudgetIsAvailable()
    {
        EnbResolvedRenderCamera camera = Camera();
        var levels = new EnbW3dRenderHlodLevel[6];
        for (int ordinal = 0; ordinal < levels.Length; ordinal++)
        {
            levels[ordinal] = new EnbW3dRenderHlodLevel(
                ordinal,
                float.MaxValue,
                BitConverter.SingleToUInt32Bits(float.MaxValue),
                Array.Empty<EnbW3dRenderHlodMember>());
        }
        var hlod = new EnbW3dRenderHlod("SHIP_PART", "", levels);

        Assert.True(EnbW3dHlodBudgetCompiler.TryCreateCandidate(
            hlod,
            currentLevelOrdinal: 0,
            camera,
            new EnbW3dWorldBounds(new Vector3(100f, 0f, 0f), 10f),
            new[] { 0, 254, 398, 550, 694, 840 },
            lodScale: 1f,
            out EnbHlodDetailOwner candidate,
            out string? diagnostic), diagnostic);

        EnbRenderDetailBudgetAllocation allocation = EnbRenderDetailBudgetAllocator.Allocate(
            budget: 39_680f,
            [candidate],
            fixedRenderCost: 13_726f);

        EnbW3dHlodSelection selection = Assert.Single(allocation.HlodSelections);
        Assert.Equal(5, selection.LevelOrdinal);
        Assert.Equal(14_566f, allocation.FinalRenderCost);
    }

    [Fact]
    public void SingleLevelHlodIsFixedCostRatherThanRegisteredSharedBudgetCandidate()
    {
        EnbResolvedRenderCamera camera = Camera();
        EnbW3dRenderHlod singleLevel = Hlod("FIXED", float.MaxValue);

        Assert.False(EnbW3dHlodBudgetCompiler.TryCreateCandidate(
            singleLevel,
            currentLevelOrdinal: 0,
            camera,
            new EnbW3dWorldBounds(new Vector3(100f, 0f, 0f), 10f),
            new[] { 7 },
            lodScale: 1f,
            out _,
            out string? diagnostic));
        Assert.Contains("fixed render cost", diagnostic, StringComparison.Ordinal);

        EnbRenderDetailBudgetAllocation allocation = EnbRenderDetailBudgetAllocator.Allocate(
            budget: 100f,
            Array.Empty<EnbHlodDetailOwner>(),
            fixedRenderCost: EnbW3dHlodBudgetCompiler.CurrentRetailLevelCost(7));

        Assert.Equal(7f, allocation.FinalRenderCost);
        Assert.Empty(allocation.HlodSelections);
    }

    [Fact]
    public void SharedAllocatorCanOvershootUpgradeAndCompensateByDowngradingLowerValueDetailElsewhere()
    {
        EnbHlodDetailOwner highValueUpgrade = Candidate(
            "A",
            current: 0,
            costs: [10f, 30f],
            priorities: [float.MaxValue, 10f, -1f]);
        EnbHlodDetailOwner lowValueExistingDetail = Candidate(
            "B",
            current: 1,
            costs: [10f, 20f],
            priorities: [float.MaxValue, 2f, -1f]);

        EnbRenderDetailBudgetAllocation result = EnbRenderDetailBudgetAllocator.Allocate(
            budget: 40f,
            [highValueUpgrade, lowValueExistingDetail]);

        Assert.Equal(40f, result.FinalRenderCost);
        Assert.Equal(1, Selection(result, "A").LevelOrdinal);
        Assert.Equal(0, Selection(result, "B").LevelOrdinal);
    }

    [Fact]
    public void SharedAllocatorStopsWhenCompensationWouldImmediatelyUndoTheSameUpgrade()
    {
        EnbHlodDetailOwner expensiveBestUpgrade = Candidate(
            "A",
            current: 0,
            costs: [10f, 30f],
            priorities: [float.MaxValue, 10f, -1f]);
        EnbHlodDetailOwner cheaperSecondUpgrade = Candidate(
            "B",
            current: 0,
            costs: [10f, 20f],
            priorities: [float.MaxValue, 5f, -1f]);

        EnbRenderDetailBudgetAllocation result = EnbRenderDetailBudgetAllocator.Allocate(
            budget: 35f,
            [expensiveBestUpgrade, cheaperSecondUpgrade]);

        // Retail first tries A (higher transition priority), overshoots, finds
        // A itself as the cheapest removable detail, undoes it, then exits.
        // It does not continue and choose the lower-priority B upgrade that fits.
        Assert.Equal(20f, result.FinalRenderCost);
        Assert.Equal(0, Selection(result, "A").LevelOrdinal);
        Assert.Equal(0, Selection(result, "B").LevelOrdinal);
    }

    [Fact]
    public void SharedAllocatorUsesLowestPriorityDowngradeWhenAlreadyOverBudget()
    {
        EnbHlodDetailOwner expensiveDetail = Candidate(
            "A",
            current: 1,
            costs: [10f, 30f],
            priorities: [float.MaxValue, 10f, -1f]);
        EnbHlodDetailOwner lowValueDetail = Candidate(
            "B",
            current: 1,
            costs: [10f, 20f],
            priorities: [float.MaxValue, 2f, -1f]);

        EnbRenderDetailBudgetAllocation result = EnbRenderDetailBudgetAllocator.Allocate(
            budget: 40f,
            [expensiveDetail, lowValueDetail]);

        Assert.Equal(40f, result.FinalRenderCost);
        Assert.Equal(1, Selection(result, "A").LevelOrdinal);
        Assert.Equal(0, Selection(result, "B").LevelOrdinal);
    }

    [Fact]
    public void W3dCostResolverUsesTriangleCountTimesActiveDescriptorPassCountAndExplicitActivity()
    {
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(
            EnbR4W3dRenderResourceTests.BuildResolvedResource(shaderId: 0),
            out EnbW3dRenderResource compiled,
            out string? diagnostic), diagnostic);

        EnbW3dRenderMesh sourceMesh = Assert.Single(compiled.Meshes);
        Assert.Equal(1, sourceMesh.CurrentRetailRenderPassCount);
        EnbW3dRenderMesh twoPassMesh = (sourceMesh).Copy().SetCurrentRetailRenderPassCount(2);
        EnbW3dRenderResource resource = compiled.Copy().SetMeshes(new[] { twoPassMesh });
        EnbW3dRenderHlod hlod = Assert.Single(resource.HierarchicalLods);

        Assert.True(EnbW3dHlodBudgetCompiler.TryComputeW3dLevelRenderCosts(
            resource,
            hlod,
            static _ => true,
            out ImmutableArray<int> costs,
            out diagnostic), diagnostic);
        Assert.Equal(new[] { 2 }, costs);

        Assert.True(EnbW3dHlodBudgetCompiler.TryComputeW3dLevelRenderCosts(
            resource,
            hlod,
            static _ => false,
            out costs,
            out diagnostic), diagnostic);
        Assert.Equal(new[] { 0 }, costs);
    }

    [Fact]
    public void QualifiedHlodMemberNameResolvesMeshContainerForCostAndDraw()
    {
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(
            EnbR4W3dRenderResourceTests.BuildResolvedResource(shaderId: 0),
            out EnbW3dRenderResource compiled,
            out string? diagnostic), diagnostic);

        EnbW3dRenderMesh mesh = Assert.Single(compiled.Meshes);
        EnbW3dRenderHlod original = Assert.Single(compiled.HierarchicalLods);
        EnbW3dRenderHlodMember originalMember = Assert.Single(Assert.Single(original.Levels).Members);
        string qualifiedName = $"{mesh.ContainerName}.{mesh.Name}";
        var qualified = original with
        {
            Levels = new[]
            {
                original.Levels[0] with
                {
                    Members = new[] { originalMember with { Name = qualifiedName } },
                },
            },
        };
        EnbW3dRenderResource resource = compiled.Copy().SetHierarchicalLods(new[] { qualified });

        Assert.True(EnbW3dHlodBudgetCompiler.TryComputeW3dLevelRenderCosts(
            resource,
            qualified,
            static _ => true,
            out ImmutableArray<int> costs,
            out diagnostic), diagnostic);
        Assert.Equal(new[] { 1 }, costs);

        Assert.True(EnbW3dDrawPacketCompiler.TryBuild(
            SceneObject(),
            resource,
            frameTimeMilliseconds: 0,
            renderPass: EnbRenderPassKind.OrdinaryWorld,
            hlodSelection: new EnbW3dHlodSelection(qualified.Name, 0),
            lightEnvironment: EnbW3dLightEnvironment.BlackNoLights,
            out EnbW3dDrawInvocation drawList,
            out diagnostic), diagnostic);
        Assert.Equal(mesh.Name, Assert.Single(drawList.Packets).Mesh.Name);
    }

    [Fact]
    public void AllocatorOutputFeedsExistingExplicitR4HlodSelectionBoundary()
    {
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(
            EnbR4W3dRenderResourceTests.BuildResolvedResource(shaderId: 0),
            out EnbW3dRenderResource compiled,
            out string? diagnostic), diagnostic);

        EnbW3dRenderHlod original = Assert.Single(compiled.HierarchicalLods);
        EnbW3dRenderHlodMember member = Assert.Single(Assert.Single(original.Levels).Members);
        var twoLevel = new EnbW3dRenderHlod(
            original.Name,
            original.HierarchyName,
            new EnbW3dRenderHlodLevel[]
            {
                new(0, float.MaxValue, BitConverter.SingleToUInt32Bits(float.MaxValue), new[] { member }),
                new(1, float.MaxValue, BitConverter.SingleToUInt32Bits(float.MaxValue), new[] { member }),
            });
        EnbW3dRenderResource resource = compiled.Copy().SetHierarchicalLods(new[] { twoLevel });

        EnbRenderDetailBudgetAllocation allocation = EnbRenderDetailBudgetAllocator.Allocate(
            2f,
            [Candidate("SHIPROOT", 0, [1f, 2f], [float.MaxValue, 5f, -1f])]);
        EnbW3dHlodSelection selection = Assert.Single(allocation.HlodSelections);
        Assert.Equal(1, selection.LevelOrdinal);

        Assert.True(EnbW3dDrawPacketCompiler.TryBuild(
            SceneObject(),
            resource,
            frameTimeMilliseconds: 0,
            renderPass: EnbRenderPassKind.OrdinaryWorld,
            hlodSelection: selection,
            lightEnvironment: EnbW3dLightEnvironment.BlackNoLights,
            out EnbW3dDrawInvocation drawList,
            out diagnostic), diagnostic);
        Assert.Single(drawList.Packets);
        Assert.Equal(selection, drawList.HlodSelection);
    }

    [Fact]
    public void R5BStaysBackendNeutralAndDoesNotInventDistanceThresholds()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "EnbNext.Rendering",
            "EnbRenderDetailBudgetAllocator.cs"));

        Assert.Contains("0096F180", source, StringComparison.Ordinal);
        Assert.Contains("EnbW3dHlodSelection", source, StringComparison.Ordinal);
        Assert.Contains("Euclidean", source, StringComparison.Ordinal);
        Assert.DoesNotContain("cameraDistance", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("distanceThreshold", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Direct3D", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OpenGL", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Vulkan", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Avalonia", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EnbNext.Net7", source, StringComparison.OrdinalIgnoreCase);
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

    private static EnbW3dRenderHlod Hlod(string name, params float[] thresholds)
        => new(
            name,
            "",
            thresholds.Select((threshold, ordinal) => new EnbW3dRenderHlodLevel(
                ordinal,
                threshold,
                BitConverter.SingleToUInt32Bits(threshold),
                Array.Empty<EnbW3dRenderHlodMember>())).ToArray());

    private static EnbHlodDetailOwner Candidate(
        string name,
        int current,
        float[] costs,
        float[] priorities)
        => new(
            name,
            current,
            MinimumLevelOrdinal: 0,
            costs.ToImmutableArray(),
            priorities.ToImmutableArray(),
            ProjectedDetail: 1f);

    private static EnbW3dHlodSelection Selection(EnbRenderDetailBudgetAllocation result, string name)
        => Assert.Single(result.HlodSelections.Where(selection => selection.Name == name));

    private static EnbSceneObjectSnapshot SceneObject()
        => new(
            RenderIdentity: 0x1122_3344_5566_7788UL,
            VisualGeneration: 7,
            GameId: 0x1234,
            IsLocalShip: false,
            IsCurrentTarget: false,
            ObjectType: 1,
            BaseAsset: 42,
            Scale: 1f,
            HsvH: 0,
            HsvS: 0,
            HsvV: 0,
            RelationshipRaw: 0,
            IsAttacking: false,
            Name: "R5B fixture",
            Radar: new EnbSceneRadarSnapshot(false, 0, null),
            Spatial: new EnbSceneSpatialSnapshot(
                EnbSceneSpatialProviderKind.Fixed,
                new EnbSceneTransformSnapshot(
                    ClientTime: 100,
                    Position: new Vector3(100, 200, 300),
                    Orientation: Quaternion.Identity,
                    Velocity: Vector3.Zero,
                    Speed: 0,
                    Rotation: Vector3.Zero,
                    OrientationColumn0: Vector3.UnitX),
                Planet: null),
            BaseAssetReference: null,
            ClientBaseAssetReference: null,
            Decals: ImmutableArray<EnbSceneDecalSnapshot>.Empty,
            ColorizationEntries: ImmutableArray<EnbSceneColorizationSnapshot>.Empty,
            RenderStateLinks: ImmutableArray<EnbSceneRenderStateLinkSnapshot>.Empty,
            ActiveRenderStates: ImmutableArray<uint>.Empty,
            RenderStateActions: ImmutableArray<EnbSceneRenderStateActionSnapshot>.Empty,
            Attachments: ImmutableArray<EnbSceneAttachmentSnapshot>.Empty,
            NameDecal: null,
            SubParts: ImmutableArray<EnbSceneSubPartSnapshot>.Empty,
            Avatar: null,
            SpecialTargetEndpointId: 0,
            SpecialTargetTransition: 0);

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "EnbNext.slnx")))
                return current.FullName;
            current = current.Parent;
        }

        throw new Xunit.Sdk.XunitException("Could not locate EnbNext repository root.");
    }
}
