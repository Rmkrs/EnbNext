using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR8PlanetProductionGpuTests
{





    [Fact]
    public void PltDnLinearMapperIsFrozenAtLivePhaseWithoutChangingAuthoredScale()
    {
        EnbW3dMapperUniforms authoredUniforms = new(
            Scale: new Vector2(6f, 2f),
            ScrollPerMillisecond: new Vector2(0.0001f, -0.0002f),
            Offset: new Vector2(0.25f, 0.5f),
            ClampFix: false,
            HasClosedBaseParameters: true);
        EnbW3dMapperRuntimeProgram authored = EnbW3dMapperRuntimeProgram.Disabled with
        {
            Kind = EnbW3dMapperRuntimeProgramKind.Linear,
            BaseUniforms = authoredUniforms,
        };
        var texture = new EnbW3dRenderTextureBinding(1, "PLT_DN01.TGA", null);

        bool ok = EnbCurrentRetailPlanetRuntime.TryEvaluateDayNightMapperOverride(
            texture,
            authored,
            0.958635f,
            out EnbW3dMapperRuntimeEvaluation evaluation,
            out bool applied,
            out string? diagnostic);

        Assert.True(ok, diagnostic);
        Assert.True(applied);
        Assert.Equal(authoredUniforms.Scale, evaluation.TextureScale);
        Assert.Equal(Vector2.Zero, evaluation.TextureCross);
        Assert.Equal(new Vector2(0.958635f, 0f), evaluation.TextureOffset);
        Assert.False(evaluation.ConfiguresBumpEnvironment);
    }

    [Fact]
    public void NonPltDnOrNonLinearMapperRemainsAuthored()
    {
        EnbW3dMapperRuntimeProgram linear = EnbW3dMapperRuntimeProgram.Disabled with
        {
            Kind = EnbW3dMapperRuntimeProgramKind.Linear,
        };
        EnbW3dMapperRuntimeProgram nonLinear = EnbW3dMapperRuntimeProgram.Disabled with
        {
            Kind = EnbW3dMapperRuntimeProgramKind.StaticScale,
        };

        Assert.True(EnbCurrentRetailPlanetRuntime.TryEvaluateDayNightMapperOverride(
            new EnbW3dRenderTextureBinding(1, "PLT0401.TGA", null),
            linear,
            0.5f,
            out _,
            out bool nonDnApplied,
            out string? nonDnDiagnostic), nonDnDiagnostic);
        Assert.False(nonDnApplied);

        Assert.True(EnbCurrentRetailPlanetRuntime.TryEvaluateDayNightMapperOverride(
            new EnbW3dRenderTextureBinding(1, "PLT_DN06.TGA", null),
            nonLinear,
            0.5f,
            out _,
            out bool nonLinearApplied,
            out string? nonLinearDiagnostic), nonLinearDiagnostic);
        Assert.False(nonLinearApplied);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void NonFiniteLivePhaseFailsClosed(float phase)
    {
        EnbW3dMapperRuntimeProgram linear = EnbW3dMapperRuntimeProgram.Disabled with
        {
            Kind = EnbW3dMapperRuntimeProgramKind.Linear,
        };

        bool ok = EnbCurrentRetailPlanetRuntime.TryEvaluateDayNightMapperOverride(
            new EnbW3dRenderTextureBinding(1, "PLT_DN01.TGA", null),
            linear,
            phase,
            out _,
            out bool applied,
            out string? diagnostic);

        Assert.False(ok);
        Assert.False(applied);
        Assert.Contains("non-finite", diagnostic, StringComparison.OrdinalIgnoreCase);
    }


    [Theory]
    [InlineData(0x00000000u, EnbCurrentRetailW3dGeometryClass.Normal)]
    [InlineData(0x00010000u, EnbCurrentRetailW3dGeometryClass.CameraAligned)]
    [InlineData(0x00020000u, EnbCurrentRetailW3dGeometryClass.Skin)]
    [InlineData(0x00060000u, EnbCurrentRetailW3dGeometryClass.CameraOriented)]
    [InlineData(0x00030000u, EnbCurrentRetailW3dGeometryClass.Unsupported)]
    [InlineData(0xA0010000u, EnbCurrentRetailW3dGeometryClass.CameraAligned)]
    public void MeshHeaderGeometrySelectorMapsToCurrentRetailRuntimeClass(
        uint attributes,
        EnbCurrentRetailW3dGeometryClass expected)
    {
        Assert.Equal(expected, EnbCurrentRetailCameraGeometryRuntime.Classify(attributes));
    }

    [Fact]
    public void CameraAlignedPresentationDiscardsPacketBasisAndReappliesExplicitMeshModelScale()
    {
        EnbResolvedRenderCamera camera = CreateCamera(
            position: new Vector3(100f, 200f, 300f),
            orientation: Quaternion.Identity);
        Matrix4x4 authoredWorld =
            Matrix4x4.CreateScale(2f) *
            Matrix4x4.CreateFromYawPitchRoll(0.7f, -0.3f, 1.1f) *
            Matrix4x4.CreateTranslation(10f, 20f, 30f);

        bool ok = EnbCurrentRetailCameraGeometryRuntime.TryResolvePresentationWorldTransform(
            EnbCurrentRetailCameraGeometryRuntime.CameraAlignedGeometry,
            "aligned",
            EnbW3dSubmissionVertexSource.RigidMesh,
            hasDeformedGeometry: false,
            currentRetailMeshModelScale: 2f,
            authoredWorld,
            camera,
            out Matrix4x4 presented,
            out EnbCurrentRetailW3dGeometryClass geometryClass,
            out string? diagnostic);

        Assert.True(ok, diagnostic);
        Assert.Equal(EnbCurrentRetailW3dGeometryClass.CameraAligned, geometryClass);
        AssertVectorClose(new Vector3(10f, 20f, 30f), new Vector3(presented.M41, presented.M42, presented.M43));
        AssertVectorClose(-camera.Basis.Forward, NormalizeRow1(presented));
        Assert.Equal(2f, Row1Length(presented), 4);
        Assert.Equal(2f, Row2Length(presented), 4);
        Assert.Equal(2f, Row3Length(presented), 4);
    }

    [Fact]
    public void CameraAlignedUsesRetailCameraPositiveZWhichIsOppositeEnbNextForward()
    {
        Quaternion orientation = Quaternion.Normalize(
            Quaternion.CreateFromYawPitchRoll(0.9f, -0.35f, 0.22f));
        EnbResolvedRenderCamera camera = CreateCamera(
            position: new Vector3(-25f, 80f, 14f),
            orientation: orientation);
        Matrix4x4 authoredWorld =
            Matrix4x4.CreateScale(1.25f) *
            Matrix4x4.CreateRotationZ(-0.7f) *
            Matrix4x4.CreateTranslation(500f, -120f, 33f);

        Assert.True(EnbCurrentRetailCameraGeometryRuntime.TryResolvePresentationWorldTransform(
            EnbCurrentRetailCameraGeometryRuntime.CameraAlignedGeometry,
            "aligned-rotated-camera",
            EnbW3dSubmissionVertexSource.RigidMesh,
            hasDeformedGeometry: false,
            currentRetailMeshModelScale: 1.25f,
            authoredWorld,
            camera,
            out Matrix4x4 presented,
            out _,
            out string? diagnostic), diagnostic);

        // WW3D camera space looks down local -Z. Retail ALIGNED explicitly
        // Obj_Look_At(mesh_position, mesh_position + camera.Transform.Z, 0),
        // therefore camera +Z is the opposite of EnbNext's world Forward.
        AssertVectorClose(-camera.Basis.Forward, NormalizeRow1(presented));
        AssertVectorClose(new Vector3(500f, -120f, 33f), new Vector3(presented.M41, presented.M42, presented.M43));
        Assert.Equal(1.25f, Row1Length(presented), 4);
    }

    [Fact]
    public void CameraOrientedPlanarMeshRetainsOffAxisProjectiveMapping()
    {
        Assert.True(EnbResolvedRenderCamera.TryCreate(
            Vector3.Zero,
            Quaternion.Identity,
            new EnbRenderViewport(1600, 900, 1.0),
            Math.PI / 2.0,
            4.0 / 3.0,
            new EnbRenderClipRange(6f, 600000f),
            out EnbResolvedRenderCamera camera));

        Vector3[] planarVertices =
        [
            new(0f, 40f, 40f),
            new(0f, -40f, 40f),
            new(0f, -40f, -40f),
            new(0f, 40f, -40f),
        ];
        Matrix4x4 authoredWorld = Matrix4x4.CreateTranslation(100f, 80f, 0f);

        Assert.True(EnbCurrentRetailCameraGeometryRuntime.TryProjectCameraOrientedPlanarMesh(
            EnbCurrentRetailCameraGeometryRuntime.CameraOrientedGeometry,
            "off-axis-plane",
            planarVertices,
            Vector3.Zero,
            40f,
            1f,
            authoredWorld,
            camera,
            out EnbCurrentRetailProjectedPlanarMesh planar,
            out string? diagnostic), diagnostic);

        Assert.True(camera.TryProjectWorldPoint(new Vector3(100f, 80f, 0f), 0, out EnbRenderProjectionResult center));
        double leftExtent = center.PixelX - planar.MinimumX;
        double rightExtent = planar.MaximumX - center.PixelX;

        Assert.True(leftExtent > 0);
        Assert.True(rightExtent > 0);
        Assert.True(Math.Abs(leftExtent - rightExtent) > 10.0);
        Assert.True(planar.MinimumDepth < planar.MaximumDepth);

        // The inverse map must return the canonical atmosphere source corners
        // for the real projected mesh vertices. This checks the projective
        // mapping itself rather than a screen-space bounding approximation.
        for (int i = 0; i < planarVertices.Length; i++)
        {
            Assert.True(EnbCurrentRetailCameraGeometryRuntime.TryResolvePresentationWorldTransform(
                EnbCurrentRetailCameraGeometryRuntime.CameraOrientedGeometry,
                "off-axis-plane",
                EnbW3dSubmissionVertexSource.RigidMesh,
                hasDeformedGeometry: false,
                currentRetailMeshModelScale: 1f,
                authoredWorld,
                camera,
                out Matrix4x4 presented,
                out _,
                out string? transformDiagnostic), transformDiagnostic);
            Vector3 world = Vector3.Transform(planarVertices[i], presented);
            Vector3 view = camera.WorldToCanonicalView(world);
            Assert.True(camera.Projection.TryProjectViewPoint(view, out Vector3 ndc));
            double pixelX = (ndc.X * 0.5 + 0.5) * camera.Viewport.PixelWidth;
            double pixelY = (0.5 - ndc.Y * 0.5) * camera.Viewport.PixelHeight;
            Assert.True(planar.TryMapPixelToSource(pixelX, pixelY, out double u, out double v));
            (double expectedU, double expectedV) = i switch
            {
                0 => (0.0, 0.0),
                1 => (1.0, 0.0),
                2 => (1.0, 1.0),
                _ => (0.0, 1.0),
            };
            Assert.Equal(expectedU, u, 5);
            Assert.Equal(expectedV, v, 5);
        }
    }

    [Fact]
    public void CameraOrientedPlanarMeshDoesNotRequireItsEnclosingSourceSquareToStayInFrontOfCamera()
    {
        Assert.True(EnbResolvedRenderCamera.TryCreate(
            Vector3.Zero,
            Quaternion.Identity,
            new EnbRenderViewport(1600, 900, 1.0),
            Math.PI / 2.0,
            4.0 / 3.0,
            new EnbRenderClipRange(6f, 600000f),
            out EnbResolvedRenderCamera camera));

        // The real authored mesh stays in front of the camera, but its
        // radius-derived enclosing source square reaches farther in local Y
        // than any authored vertex and crosses the camera plane. The Screen
        // bridge must remain defined because only actual mesh support is
        // rasterized; the black source outside it is irrelevant.
        Vector3[] planarVertices =
        [
            new(0f, 100f, 0f),
            new(0f, -100f, 0f),
            new(0f, 0f, 200f),
            new(0f, 0f, -200f),
        ];
        Matrix4x4 authoredWorld = Matrix4x4.CreateTranslation(100f, 80f, 0f);

        Assert.True(EnbCurrentRetailCameraGeometryRuntime.TryProjectCameraOrientedPlanarMesh(
            EnbCurrentRetailCameraGeometryRuntime.CameraOrientedGeometry,
            "off-axis-disc",
            planarVertices,
            Vector3.Zero,
            200f,
            1f,
            authoredWorld,
            camera,
            out EnbCurrentRetailProjectedPlanarMesh planar,
            out string? diagnostic), diagnostic);
        Assert.True(planar.MaximumX > planar.MinimumX);
        Assert.True(planar.MaximumY > planar.MinimumY);

        Assert.True(EnbCurrentRetailCameraGeometryRuntime.TryResolvePresentationWorldTransform(
            EnbCurrentRetailCameraGeometryRuntime.CameraOrientedGeometry,
            "off-axis-disc",
            EnbW3dSubmissionVertexSource.RigidMesh,
            hasDeformedGeometry: false,
            currentRetailMeshModelScale: 1f,
            authoredWorld,
            camera,
            out Matrix4x4 presented,
            out _,
            out string? transformDiagnostic), transformDiagnostic);

        Vector3 squareCorner = Vector3.Transform(new Vector3(0f, -200f, 200f), presented);
        Vector3 cornerView = camera.WorldToCanonicalView(squareCorner);
        Vector4 cornerClip = Vector4.Transform(new Vector4(cornerView, 1f), camera.Projection.Matrix);
        Assert.True(cornerClip.W <= 0f);
    }

    [Fact]
    public void CameraOrientedPresentationPointsLocalPositiveXAtCameraAndReappliesExplicitMeshModelScale()
    {
        EnbResolvedRenderCamera camera = CreateCamera(
            position: new Vector3(20f, 20f, 30f),
            orientation: Quaternion.Identity);
        Matrix4x4 authoredWorld =
            Matrix4x4.CreateScale(3f) *
            Matrix4x4.CreateRotationZ(1.2f) *
            Matrix4x4.CreateTranslation(10f, 20f, 30f);

        bool ok = EnbCurrentRetailCameraGeometryRuntime.TryResolvePresentationWorldTransform(
            EnbCurrentRetailCameraGeometryRuntime.CameraOrientedGeometry,
            "oriented",
            EnbW3dSubmissionVertexSource.RigidMesh,
            hasDeformedGeometry: false,
            currentRetailMeshModelScale: 3f,
            authoredWorld,
            camera,
            out Matrix4x4 presented,
            out _,
            out string? diagnostic);

        Assert.True(ok, diagnostic);
        AssertVectorClose(Vector3.UnitX, NormalizeRow1(presented));
        AssertVectorClose(Vector3.UnitY, NormalizeRow2(presented));
        AssertVectorClose(Vector3.UnitZ, NormalizeRow3(presented));
        Assert.Equal(3f, Row1Length(presented), 4);
    }

    [Fact]
    public void CameraRelativePresentationIgnoresInheritedNonUniformRenderObjBasis()
    {
        EnbResolvedRenderCamera camera = CreateCamera(
            position: new Vector3(25f, 10f, 4f),
            orientation: Quaternion.Identity);
        Matrix4x4 inheritedNonUniformWorld =
            Matrix4x4.CreateScale(0.980204f, 0.980204f, 1f) *
            Matrix4x4.CreateRotationZ(0.35f) *
            Matrix4x4.CreateTranslation(5f, 10f, 4f);

        Assert.True(EnbCurrentRetailCameraGeometryRuntime.TryResolvePresentationWorldTransform(
            EnbCurrentRetailCameraGeometryRuntime.CameraOrientedGeometry,
            "camera-relative-nonuniform-parent",
            EnbW3dSubmissionVertexSource.RigidMesh,
            hasDeformedGeometry: false,
            currentRetailMeshModelScale: 1.5f,
            inheritedNonUniformWorld,
            camera,
            out Matrix4x4 presented,
            out _,
            out string? diagnostic), diagnostic);

        AssertVectorClose(new Vector3(5f, 10f, 4f), new Vector3(presented.M41, presented.M42, presented.M43));
        AssertVectorClose(Vector3.UnitX, NormalizeRow1(presented));
        Assert.Equal(1.5f, Row1Length(presented), 4);
        Assert.Equal(1.5f, Row2Length(presented), 4);
        Assert.Equal(1.5f, Row3Length(presented), 4);
    }

    [Fact]
    public void RetailRollZeroLookAtKeepsPositiveZAsUpForHorizontalTarget()
    {
        Assert.True(EnbCurrentRetailCameraGeometryRuntime.TryBuildRetailRollZeroLookAt(
            new Vector3(4f, 5f, 6f),
            Vector3.UnitY,
            out Matrix4x4 transform));

        AssertVectorClose(Vector3.UnitY, NormalizeRow1(transform));
        AssertVectorClose(-Vector3.UnitX, NormalizeRow2(transform));
        AssertVectorClose(Vector3.UnitZ, NormalizeRow3(transform));
        AssertVectorClose(new Vector3(4f, 5f, 6f), new Vector3(transform.M41, transform.M42, transform.M43));
    }

    [Fact]
    public void UnknownGeometryAndInvalidExplicitMeshModelScaleFailClosed()
    {
        EnbResolvedRenderCamera camera = CreateCamera(Vector3.Zero, Quaternion.Identity);

        Assert.False(EnbCurrentRetailCameraGeometryRuntime.TryResolvePresentationWorldTransform(
            0x00030000u,
            "unknown",
            EnbW3dSubmissionVertexSource.RigidMesh,
            hasDeformedGeometry: false,
            currentRetailMeshModelScale: 1f,
            Matrix4x4.Identity,
            camera,
            out _,
            out _,
            out string? unknownDiagnostic));
        Assert.Contains("unsupported", unknownDiagnostic, StringComparison.OrdinalIgnoreCase);

        Assert.False(EnbCurrentRetailCameraGeometryRuntime.TryResolvePresentationWorldTransform(
            EnbCurrentRetailCameraGeometryRuntime.CameraAlignedGeometry,
            "bad-model-scale",
            EnbW3dSubmissionVertexSource.RigidMesh,
            hasDeformedGeometry: false,
            currentRetailMeshModelScale: float.NaN,
            Matrix4x4.CreateTranslation(10f, 0f, 0f),
            camera,
            out _,
            out _,
            out string? scaleDiagnostic));
        Assert.Contains("MeshModel scale", scaleDiagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(EnbCurrentRetailCameraGeometryRuntime.CameraAlignedGeometry)]
    [InlineData(EnbCurrentRetailCameraGeometryRuntime.CameraOrientedGeometry)]
    public void ZeroMeshScaleCollapsesCameraRelativeGeometry(uint attributes)
    {
        var position = new Vector3(10, 20, 30);
        Assert.True(EnbCurrentRetailCameraGeometryRuntime.TryResolvePresentationWorldTransform(
            attributes, "zero-scale", EnbW3dSubmissionVertexSource.RigidMesh, false, 0,
            Matrix4x4.CreateTranslation(position), CreateCamera(Vector3.Zero, Quaternion.Identity),
            out var world, out _, out var diagnostic), diagnostic);
        Assert.Equal(position, Vector3.Transform(new Vector3(5, -8, 12), world));
    }

    [Fact]
    public void NormalGeometryKeepsAuthoredWorldTransformExactly()
    {
        EnbResolvedRenderCamera camera = CreateCamera(new Vector3(99f, -33f, 7f), Quaternion.Identity);
        Matrix4x4 authoredWorld =
            Matrix4x4.CreateScale(1.5f) *
            Matrix4x4.CreateFromYawPitchRoll(0.2f, 0.4f, -0.7f) *
            Matrix4x4.CreateTranslation(12f, 34f, 56f);

        Assert.True(EnbCurrentRetailCameraGeometryRuntime.TryResolvePresentationWorldTransform(
            EnbCurrentRetailCameraGeometryRuntime.NormalGeometry,
            "normal",
            EnbW3dSubmissionVertexSource.RigidMesh,
            hasDeformedGeometry: false,
            currentRetailMeshModelScale: 99f,
            authoredWorld,
            camera,
            out Matrix4x4 presented,
            out EnbCurrentRetailW3dGeometryClass geometryClass,
            out string? diagnostic), diagnostic);

        Assert.Equal(EnbCurrentRetailW3dGeometryClass.Normal, geometryClass);
        Assert.Equal(authoredWorld, presented);
    }

    private static EnbResolvedRenderCamera CreateCamera(Vector3 position, Quaternion orientation)
    {
        Assert.True(EnbResolvedRenderCamera.TryCreate(
            position,
            orientation,
            new EnbRenderViewport(1280, 720, 1.0),
            Math.PI / 2.0,
            new EnbRenderClipRange(6f, 600000f),
            out EnbResolvedRenderCamera camera));
        return camera;
    }

    private static float Row1Length(Matrix4x4 value) => new Vector3(value.M11, value.M12, value.M13).Length();
    private static float Row2Length(Matrix4x4 value) => new Vector3(value.M21, value.M22, value.M23).Length();
    private static float Row3Length(Matrix4x4 value) => new Vector3(value.M31, value.M32, value.M33).Length();
    private static Vector3 NormalizeRow1(Matrix4x4 value) => Vector3.Normalize(new Vector3(value.M11, value.M12, value.M13));
    private static Vector3 NormalizeRow2(Matrix4x4 value) => Vector3.Normalize(new Vector3(value.M21, value.M22, value.M23));
    private static Vector3 NormalizeRow3(Matrix4x4 value) => Vector3.Normalize(new Vector3(value.M31, value.M32, value.M33));

    private static void AssertVectorClose(Vector3 expected, Vector3 actual)
    {
        Assert.Equal(expected.X, actual.X, 4);
        Assert.Equal(expected.Y, actual.Y, 4);
        Assert.Equal(expected.Z, actual.Z, 4);
    }
}
