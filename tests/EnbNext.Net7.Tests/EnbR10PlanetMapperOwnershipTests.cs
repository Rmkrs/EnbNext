namespace EnbNext.Net7.Tests;

public sealed class EnbR10PlanetMapperOwnershipTests
{
    [Fact]
    public void MaterializedPlanetOwnsMapperStateBeforeMainViewCulling()
    {
        string renderer = Normalize(Source("src/EnbNext.Engine/EnbWorldRenderer.cs"));

        int ownershipUpdate = renderer.IndexOf(
            "UpdatePlanetMapperStateOwnership(frame, retailAssets, roots)",
            StringComparison.Ordinal);
        int hlodAndFrustumCulling = renderer.IndexOf(
            "LiveHlodSelectionStats hlodStats = ApplyHlodSelections(",
            StringComparison.Ordinal);

        Assert.True(ownershipUpdate >= 0);
        Assert.True(hlodAndFrustumCulling > ownershipUpdate);
        Assert.Contains(
            "IEnumerable<IEnbRenderObject> materializedPlanets = roots",
            renderer,
            StringComparison.Ordinal);
        Assert.Contains(
            "ImmutableHashSet<LivePlanetGpuIdentity> Owned",
            renderer,
            StringComparison.Ordinal);
        Assert.Contains(
            "=> Owned.Contains(new LivePlanetGpuIdentity(obj.RenderIdentity, obj.VisualGeneration))",
            renderer,
            StringComparison.Ordinal);
        Assert.Contains(
            "=> Presented.Contains(new LivePlanetGpuIdentity(obj.RenderIdentity, obj.VisualGeneration))",
            renderer,
            StringComparison.Ordinal);

        // An initially culled planet has no per-draw tracker. Its retained
        // owner state must still be published for the scanner.
        Assert.Contains("if (trackers.Count == 0)", renderer, StringComparison.Ordinal);
        Assert.Contains(
            "ownedPlanetMapperStates,\n                ImmutableHashSet<LivePlanetGpuIdentity>.Empty,",
            renderer,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MapperEpochSurvivesVisibleToCulledGpuRetirementUntilObjectGenerationEnds()
    {
        string renderer = Normalize(Source("src/EnbNext.Engine/EnbWorldRenderer.cs"));
        string ownership = Method(
            renderer,
            "private ImmutableHashSet<LivePlanetGpuIdentity> UpdatePlanetMapperStateOwnership(",
            "private bool TryResolveCurrentRetailPlanetDayNightPhase(");
        string retirement = Method(
            renderer,
            "private void RetireUnusedGpuResources(",
            "private static bool IsFinite(Vector3 value)");

        Assert.Contains(
            "Dictionary<LivePlanetGpuIdentity, LivePlanetMapperStateOwnership> _planetMapperStateOwnership",
            renderer,
            StringComparison.Ordinal);
        Assert.Contains(
            "new LivePlanetMapperStateOwnership(\n                    unchecked((uint)Environment.TickCount))",
            ownership,
            StringComparison.Ordinal);
        Assert.Contains(
            ".Where(identity => !liveIdentities.Contains(identity)).ToArray()",
            ownership,
            StringComparison.Ordinal);
        Assert.DoesNotContain("_planetMapperStateOwnership", retirement, StringComparison.Ordinal);
        string retained=Normalize(Source("src/EnbNext.Engine/EnbWorldRenderer.OwnedPlanetMappers.cs"));
        Assert.Contains("public readonly LivePlanetGpuIdentity PlanetIdentity=identity;",retained,StringComparison.Ordinal);
        Assert.Contains("FrameMapperStartOverride=MapperOverrides.Count!=0&&MapperOverrides.TryGetValue(PlanetIdentity",retained,StringComparison.Ordinal);
        Assert.Contains("FrameDayNightOverride=PhaseOverrides.Count!=0&&PhaseOverrides.TryGetValue(PlanetIdentity",retained,StringComparison.Ordinal);
        Assert.Contains("source.MapperContext=new(source.PlanetIdentity);",retained,StringComparison.Ordinal);
        Assert.Contains("preparedBindings?.MapperOwner is {} mapperSource",renderer,StringComparison.Ordinal);
        Assert.Contains(
            "mapperSource.PlanetMapper?.MapperStartMilliseconds",
            renderer,
            StringComparison.Ordinal);
        Assert.Contains(
            "uint? MapperStartMillisecondsOverride",
            renderer,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ModeZeroPhaseAndScannerOverridesUseExactPlanetGeneration()
    {
        string renderer = Normalize(Source("src/EnbNext.Engine/EnbWorldRenderer.cs"));
        string view = Normalize(Source("src/EnbNext.Engine/EnbWorldController.cs"));
        string ownership = Method(
            renderer,
            "private ImmutableHashSet<LivePlanetGpuIdentity> UpdatePlanetMapperStateOwnership(",
            "private bool TryResolveCurrentRetailPlanetDayNightPhase(");

        Assert.Contains(
            "ImmutableDictionary<LivePlanetGpuIdentity, float> DayNightPhasesByRenderIdentity",
            renderer,
            StringComparison.Ordinal);
        Assert.Contains(
            "ImmutableDictionary<LivePlanetGpuIdentity, uint> MapperStartMillisecondsByRenderIdentity",
            renderer,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (_useMainViewEnvironment && presentationMode == 0)",
            ownership,
            StringComparison.Ordinal);
        Assert.Contains(
            "TryResolveCurrentRetailPlanetDayNightPhase(",
            ownership,
            StringComparison.Ordinal);
        Assert.Contains(
            "source.FrameDayNightOverride.HasValue:frame.DayNightPhaseOverrides.TryGetValue(identity,out inheritedPhase)",
            ownership,
            StringComparison.Ordinal);
        Assert.Contains(
            "source.FrameMapperStartOverride.HasValue:frame.MapperStartMillisecondsOverrides.TryGetValue(identity,out inheritedMapperStartMilliseconds)",
            ownership,
            StringComparison.Ordinal);
        Assert.Contains(
            "if(ownership.RuntimeSemanticsResolved)owned.Add(identity);",
            ownership,
            StringComparison.Ordinal);

        Assert.Contains("planetGpuPresentation.Owns(target)", view, StringComparison.Ordinal);
        Assert.DoesNotContain("planetGpuPresentation.Contains(target)", view, StringComparison.Ordinal);
        Assert.Contains(
            "new LivePlanetGpuIdentity(\n                target.RenderIdentity,\n                target.VisualGeneration)",
            view,
            StringComparison.Ordinal);
        Assert.Contains(
            "dayNightPhaseOverrides: dayNightPhaseOverrides",
            view,
            StringComparison.Ordinal);
        Assert.Contains(
            "mapperStartMillisecondsOverrides: mapperStartMillisecondsOverrides",
            view,
            StringComparison.Ordinal);
        Assert.Contains("snapshot.Environment?.PresentationMode switch", view, StringComparison.Ordinal);
        Assert.Contains("1 or 5 => true", view, StringComparison.Ordinal);
        Assert.Contains("_ => false", view, StringComparison.Ordinal);
    }

    private static string Method(string source, string start, string end)
    {
        int startIndex = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"Missing method start: {start}");
        int endIndex = source.IndexOf(end, startIndex + start.Length, StringComparison.Ordinal);
        Assert.True(endIndex > startIndex, $"Missing method end after {start}: {end}");
        return source[startIndex..endIndex];
    }

    private static string Normalize(string source)
        => source.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string Source(string relative)
        => File.ReadAllText(Path.Combine(FindRepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string FindRepoRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "src", "EnbNext.Net7")) &&
                Directory.Exists(Path.Combine(current.FullName, "tests", "EnbNext.Net7.Tests")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate EnbNext repository root for R10 planet mapper ownership tests.");
    }
}
