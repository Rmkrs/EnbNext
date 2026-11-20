using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR10GutterProjectionTests
{
    [Fact]
    public void FrontCenterRemainsAtProjectedScreenCenter()
    {
        EnbResolvedRenderCamera camera = Camera();

        Assert.True(EnbGutterProjectionRuntime.TryResolve(
            camera, new Vector3(100f, 0f, 0f), 0f, 500f, out EnbGutterProjection projected));
        Assert.False(projected.IsClampedToGutter);
        Assert.Equal(400, projected.LogicalX, 3);
        Assert.Equal(300, projected.LogicalY, 3);
        Assert.Equal(400, projected.ReticleLogicalX, 3);
        Assert.Equal(300, projected.ReticleLogicalY, 3);
        Assert.Equal(400, projected.ObjectLogicalX, 3);
        Assert.Equal(300, projected.ObjectLogicalY, 3);
        Assert.Equal(0, projected.ObjectLogicalRadiusX, 3);
        Assert.Equal(0, projected.ObjectLogicalRadiusY, 3);
        Assert.Equal(0, projected.ReticleRadiusNormalized, 5);
        Assert.Equal(1f, projected.IconScale);
    }

    [Fact]
    public void ObjectProjectionRetainsUnclampedCenterAndAuthoredRadiusForHoverIndicator()
    {
        EnbResolvedRenderCamera camera = Camera();

        Assert.True(EnbGutterProjectionRuntime.TryResolve(
            camera, new Vector3(100f, -1000f, 0f), 25f, 10_000f, out EnbGutterProjection projected));

        Assert.True(projected.IsClampedToGutter);
        Assert.Equal(projected.LogicalX, projected.ReticleLogicalX, 3);
        Assert.Equal(projected.LogicalY, projected.ReticleLogicalY, 3);
        Assert.True(projected.ObjectLogicalX > projected.LogicalX);
        Assert.Equal(300, projected.ObjectLogicalY, 3);
        Assert.True(projected.ObjectLogicalRadiusX > 0);
        Assert.True(projected.ObjectLogicalRadiusY > 0);
        Assert.True(projected.ReticleRadiusNormalized > 0);
    }

    [Fact]
    public void OnscreenIconSeparatesFromRetailResolverCenter()
    {
        EnbResolvedRenderCamera camera = Camera();

        Assert.True(EnbGutterProjectionRuntime.TryResolve(
            camera, new Vector3(100f, -10f, 0f), 25f, 500f, out EnbGutterProjection projected));

        Assert.False(projected.IsClampedToGutter);
        Assert.True(projected.IsInFront);
        Assert.Equal(440, projected.ReticleLogicalX, 3);
        Assert.Equal(300, projected.ReticleLogicalY, 3);
        Assert.Equal(projected.ObjectLogicalX, projected.ReticleLogicalX, 3);
        Assert.Equal(projected.ObjectLogicalY, projected.ReticleLogicalY, 3);
        Assert.Equal(0.125, projected.ObjectRadiusNormalized, 6);
        Assert.Equal(0.107322332, projected.HoverRadiusNormalized, 6);
        Assert.True(projected.LogicalX > projected.ReticleLogicalX);
        Assert.Equal(projected.ReticleLogicalY, projected.LogicalY, 3);
    }

    [Fact]
    public void OffscreenRightIntersectsRightGutter()
    {
        EnbResolvedRenderCamera camera = Camera();

        Assert.True(EnbGutterProjectionRuntime.TryResolve(
            camera, new Vector3(100f, -1000f, 0f), 0f, 10_000f, out EnbGutterProjection projected));
        Assert.True(projected.IsClampedToGutter);
        Assert.Equal(EnbGutterEdge.Right, projected.Edge);
        Assert.Equal(792.5, projected.LogicalX, 3);
        Assert.InRange(projected.LogicalY, 7.5, 592.5);
        Assert.Equal(0.5f, projected.IconScale);
    }

    [Fact]
    public void DirectlyBehindUsesRetailLowerEdgeDegeneracy()
    {
        EnbResolvedRenderCamera camera = Camera();

        Assert.True(EnbGutterProjectionRuntime.TryResolve(
            camera, new Vector3(-100f, 0f, 0f), 0f, 0f, out EnbGutterProjection projected));
        Assert.True(projected.IsClampedToGutter);
        Assert.Equal(EnbGutterEdge.Bottom, projected.Edge);
        Assert.Equal(400, projected.LogicalX, 3);
        Assert.Equal(592.5, projected.LogicalY, 3);
    }

    [Theory]
    [InlineData(0f, 1f)]
    [InlineData(1000f, 1f)]
    [InlineData(3500f, 0.75f)]
    [InlineData(6000f, 0.5f)]
    [InlineData(100000f, 0.5f)]
    public void RetailDistanceScaleUsesRecoveredOptionDefaults(float distance, float expected)
    {
        Assert.Equal(expected, EnbGutterProjectionRuntime.ResolveIconScale(distance), 5);
    }

    [Fact]
    public void LargeBodyUsesCappedFallbackAndUncappedMeshPrefilter()
    {
        EnbResolvedRenderCamera camera = Camera();

        Assert.True(EnbGutterProjectionRuntime.TryResolve(
            camera, new Vector3(100f, 0f, 0f), 80f, 0f, out EnbGutterProjection projected));

        Assert.False(projected.IsClampedToGutter);
        Assert.Equal(400, projected.LogicalX, 3);
        Assert.Equal(300, projected.LogicalY, 3);
        Assert.True(projected.ObjectLogicalRadiusX > 160);
        Assert.True(projected.ObjectLogicalRadiusY > 160);
        Assert.Equal(0.4, projected.ObjectRadiusNormalized, 6);
        Assert.Equal(0.15, projected.HoverRadiusNormalized, 6);

        string overlay = Source("src/EnbNext.Engine/EnbGutterUi.cs");
        Assert.Contains("Math.Min(MaximumReticleRadius, marker.ReticleRadiusNormalized)", overlay, StringComparison.Ordinal);
        Assert.Contains("marker.ObjectRadiusNormalized", overlay, StringComparison.Ordinal);
        Assert.Contains("marker.HoverRadiusNormalized", overlay, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkerExitKeepsSameContactWhenBodyHoverStillResolves()
    {
        string overlay = Source("src/EnbNext.Engine/EnbGutterUi.cs");

        Assert.Contains("internal void OnMarkerExited(Marker marker, Point pointer)", overlay, StringComparison.Ordinal);
        Assert.Contains("if (replacement is null)", overlay, StringComparison.Ordinal);
        Assert.DoesNotContain("replacement is null || ReferenceEquals(replacement, marker)", overlay, StringComparison.Ordinal);
    }

    [Fact]
    public void HoverReticleUsesRetailHud01aGlyphsNotGeneratedLineCorners()
    {
        string overlay = Source("src/EnbNext.Engine/EnbGutterUi.cs");
        string factory = Source("src/EnbNext.Presentation/EnbTargetReticleRasterFactory.cs");

        Assert.Contains("EnbTargetReticleRasterFactory.TryCreate", Source("src/EnbNext.Rendering/EnbGutterResources.cs"), StringComparison.Ordinal);
        Assert.DoesNotContain("DrawCorner", overlay, StringComparison.Ordinal);
        Assert.DoesNotContain("DrawLine", overlay, StringComparison.Ordinal);
        Assert.Contains("\"hud01a.w3d\"", factory, StringComparison.Ordinal);
        Assert.Contains("[\"UL\", \"UR\", \"BR\", \"BL\"]", factory, StringComparison.Ordinal);
        Assert.DoesNotContain("hud04a", factory, StringComparison.OrdinalIgnoreCase);
    }

    // Actual overlay palette, font, distance and label-side behavior is checked
    // in ScannerProbe.ReticleProbe. The former source-string test pinned the
    // removed fixed-color/half-screen implementation instead of that behavior.

    [Fact]
    public void HoverResolutionKeepsPresentedGeometryAndRetailRadiusFallbackWired()
    {
        string overlay = Source("src/EnbNext.Engine/EnbGutterUi.cs");
        string renderer = Source("src/EnbNext.Engine/EnbWorldRenderer.cs");
        string bounds = Source("src/EnbNext.Engine/LiveRenderedObjectBounds.cs");

        Assert.Contains("LiveCompletedRenderQueries", overlay, StringComparison.Ordinal);
        Assert.Contains("IsInsideRenderedObject", overlay, StringComparison.Ordinal);
        Assert.Contains("IsInsideRetailHoverRadius", overlay, StringComparison.Ordinal);
        Assert.Contains("HoverCandidateKind.ProjectedRadius", overlay, StringComparison.Ordinal);
        Assert.Contains("HoverCandidateKind.RenderedObject", overlay, StringComparison.Ordinal);
        Assert.Contains("marker.HoverRadiusNormalized", overlay, StringComparison.Ordinal);
        Assert.Contains("if (!marker.IsInFront)", overlay, StringComparison.Ordinal);
        Assert.Contains("ResolveHoverReticleAnchor", overlay, StringComparison.Ordinal);
        Assert.Contains("new(marker.ReticleX, marker.ReticleY)", overlay, StringComparison.Ordinal);
        Assert.DoesNotContain("IsInsidePickSphere", overlay, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolveHoverObjectRadius", overlay, StringComparison.Ordinal);
        Assert.DoesNotContain("IsInsideProjectedTriangles", overlay, StringComparison.Ordinal);
        Assert.Contains("BuildProjectedObjectBoundsFrame", renderer, StringComparison.Ordinal);
        Assert.Contains("TryCreateProjectedBounds", renderer, StringComparison.Ordinal);
        Assert.Contains("TryComputePresentedMeshSphere", renderer, StringComparison.Ordinal);
        Assert.DoesNotContain("_projectedTriangles", renderer, StringComparison.Ordinal);
        Assert.Contains("CurrentRetailWorldSphere", renderer, StringComparison.Ordinal);
        Assert.Contains("EnbBodyQueryOwner", bounds, StringComparison.Ordinal);
        Assert.Contains("PickSphereCenter", bounds, StringComparison.Ordinal);
        Assert.Contains("PickSphereRadius", bounds, StringComparison.Ordinal);
        Assert.Contains("DistanceRadiiByRenderIdentity", bounds, StringComparison.Ordinal);
        Assert.Contains("BuildCurrentRetailDistanceRadii", renderer, StringComparison.Ordinal);
        //Radius composition is exercised by the retained-Lego GPU/source-pose probes.
        Assert.DoesNotContain("instance.Object.Spatial?.ProviderKind == EnbSceneSpatialProviderKind.Fixed &&", renderer, StringComparison.Ordinal);
    }


    [Fact]
    public void TargetPaneArrowsUseTheCompleteActiveTargetList()
    {
        string overlay = Source("src/EnbNext.Engine/EnbScannerUi.cs");
        string view = Source("src/EnbNext.Engine/EnbWorldController.cs");

        Assert.Contains(
            // 0060E300 (SCAN-D07): scanner arrows are context-filtered unless the right button is held.
            "_radar, current, previous, preserveCurrentCategory);",
            overlay,
            StringComparison.Ordinal);
        Assert.Contains(
            "_radar, current, preserveCurrentCategory: false",
            overlay,
            StringComparison.Ordinal);
        Assert.Contains("uint? current = _target?.GameId ??", overlay, StringComparison.Ordinal);
        Assert.Contains(
            "uint? current = _ui.Scanner.SelectedTarget?.GameId ??",
            view,
            StringComparison.Ordinal);
    }

    [Fact]
    public void FixedNavigationZeroQuaternionUsesTheMainWorldIdentityInterpretation()
    {
        string view = Source("src/EnbNext.Engine/EnbWorldController.cs");
        string gutter = Source("src/EnbNext.Engine/EnbGutterUi.cs");
        string overlay = Source("src/EnbNext.Engine/EnbScannerUi.cs");

        Assert.DoesNotContain(
            "transform.Orientation.LengthSquared() <= 1e-12f",
            view,
            StringComparison.Ordinal);
        Assert.Contains(
            "_ui.Scanner.ApplyLocalTargetSelection(targetId);",
            view,
            StringComparison.Ordinal);
        Assert.Contains(
            "long selectionToken = _ui.Scanner.ApplyLocalTargetSelection(targetId);",
            view,
            StringComparison.Ordinal);
        Assert.Contains("RejectLocalTargetSelection(selectionToken)", view, StringComparison.Ordinal);
        Assert.Contains("_pendingLocalSelectionToken != selectionToken", overlay, StringComparison.Ordinal);
        Assert.Contains("_nextLocalSelectionToken == long.MaxValue", overlay, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Task.Run(() => _targetRequester",
            view,
            StringComparison.Ordinal);
        Assert.Contains(
            "await _targetRequester(target, CancellationToken.None);",
            gutter,
            StringComparison.Ordinal);
        Assert.DoesNotContain("_targetContentReady", overlay, StringComparison.Ordinal);
        Assert.Contains("private bool _targetModelReady", overlay, StringComparison.Ordinal);
        Assert.Contains(
            "EnbSceneTargetVitalsSnapshot? vitals = _target?.TargetVitals;",
            overlay,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "_target is null || _font is null || !_targetModelReady",
            overlay,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "_hasPendingLocalSelection || !_targetModelReady",
            overlay,
            StringComparison.Ordinal);
    }

    private static EnbResolvedRenderCamera Camera()
    {
        Assert.True(EnbRenderViewport.TryFromLogicalSize(800, 600, 1, out EnbRenderViewport viewport));
        Assert.True(EnbResolvedRenderCamera.TryCreate(
            Vector3.Zero,
            Quaternion.Identity,
            viewport,
            Math.PI / 2,
            new EnbRenderClipRange(1, 10_000),
            out EnbResolvedRenderCamera camera));
        return camera;
    }

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

        throw new DirectoryNotFoundException("Could not locate EnbNext repository root for R10 gutter projection tests.");
    }
}
