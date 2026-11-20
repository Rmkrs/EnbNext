using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR3RenderCoreTests
{
    [Fact]
    public void IdentityAndRotatedCameraBasisMatchCanonicalEnbWorldSemantics()
    {
        Assert.True(EnbRenderCameraBasis.TryCreate(Quaternion.Identity, out EnbRenderCameraBasis identity));
        AssertVectorNear(Vector3.UnitX, identity.Forward);
        AssertVectorNear(-Vector3.UnitY, identity.Right);
        AssertVectorNear(Vector3.UnitZ, identity.Up);
        AssertVectorNear(-identity.Forward, Vector3.Cross(identity.Right, identity.Up));

        Quaternion quarterTurn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f);
        Assert.True(EnbRenderCameraBasis.TryCreate(quarterTurn, out EnbRenderCameraBasis rotated));
        AssertVectorNear(Vector3.UnitY, rotated.Forward);
        AssertVectorNear(Vector3.UnitX, rotated.Right);
        AssertVectorNear(Vector3.UnitZ, rotated.Up);
        Assert.InRange(MathF.Abs(Vector3.Dot(rotated.Forward, rotated.Right)), 0f, 1e-5f);
        Assert.InRange(MathF.Abs(Vector3.Dot(rotated.Forward, rotated.Up)), 0f, 1e-5f);
        Assert.InRange(MathF.Abs(Vector3.Dot(rotated.Right, rotated.Up)), 0f, 1e-5f);
    }


    [Fact]
    public void CameraCatalogAcceptsAllEightSequentialStockModeRecordsAndPreservesR3DistanceScale()
    {
        string text = string.Join(
            "\n",
            Enumerable.Range(0, 8).Select(index =>
                $"[Mode{index:00}]\nHeading={index * 10}\nDistance={index + 1}\nTilt={-index}\nFOV={90 - index}\nElevationOffset={index * 2}"));

        EnbRetailCameraModeCatalog catalog = EnbRetailCameraModeCatalog.ParseText(text);
        Assert.Equal(8, catalog.Modes.Count);
        for (int index = 0; index < 8; index++)
        {
            EnbRetailCameraMode mode = catalog.Modes[index];
            Assert.Equal(index, mode.Index);
            Assert.Equal(index + 1f, mode.Distance);
            Assert.Equal((90 - index) * MathF.PI / 180f, mode.HorizontalFieldOfViewRadians, 5);
        }

        Assert.Equal(75f, CameraState().ChaseDistanceScale);
    }



    [Fact]
    public void NormalCameraControllerMatchesRetailFirstThirdPersonAndSlotCyclingStateMachine()
    {
        EnbSceneCameraStateSnapshot cameraState = CameraState() with
        {
            Modes = Enumerable.Range(0, 8)
                .Select(index => new EnbSceneCameraModeSnapshot(
                    index,
                    HeadingRadians: 0f,
                    Distance: index == 0 ? -1f : index,
                    TiltRadians: 0f,
                    HorizontalFieldOfViewRadians: MathF.PI / 2f,
                    ElevationOffsetRadians: 0f))
                .ToImmutableArray(),
        };

        var controller = new EnbNormalCameraController();
        Assert.Equal(1, controller.ActiveModeIndex);
        Assert.Equal(1, controller.RememberedThirdPersonModeIndex);
        Assert.False(controller.IsFirstPerson);

        Assert.True(controller.SelectNextThirdPersonMode(cameraState));
        Assert.Equal(2, controller.ActiveModeIndex);
        Assert.True(controller.SelectPreviousThirdPersonMode(cameraState));
        Assert.Equal(1, controller.ActiveModeIndex);
        Assert.True(controller.SelectPreviousThirdPersonMode(cameraState));
        Assert.Equal(7, controller.ActiveModeIndex);
        Assert.Equal(7, controller.RememberedThirdPersonModeIndex);
        Assert.True(controller.SelectNextThirdPersonMode(cameraState));
        Assert.Equal(1, controller.ActiveModeIndex);

        Assert.True(controller.SelectThirdPersonMode(cameraState, 5));
        Assert.Equal(5, controller.RememberedThirdPersonModeIndex);
        Assert.True(controller.SelectFirstPerson(cameraState));
        Assert.True(controller.IsFirstPerson);
        Assert.Equal(0, controller.ActiveModeIndex);
        Assert.Equal(5, controller.RememberedThirdPersonModeIndex);

        // Retail's Next/Prev camera actions do not cycle third-person slots while
        // mode 0 is active; their first-person-only flag is separate from pose.
        Assert.False(controller.SelectNextThirdPersonMode(cameraState));
        Assert.False(controller.SelectPreviousThirdPersonMode(cameraState));
        Assert.Equal(0, controller.ActiveModeIndex);

        Assert.True(controller.RestoreDefaultThirdPerson(cameraState));
        Assert.Equal(5, controller.ActiveModeIndex);
        Assert.False(controller.IsFirstPerson);

        var firstPersonStartup = new EnbNormalCameraController(startInFirstPerson: true);
        Assert.Equal(0, firstPersonStartup.ActiveModeIndex);
        Assert.Equal(1, firstPersonStartup.RememberedThirdPersonModeIndex);
        Assert.True(firstPersonStartup.RestoreDefaultThirdPerson(cameraState));
        Assert.Equal(1, firstPersonStartup.ActiveModeIndex);

        EnbNormalCameraController persistedFirstPerson = EnbNormalCameraController.FromRetailStartup(
            cameraState with { StartInFirstPerson = true });
        Assert.Equal(0, persistedFirstPerson.ActiveModeIndex);
        Assert.Equal(1, persistedFirstPerson.RememberedThirdPersonModeIndex);
    }

    [Fact]
    public void StaticNormalCameraPoseUsesCanonicalRetailHeadingTiltDistanceAndElevationOrder()
    {
        EnbSceneTransformSnapshot anchor = AnchorTransform(Vector3.Zero, Quaternion.Identity);

        var straightBehind = new EnbChaseCameraModeGeometry(
            ModeIndex: 1,
            HeadingRadians: 0f,
            ScaledDistance: 100f,
            TiltRadians: 0f,
            HorizontalFieldOfViewRadians: MathF.PI / 2f,
            ElevationOffsetRadians: 0f);
        Assert.True(EnbNormalCameraController.TryResolveStaticNormalPose(
            anchor,
            straightBehind,
            out Vector3 straightPosition,
            out Quaternion straightOrientation));
        AssertVectorNear(new Vector3(-100f, 0f, 0f), straightPosition);
        Assert.True(EnbRenderCameraBasis.TryCreate(straightOrientation, out EnbRenderCameraBasis straightBasis));
        AssertVectorNear(Vector3.UnitX, straightBasis.Forward);
        AssertVectorNear(Vector3.UnitZ, straightBasis.Up);

        var negativeDistance = straightBehind with { ModeIndex = 0, ScaledDistance = -75f };
        Assert.True(EnbNormalCameraController.TryResolveStaticNormalPose(
            anchor,
            negativeDistance,
            out Vector3 firstPersonPosition,
            out _));
        AssertVectorNear(new Vector3(75f, 0f, 0f), firstPersonPosition);

        var heading = straightBehind with { HeadingRadians = MathF.PI / 2f };
        Assert.True(EnbNormalCameraController.TryResolveStaticNormalPose(
            anchor,
            heading,
            out Vector3 headingPosition,
            out Quaternion headingOrientation));
        AssertVectorNear(new Vector3(0f, -100f, 0f), headingPosition);
        Assert.True(EnbRenderCameraBasis.TryCreate(headingOrientation, out EnbRenderCameraBasis headingBasis));
        AssertVectorNear(Vector3.UnitY, headingBasis.Forward);

        var tilted = straightBehind with
        {
            TiltRadians = MathF.PI / 6f,
            ElevationOffsetRadians = MathF.PI / 9f,
        };
        Assert.True(EnbNormalCameraController.TryResolveStaticNormalPose(
            anchor,
            tilted,
            out Vector3 tiltedPosition,
            out Quaternion tiltedOrientation));

        // Positive retail Tilt places a positive-distance chase camera above the
        // anchor. ElevationOffset is applied only after translation, so it
        // changes final look direction without changing that position.
        AssertVectorNear(new Vector3(-86.60254f, 0f, 50f), tiltedPosition);
        Assert.True(EnbRenderCameraBasis.TryCreate(tiltedOrientation, out EnbRenderCameraBasis tiltedBasis));
        AssertVectorNear(
            new Vector3(MathF.Cos(MathF.PI / 18f), 0f, -MathF.Sin(MathF.PI / 18f)),
            tiltedBasis.Forward);
    }

    [Fact]
    public void DynamicBankTargetUsesNormalizedAngularZScaleClampAndPlayerBranch()
    {
        EnbSceneTransformSnapshot moderate = AnchorTransform(
            Vector3.Zero,
            Quaternion.Identity,
            velocity: new Vector3(99f, 88f, 77f), rotation: new Vector3(0f, 0f, 0.001f));

        Assert.True(EnbNormalCameraController.TryResolveNormalPlayerBankTarget(
            moderate,
            EnbNormalCameraController.ActivePlayerObjectType,
            out float target));
        Assert.Equal(-0.125f, target, 6);

        EnbSceneTransformSnapshot saturatedPositive = AnchorTransform(
            Vector3.Zero,
            Quaternion.Identity,
            velocity: Vector3.Zero, rotation: new Vector3(0f, 0f, 1f));
        Assert.True(EnbNormalCameraController.TryResolveNormalPlayerBankTarget(
            saturatedPositive,
            EnbNormalCameraController.ActivePlayerObjectType,
            out target));
        Assert.Equal(
            -EnbNormalCameraController.RetailBankClampRadians *
            EnbNormalCameraController.RetailBankTargetMultiplier,
            target,
            6);

        EnbSceneTransformSnapshot saturatedNegative = AnchorTransform(
            Vector3.Zero,
            Quaternion.Identity,
            velocity: Vector3.Zero, rotation: new Vector3(0f, 0f, -1f));
        Assert.True(EnbNormalCameraController.TryResolveNormalPlayerBankTarget(
            saturatedNegative,
            EnbNormalCameraController.ActivePlayerObjectType,
            out target));
        Assert.Equal(
            EnbNormalCameraController.RetailBankClampRadians *
            EnbNormalCameraController.RetailBankTargetMultiplier,
            target,
            6);

        // The current-retail alternate branch for non-active-player objects has
        // an extra heading condition. This deliberately does not approximate it.
        Assert.True(EnbNormalCameraController.TryResolveNormalPlayerBankTarget(
            moderate,
            0,
            out target));
        Assert.Equal(0f, target);

        EnbSceneTransformSnapshot malformed = moderate with
        {
            Rotation = new Vector3(0f, 0f, float.NaN),
        };
        Assert.False(EnbNormalCameraController.TryResolveNormalPlayerBankTarget(
            malformed,
            EnbNormalCameraController.ActivePlayerObjectType,
            out _));
    }

    [Fact]
    public void DynamicBankSmootherUsesLinearFrameElapsedTimeAndPrecedesStaticChaseComposition()
    {
        EnbSceneCameraStateSnapshot cameraState = CameraState() with
        {
            Modes = ImmutableArray.Create(
                new EnbSceneCameraModeSnapshot(0, 0f, -1f, 0f, MathF.PI / 2f, 0f),
                new EnbSceneCameraModeSnapshot(1, 0f, 2f, 0f, MathF.PI / 2f, 0f)),
        };
        EnbSceneTransformSnapshot anchor = AnchorTransform(
            Vector3.Zero,
            Quaternion.Identity,
            velocity: Vector3.Zero, rotation: new Vector3(0f, 0f, .001f));
        Assert.True(EnbRenderViewport.TryFromLogicalSize(800, 600, 1.0, out EnbRenderViewport viewport));

        var controller = new EnbNormalCameraController();
        Assert.True(controller.TryResolveFrame(
            cameraState,
            anchor,
            EnbNormalCameraController.ActivePlayerObjectType,
            0,
            viewport,
            out EnbNormalCameraFrame frame));
        Assert.Equal(0f, frame.BankRadians);
        Assert.Equal(-0.125f, frame.BankTargetRadians, 6);

        Assert.True(controller.TryResolveFrame(
            cameraState,
            anchor,
            EnbNormalCameraController.ActivePlayerObjectType,
            50,
            viewport,
            out frame));
        Assert.Equal(-0.05f, frame.BankRadians, 6);
        Assert.Equal(-0.125f, frame.BankTargetRadians, 6);
        AssertVectorNear(new Vector3(-150f, 0f, 0f), frame.Camera.Position);
        AssertQuaternionEquivalent(
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.005f), // desired bank -.05, camera follow alpha .1
            frame.Camera.WorldOrientation);

        Assert.True(controller.TryResolveFrame(
            cameraState,
            anchor,
            EnbNormalCameraController.ActivePlayerObjectType,
            50,
            viewport,
            out frame));
        Assert.Equal(-0.1f, frame.BankRadians, 6);

        // The next 50 ms step would overshoot, so retail clamps exactly onto
        // the target instead of crossing it.
        Assert.True(controller.TryResolveFrame(
            cameraState,
            anchor,
            EnbNormalCameraController.ActivePlayerObjectType,
            50,
            viewport,
            out frame));
        Assert.Equal(-0.125f, frame.BankRadians, 6);
        Assert.Equal(frame.BankTargetRadians, frame.BankRadians);

        // Bank is presentation-frame state: the same immutable network sample
        // continues to converge when another render frame elapses.
        Assert.False(controller.TryResolveFrame(
            cameraState,
            anchor,
            EnbNormalCameraController.ActivePlayerObjectType,
            double.NaN,
            viewport,
            out _));
    }

    [Fact]
    public void NormalCameraFrameUsesRetailModeOneByDefaultAndSelectedModeProjection()
    {
        EnbSceneCameraStateSnapshot cameraState = CameraState() with
        {
            Modes = ImmutableArray.Create(
                new EnbSceneCameraModeSnapshot(0, 0f, -1f, 0f, 1.1f, 0f),
                new EnbSceneCameraModeSnapshot(1, 0f, 2f, 0f, 1.2f, 0f),
                new EnbSceneCameraModeSnapshot(2, MathF.PI / 2f, 3f, 0f, 1.3f, 0f)),
        };
        Assert.True(EnbRenderViewport.TryFromLogicalSize(1600, 900, 1.0, out EnbRenderViewport viewport));

        var controller = new EnbNormalCameraController();
        Assert.True(controller.TryResolveFrame(
            cameraState,
            AnchorTransform(new Vector3(10f, 20f, 30f), Quaternion.Identity),
            EnbNormalCameraController.ActivePlayerObjectType,
            0,
            viewport,
            out EnbNormalCameraFrame frame));

        Assert.Equal(1, frame.ActiveModeIndex);
        Assert.Equal(150f, frame.Mode.ScaledDistance);
        Assert.Equal(1.2, frame.Camera.Projection.HorizontalFieldOfViewRadians, 6);
        Assert.Equal(16.0 / 9.0, viewport.AspectRatio, 12);
        Assert.Equal(EnbNormalCameraController.CurrentRetailGameplayProjectionAspectRatio, frame.Camera.Projection.AspectRatio, 12);
        Assert.Equal(
            2.0 * Math.Atan(Math.Tan(frame.Camera.Projection.HorizontalFieldOfViewRadians / 2.0) / EnbNormalCameraController.CurrentRetailGameplayProjectionAspectRatio),
            frame.Camera.Projection.VerticalFieldOfViewRadians,
            12);
        AssertVectorNear(new Vector3(-140f, 20f, 30f), frame.Camera.Position);

        Assert.True(controller.SelectNextThirdPersonMode(cameraState));
        Assert.True(controller.TryResolveFrame(
            cameraState,
            AnchorTransform(new Vector3(10f, 20f, 30f), Quaternion.Identity),
            EnbNormalCameraController.ActivePlayerObjectType,
            0,
            viewport,
            out frame));
        Assert.Equal(2, frame.ActiveModeIndex);
        Assert.Equal(1.3, frame.Camera.Projection.HorizontalFieldOfViewRadians, 6);
        //Preset changes retain the previous pose; zero elapsed time cannot
        //advance799610 even though the selected projection changes now.
        AssertVectorNear(new Vector3(-140f, 20f, 30f), frame.Camera.Position);
        Assert.True(controller.TryResolveFrame(cameraState,
            AnchorTransform(new Vector3(10f,20f,30f),Quaternion.Identity),
            EnbNormalCameraController.ActivePlayerObjectType,1000,viewport,out frame));
        AssertVectorNear(new Vector3(10f, -205f, 30f), frame.Camera.Position);
    }

    [Fact]
    public void ResolvedCameraOwnsDirectionRaySphereAndPhysicalToLogicalProjectionMath()
    {
        Assert.True(EnbRenderViewport.TryFromLogicalSize(800, 600, 2.0, out EnbRenderViewport viewport));
        Assert.True(EnbResolvedRenderCamera.TryCreate(
            Vector3.Zero,
            Quaternion.Identity,
            viewport,
            Math.PI / 2.0,
            new EnbRenderClipRange(6f, 600000f),
            out EnbResolvedRenderCamera camera));

        Assert.True(camera.TryProjectWorldDirection(Vector3.UnitX, out EnbRenderProjectionResult forward));
        Assert.Equal(800.0, forward.PixelX, 8);
        Assert.Equal(600.0, forward.PixelY, 8);
        Assert.Equal(400.0, viewport.ToLogicalX(forward.PixelX), 8);
        Assert.Equal(300.0, viewport.ToLogicalY(forward.PixelY), 8);

        Assert.True(camera.TryGetWorldRayDirectionFromNdc(0, 0, out Vector3 centerRay));
        AssertVectorNear(Vector3.UnitX, centerRay);
        Assert.True(camera.TryGetWorldRayDirectionFromNdc(0.5, 0, out Vector3 rightRay));
        Assert.True(Vector3.Dot(rightRay, -Vector3.UnitY) > 0f);

        Assert.True(camera.TryProjectWorldSphere(
            new Vector3(100f, 0f, 0f),
            10f,
            out EnbRenderSphereProjectionResult sphere));
        Assert.Equal(800.0, sphere.PixelX, 8);
        Assert.Equal(600.0, sphere.PixelY, 8);
        Assert.Equal(80.0, sphere.PixelRadiusX, 5);
        //9447A0 float bounds and944EB0/945A60 PC24 give M22=3FAAAAA9.
        Assert.Equal(79.999988079071045, sphere.PixelRadiusY, 8);
    }


    [Fact]
    public void SphereProjectionUsesIndependentProjectionAxesWhenRetailAspectDiffersFromPhysicalViewport()
    {
        Assert.True(EnbRenderViewport.TryFromLogicalSize(1600, 900, 1.0, out EnbRenderViewport viewport));
        Assert.True(EnbResolvedRenderCamera.TryCreate(
            Vector3.Zero,
            Quaternion.Identity,
            viewport,
            Math.PI / 2.0,
            EnbNormalCameraController.CurrentRetailGameplayProjectionAspectRatio,
            new EnbRenderClipRange(6f, 600000f),
            out EnbResolvedRenderCamera camera));

        Assert.True(camera.TryProjectWorldSphere(
            new Vector3(100f, 0f, 0f),
            10f,
            out EnbRenderSphereProjectionResult sphere));

        Assert.Equal(800.0, sphere.PixelX, 8);
        Assert.Equal(450.0, sphere.PixelY, 8);
        Assert.Equal(80.0, sphere.PixelRadiusX, 5);
        Assert.Equal(59.999991059303284, sphere.PixelRadiusY, 8);
    }

    [Fact]
    public void R3CViewportUsesActiveCameraControllerAndPrototypeCameraImplementationIsGone()
    {
        string project = Source("src/EnbNext.App/EnbNext.App.csproj");
        string view = Source("src/EnbNext.Engine/EnbWorldController.cs");

        Assert.Contains("EnbNext.Rendering", project, StringComparison.Ordinal);
        Assert.Contains("EnbResolvedRenderCamera", view, StringComparison.Ordinal);
        Assert.Contains("EnbRenderViewport.TryFromLogicalSize", view, StringComparison.Ordinal);
        Assert.Contains("EnbNormalCameraController", view, StringComparison.Ordinal);
        Assert.Contains("TryResolveFrame", view, StringComparison.Ordinal);
        Assert.Contains("Stopwatch.GetTimestamp", view, StringComparison.Ordinal);
        Assert.Contains("cameraFrameElapsedMilliseconds", view, StringComparison.Ordinal);
        Assert.Contains("localObject.ObjectType", view, StringComparison.Ordinal);
        Assert.DoesNotContain("EnbRenderCameraIntentResolver", view, StringComparison.Ordinal);
        Assert.DoesNotContain("HorizontalFieldOfViewDegrees = 119", view, StringComparison.Ordinal);
        Assert.DoesNotContain("LiveWorldCamera", view, StringComparison.Ordinal);
        Assert.DoesNotContain("Vector3.Transform(-Vector3.UnitZ", view, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(FindRepoRoot(), "src", "EnbNext.App", "LiveWorldCamera.cs")));
    }

    [Fact]
    public void HorizontalFovProjectionMatchesRetailFourByThreeFixtureAndCanonicalDepth()
    {
        var viewport = new EnbRenderViewport(800, 600, 1.0);
        var clip = new EnbRenderClipRange(6f, 600000f);

        Assert.True(EnbPerspectiveProjection.TryCreateHorizontalFov(
            viewport,
            Math.PI / 2.0,
            clip,
            out EnbPerspectiveProjection projection));

        Assert.Equal(4.0 / 3.0, projection.AspectRatio, 12);
        Assert.Equal(73.739795, Degrees(projection.VerticalFieldOfViewRadians), 5);
        Assert.Equal(1.0f, projection.Matrix.M11, 5);
        Assert.Equal(4.0f / 3.0f, projection.Matrix.M22, 5);

        Assert.True(projection.TryProjectViewPoint(new Vector3(0f, 0f, -clip.NearPlane), out Vector3 near));
        Assert.True(projection.TryProjectViewPoint(new Vector3(0f, 0f, -clip.FarPlane), out Vector3 far));
        Assert.InRange(MathF.Abs(near.Z), 0f, 1e-5f);
        Assert.InRange(MathF.Abs(far.Z - 1f), 0f, 1e-5f);
    }

    [Fact]
    public void OrthographicProjectionPreservesBoundsAndCanonicalDepth()
    {
        var viewport = new EnbRenderViewport(256, 128, 1.0);
        var clip = new EnbRenderClipRange(2f, 10f);

        Assert.True(EnbResolvedRenderCamera.TryCreateOrthographic(
            Vector3.Zero,
            Quaternion.Identity,
            viewport,
            halfWidth: 4,
            halfHeight: 4,
            clip,
            out EnbResolvedRenderCamera camera));

        Assert.True(camera.Projection.IsOrthographic);
        Assert.Equal(4, camera.Projection.OrthographicHalfWidth, 6);
        Assert.Equal(4, camera.Projection.OrthographicHalfHeight, 6);
        Assert.True(camera.Projection.TryProjectViewPoint(new Vector3(4f, 4f, -clip.NearPlane), out Vector3 near));
        Assert.True(camera.Projection.TryProjectViewPoint(new Vector3(-4f, -4f, -clip.FarPlane), out Vector3 far));
        Assert.InRange(Vector3.Distance(new Vector3(1f, 1f, 0f), near), 0f, 1e-5f);
        Assert.InRange(Vector3.Distance(new Vector3(-1f, -1f, 1f), far), 0f, 1e-5f);

        Assert.True(camera.TryProjectWorldSphere(new Vector3(6f, 0f, 0f), 2f, out EnbRenderSphereProjectionResult sphere));
        Assert.Equal(128.0, sphere.PixelX, 8);
        Assert.Equal(64.0, sphere.PixelY, 8);
        Assert.Equal(64.0, sphere.PixelRadiusX, 8);
        Assert.Equal(32.0, sphere.PixelRadiusY, 8);
    }


    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(2520, 1080)]
    [InlineData(1080, 1920)]
    public void ModernProjectionDerivesVerticalFovFromPhysicalViewportAspect(int width, int height)
    {
        var viewport = new EnbRenderViewport(width, height, 1.0);
        Assert.True(EnbPerspectiveProjection.TryCreateHorizontalFov(
            viewport,
            Math.PI / 2.0,
            new EnbRenderClipRange(6f, 600000f),
            out EnbPerspectiveProjection projection));

        double expected = 2.0 * Math.Atan(Math.Tan(Math.PI / 4.0) / ((double)width / height));
        Assert.Equal(expected, projection.VerticalFieldOfViewRadians, 12);
        Assert.Equal((double)width / height, projection.AspectRatio, 12);
    }

    [Fact]
    public void LogicalViewportConversionUsesPhysicalPixelsAndIsStableAcrossEquivalentDpiLayouts()
    {
        Assert.True(EnbRenderViewport.TryFromLogicalSize(960, 540, 2.0, out EnbRenderViewport highDpi));
        Assert.True(EnbRenderViewport.TryFromLogicalSize(1280, 720, 1.5, out EnbRenderViewport resized));

        Assert.Equal(1920, highDpi.PixelWidth);
        Assert.Equal(1080, highDpi.PixelHeight);
        Assert.Equal(1920, resized.PixelWidth);
        Assert.Equal(1080, resized.PixelHeight);
        Assert.Equal(highDpi.AspectRatio, resized.AspectRatio, 12);

        Assert.False(EnbRenderViewport.TryFromLogicalSize(0, 540, 2.0, out _));
        Assert.False(EnbRenderViewport.TryFromLogicalSize(960, 540, double.NaN, out _));
    }


    [Fact]
    public void SpacePassGraphPreservesSwapBackdropOrderingAndEveryRecoveredClipFamily()
    {
        EnbSceneCameraStateSnapshot camera = CameraState();
        EnbRenderPassGraph normal = EnbRenderPassGraph.CreateSpaceSkeleton(camera, swapBackdrop: false);
        EnbRenderPassGraph swapped = EnbRenderPassGraph.CreateSpaceSkeleton(camera, swapBackdrop: true);

        Assert.True(IndexOf(normal, EnbRenderPassKind.SharedScene) < IndexOf(normal, EnbRenderPassKind.SharedWorld));
        Assert.True(IndexOf(swapped, EnbRenderPassKind.SharedWorld) < IndexOf(swapped, EnbRenderPassKind.SharedScene));

        foreach (EnbRenderPassGraph graph in new[] { normal, swapped })
        {
            Assert.Equal(new EnbRenderClipRange(20f, 100000f), graph.Passes
                .Single(static pass => pass.Kind == EnbRenderPassKind.SharedScene).ClipRange);
            Assert.Equal(new EnbRenderClipRange(6f, 600000f), graph.Passes
                .Single(static pass => pass.Kind == EnbRenderPassKind.OrdinaryWorld).ClipRange);
        }

        EnbRenderPassDescriptor[] specialized = normal.Passes
            .Where(static pass => pass.Kind == EnbRenderPassKind.SpecializedDepthRange)
            .ToArray();
        Assert.Equal(2, specialized.Length);
        Assert.True(specialized[0].ClipRange.HasValue);
        Assert.Equal(new EnbRenderClipRange(20f, 100000f), specialized[0].ClipRange.GetValueOrDefault());
        Assert.Equal(0, specialized[0].SpecializedDepthRangeIndex);
        Assert.True(specialized[1].ClipRange.HasValue);
        Assert.Equal(new EnbRenderClipRange(50f, 150f), specialized[1].ClipRange.GetValueOrDefault());
        Assert.Equal(1, specialized[1].SpecializedDepthRangeIndex);

        EnbRenderClipRange normalClip = new(6f, 600000f);
        EnbRenderClipRange? ordinaryClip = normal.Passes
            .Single(static pass => pass.Kind == EnbRenderPassKind.OrdinaryWorld)
            .ClipRange;
        Assert.True(ordinaryClip.HasValue);
        Assert.Equal(normalClip, ordinaryClip.Value);
        Assert.Equal(EnbRenderPassKind.FrameClear, normal.Passes[0].Kind);
        Assert.Equal(EnbRenderPassKind.Present, normal.Passes[^1].Kind);
    }

    [Fact]
    public void RenderingProjectStaysGpuUiAndNet7IndependentAtTheCoreBoundary()
    {
        string project = Source("src/EnbNext.Rendering/EnbNext.Rendering.csproj");
        string camera = Source("src/EnbNext.Rendering/EnbRenderCamera.cs");
        string passGraph = Source("src/EnbNext.Rendering/EnbRenderPassGraph.cs");
        string intentResolver = Source("src/EnbNext.Rendering/EnbRenderCameraIntentResolver.cs");
        string normalController = Source("src/EnbNext.Rendering/EnbNormalCameraController.cs");
        string liveWorldView = Source("src/EnbNext.Engine/EnbWorldController.cs");

        string dependencies = string.Join("\n", System.Xml.Linq.XDocument.Parse(project).Descendants()
            .Where(item => item.Name.LocalName is "ProjectReference" or "PackageReference" or "Reference")
            .Select(item => (string?)item.Attribute("Include")));
        Assert.Contains("EnbNext.Presentation", dependencies, StringComparison.Ordinal);
        foreach (string forbidden in new[]
        {
            "Avalonia",
            "EnbNext.Net7",
            "Veldrid",
            "Silk.NET",
            "Direct3D",
            "OpenGL",
            "Vulkan",
            "Metal",
        })
        {
            Assert.DoesNotContain(forbidden, dependencies, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(forbidden, camera, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(forbidden, passGraph, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(forbidden, intentResolver, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(forbidden, normalController, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain("EnbSceneHudSnapshot", camera, StringComparison.Ordinal);
        // Bank-source behavior is covered numerically above, not by pinning
        // the old (and now disproven) velocity expression into source text.
        Assert.Contains("_cameraController.TryResolveFrame", liveWorldView, StringComparison.Ordinal);
        Assert.DoesNotContain("TryResolveModeIndependentHorizontalFov", liveWorldView, StringComparison.Ordinal);
        Assert.DoesNotContain("anchor-pose preview", liveWorldView, StringComparison.Ordinal);
    }

    private static EnbSceneTransformSnapshot AnchorTransform(Vector3 position, Quaternion orientation)
        => AnchorTransform(position, orientation, Vector3.Zero);

    private static EnbSceneTransformSnapshot AnchorTransform(
        Vector3 position,
        Quaternion orientation,
        Vector3 velocity, Vector3 rotation = default)
        => new(
            ClientTime: 1234,
            Position: position,
            Orientation: orientation,
            Velocity: velocity,
            Speed: 0f,
            Rotation: rotation,
            OrientationColumn0: Vector3.Transform(Vector3.UnitX, orientation));

    private static EnbSceneCameraStateSnapshot CameraState()
        => new(
            AnchorRenderIdentity: null,
            AnchorGameId: null,
            UsesSessionOverride: false,
            SessionOverrideSelector: null,
            ModeSourceProvenance: null,
            PlayerOptionsSelector: null,
            PlayerOptionsSourceProvenance: null,
            StartInFirstPerson: false,
            Modes: ImmutableArray<EnbSceneCameraModeSnapshot>.Empty,
            ChaseDistanceScale: 75f,
            NormalClipRange: new EnbSceneClipRangeSnapshot(6f, 600000f),
            TemporaryPassClipRanges: ImmutableArray.Create(
                new EnbSceneClipRangeSnapshot(20f, 100000f),
                new EnbSceneClipRangeSnapshot(50f, 150f)));

    private static int IndexOf(EnbRenderPassGraph graph, EnbRenderPassKind kind)
    {
        for (int i = 0; i < graph.Passes.Length; i++)
        {
            if (graph.Passes[i].Kind == kind)
                return i;
        }

        return -1;
    }

    private static double Degrees(double radians) => radians * 180.0 / Math.PI;

    private static void AssertVectorNear(Vector3 expected, Vector3 actual)
    {
        Assert.InRange(MathF.Abs(expected.X - actual.X), 0f, 1e-5f);
        Assert.InRange(MathF.Abs(expected.Y - actual.Y), 0f, 1e-5f);
        Assert.InRange(MathF.Abs(expected.Z - actual.Z), 0f, 1e-5f);
    }

    private static void AssertQuaternionEquivalent(Quaternion expected, Quaternion actual)
    {
        Quaternion expectedNormalized = Quaternion.Normalize(expected);
        Quaternion actualNormalized = Quaternion.Normalize(actual);
        float alignment = MathF.Abs(Quaternion.Dot(expectedNormalized, actualNormalized));
        Assert.InRange(alignment, 1f - 1e-5f, 1f);
    }

    private static string Source(string relativePath)
    {
        string repo = FindRepoRoot();
        return File.ReadAllText(Path.Combine(repo, relativePath.Replace('/', Path.DirectorySeparatorChar)))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "EnbNext.slnx")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the EnbNext repository root.");
    }
}
