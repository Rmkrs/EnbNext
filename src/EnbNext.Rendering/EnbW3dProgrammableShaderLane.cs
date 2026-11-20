using System.Collections.Immutable;
using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>
/// First concrete programmable W3D shader lane. This deliberately closes only
/// the exact current-retail case whose fragment input is the already-resolved
/// COLO stream: lighting disabled, no active texture combiner, current-retail alpha test,
/// and no runtime texture mapper. This adds exact fixed-function blending, adds the finite current-retail alpha-test tuples, and admits the closed current-retail fog selector.
/// Unsupported material
/// state fails closed instead of being approximated by the backend.
/// </summary>
public enum EnbW3dProgrammableShaderLaneKind : byte
{
    UnlitColor1Opaque = 0,
}

/// <summary>
/// Backend-neutral scene/material inputs for the first programmable W3D lane.
/// CanonicalClipFromModel is still in the NDC-depth 0..1 convention; a concrete
/// graphics API performs its one documented clip-space conversion at submission.
/// </summary>
public sealed record EnbW3dProgrammableShaderLanePlan(
    EnbW3dIndexedSubmissionPlan Submission,
    EnbW3dProgrammableShaderLaneKind Lane,
    Matrix4x4 CanonicalClipFromModel,
    Matrix4x4 CanonicalViewFromModel,
    EnbW3dColorStream Color1,
    EnbW3dUvStream TextureCoordinates0,
    EnbRenderDepthCompare DepthCompare,
    bool DepthWriteEnabled,
    EnbRenderCullMode CullMode)
{
    public int VertexCount => Submission.Positions.Count;
    public EnbRenderClipRange ClipRange { get; init; }
}

/// <summary>
/// Matrix adapter for the already-closed camera basis/projection. The helper
/// exists so concrete backends do not independently reinterpret EnbNext's
/// +X-forward/+Y-left/+Z-up world semantics.
/// </summary>
public static class EnbRenderCameraMatrixCompiler
{
    public static bool TryCreateWorldToCanonicalView(
        EnbResolvedRenderCamera camera,
        out Matrix4x4 worldToView)
    {
        EnbRenderCameraBasis basis = camera.Basis;
        Vector3 position = camera.Position;
        worldToView = new Matrix4x4(
            basis.Right.X, basis.Up.X, -basis.Forward.X, 0f,
            basis.Right.Y, basis.Up.Y, -basis.Forward.Y, 0f,
            basis.Right.Z, basis.Up.Z, -basis.Forward.Z, 0f,
            -Vector3.Dot(position, basis.Right),
            -Vector3.Dot(position, basis.Up),
            Vector3.Dot(position, basis.Forward),
            1f);

        return IsFinite(worldToView);
    }

    public static bool TryCreateCanonicalViewFromModel(
        EnbResolvedRenderCamera camera,
        Matrix4x4 modelToWorld,
        out Matrix4x4 canonicalViewFromModel)
    {
        canonicalViewFromModel = default;
        if (!IsFinite(modelToWorld) ||
            !TryCreateWorldToCanonicalView(camera, out Matrix4x4 worldToView))
        {
            return false;
        }

        canonicalViewFromModel = modelToWorld * worldToView;
        return IsFinite(canonicalViewFromModel);
    }

    public static bool TryCreateCanonicalClipFromModel(
        EnbResolvedRenderCamera camera,
        Matrix4x4 modelToWorld,
        out Matrix4x4 canonicalClipFromModel)
    {
        canonicalClipFromModel = default;
        if (!TryCreateCanonicalViewFromModel(camera, modelToWorld, out Matrix4x4 canonicalViewFromModel))
            return false;

        canonicalClipFromModel = canonicalViewFromModel * camera.Projection.Matrix;
        return IsFinite(canonicalClipFromModel);
    }

    private static bool IsFinite(Matrix4x4 value)
        => float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13) && float.IsFinite(value.M14) &&
           float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M23) && float.IsFinite(value.M24) &&
           float.IsFinite(value.M31) && float.IsFinite(value.M32) && float.IsFinite(value.M33) && float.IsFinite(value.M34) &&
           float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43) && float.IsFinite(value.M44);
}

/// <summary>
/// Pure reduction for the first scene-correct programmable lane.
/// COLO follows: missing descriptor COLO is opaque white, otherwise the
/// already-materialized mesh EffectiveColor1 stream is consumed verbatim.
/// Stage-0 UVs are preserved when present, but does not yet sample a texture.
/// </summary>
public static class EnbW3dProgrammableShaderLaneCompiler
{
    private static readonly Vector4 OpaqueWhite = Vector4.One;

    public static bool TryCompileFirstLane(
        EnbW3dIndexedSubmissionPlan submission,
        EnbResolvedRenderCamera camera,
        out EnbW3dProgrammableShaderLanePlan plan,
        out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(submission.Packet);
        plan = default!;
        diagnostic = null;

        if (submission.Runs.IsDefaultOrEmpty)
        {
            diagnostic = $"Mesh '{submission.Packet.Mesh.Name}' has no R6A submission runs.";
            return false;
        }

        EnbW3dCompiledMaterialSemantics first = submission.Runs[0].Binding.Semantics;
        if (!TryValidateFirstLane(first, out diagnostic))
            return false;

        foreach (EnbW3dIndexedSubmissionRun run in submission.Runs)
        {
            EnbW3dCompiledMaterialSemantics semantics = run.Binding.Semantics;
            if (!TryValidateFirstLane(semantics, out diagnostic))
            {
                diagnostic = $"Mesh '{submission.Packet.Mesh.Name}' run at triangle {run.FirstTriangleOrdinal}: {diagnostic}";
                return false;
            }

            if (semantics.FixedFunction.DepthCompare != first.FixedFunction.DepthCompare ||
                semantics.FixedFunction.DepthWriteEnabled != first.FixedFunction.DepthWriteEnabled ||
                semantics.FixedFunction.CullMode != first.FixedFunction.CullMode)
            {
                diagnostic = $"Mesh '{submission.Packet.Mesh.Name}' changes depth/cull state between R6A runs; R6C first-lane submission requires one raster state for the packet.";
                return false;
            }
        }

        if (!EnbCurrentRetailCameraGeometryRuntime.TryResolvePresentationWorldTransform(
                submission,
                camera,
                out Matrix4x4 presentationWorld,
                out _,
                out diagnostic))
        {
            return false;
        }

        if (!EnbRenderCameraMatrixCompiler.TryCreateCanonicalViewFromModel(
                camera,
                presentationWorld,
                out Matrix4x4 canonicalViewFromModel) ||
            !EnbRenderCameraMatrixCompiler.TryCreateCanonicalClipFromModel(
                camera,
                presentationWorld,
                out Matrix4x4 canonicalClipFromModel))
        {
            diagnostic = $"Mesh '{submission.Packet.Mesh.Name}' produced a non-finite R3 camera/world view or clip transform.";
            return false;
        }

        if(submission.Packet.CurrentRetailViewPositions is not null)
        {
            canonicalViewFromModel=Matrix4x4.Identity;
            canonicalClipFromModel=camera.Projection.Matrix;
        }
        int vertexCount = submission.Positions.Count;
        if (!TryCompileColor1(submission.Packet.Mesh, vertexCount, out EnbW3dColorStream color1, out diagnostic))
            return false;

        if (!TryCompileStage0Uv(submission.Packet.Material.Source, vertexCount, out EnbW3dUvStream uv0, out diagnostic))
            return false;

        plan = new EnbW3dProgrammableShaderLanePlan(
            submission,
            EnbW3dProgrammableShaderLaneKind.UnlitColor1Opaque,
            canonicalClipFromModel,
            canonicalViewFromModel,
            color1,
            uv0,
            first.FixedFunction.DepthCompare,
            first.FixedFunction.DepthWriteEnabled,
            first.FixedFunction.CullMode) { ClipRange = camera.Projection.ClipRange };
        return true;
    }

    private static bool TryValidateFirstLane(
        EnbW3dCompiledMaterialSemantics semantics,
        out string? diagnostic)
    {
        diagnostic = null;
        if (semantics.VertexLighting.LightingEnabled)
        {
            diagnostic = "R6C first lane does not yet implement fixed-function vertex lighting.";
            return false;
        }

        if (semantics.PermutationKey.TextureStageCount != 0)
        {
            diagnostic = "R6C first lane does not yet sample W3D texture stages.";
            return false;
        }

        if (!TryValidateCurrentRetailAlphaTest(semantics.FixedFunction.AlphaTest, out diagnostic))
            return false;

        if (semantics.Mapper0Program.Kind != EnbW3dMapperRuntimeProgramKind.Disabled ||
            semantics.Mapper1Program.Kind != EnbW3dMapperRuntimeProgramKind.Disabled)
        {
            diagnostic = "R6C first lane does not yet execute W3D texture mapper programs.";
            return false;
        }

        if (!Enum.IsDefined(semantics.FixedFunction.DepthCompare) ||
            !Enum.IsDefined(semantics.FixedFunction.CullMode) ||
            !Enum.IsDefined(semantics.FixedFunction.SourceBlend) ||
            !Enum.IsDefined(semantics.FixedFunction.DestinationBlend) ||
            !Enum.IsDefined(semantics.FixedFunction.FogMode))
        {
            diagnostic = "R6M first lane received an undefined depth/cull/blend selector.";
            return false;
        }

        return true;
    }

    internal static bool TryValidateCurrentRetailAlphaTest(
        EnbW3dAlphaTestSemantics alphaTest,
        out string? diagnostic)
    {
        diagnostic = null;
        if (!alphaTest.Enabled)
        {
            if (alphaTest.Compare == EnbRenderDepthCompare.Always && alphaTest.Reference == 0)
                return true;
            diagnostic = $"R6N disabled alpha test must carry the neutral ALWAYS/0 tuple; got compare={alphaTest.Compare} reference=0x{alphaTest.Reference:X2}.";
            return false;
        }

        if (alphaTest.Compare == EnbRenderDepthCompare.LessOrEqual && alphaTest.Reference == 0x9F)
            return true;
        if (alphaTest.Compare == EnbRenderDepthCompare.GreaterOrEqual && alphaTest.Reference == 0x60)
            return true;

        diagnostic = $"R6N only realizes the two exact current-retail alpha-test tuples; got enabled={alphaTest.Enabled} compare={alphaTest.Compare} reference=0x{alphaTest.Reference:X2}.";
        return false;
    }

    internal static bool TryCompileColor1(
        EnbW3dRenderMesh mesh,
        int vertexCount,
        out EnbW3dColorStream color1,
        out string? diagnostic)
    {
        diagnostic = null;
        if (mesh.EffectiveColor1.Count == 0)
        {
            color1 = EnbW3dColorStream.White(vertexCount);
            return true;
        }

        if (mesh.EffectiveColor1.Count != vertexCount)
        {
            color1 = ImmutableArray<Vector4>.Empty;
            diagnostic = $"Mesh '{mesh.Name}' exposes {mesh.EffectiveColor1.Count} effective COLOR1 values for {vertexCount} vertices.";
            return false;
        }

        color1 = EnbW3dColorStream.Snapshot(mesh.EffectiveColor1);
        return true;
    }

    internal static bool TryCompileStage0Uv(
        EnbW3dRenderMaterialPass pass,
        int vertexCount,
        out EnbW3dUvStream uv0,
        out string? diagnostic)
    {
        foreach(var stage in pass.TextureStages)
            if(stage.Ordinal==0 && stage.EffectiveRuntimeTexCoords is EnbW3dUvOwner owner && owner.Count==vertexCount)
            {uv0=new(owner);diagnostic=null;return true;}
        bool result=TryCompileStageUv(pass,0,vertexCount,out var immutable,out diagnostic);
        uv0=immutable;return result;
    }

    internal static bool TryCompileStageUv(
        EnbW3dRenderMaterialPass pass,
        int stageOrdinal,
        int vertexCount,
        out ImmutableArray<Vector2> uv,
        out string? diagnostic)
    {
        diagnostic = null;
        EnbW3dRenderTextureStage? stage = pass.TextureStages.FirstOrDefault(stage => stage.Ordinal == stageOrdinal);
        if (stage is null || stage.EffectiveRuntimeTexCoords.Count == 0)
        {
            uv = ImmutableArray.CreateRange(Enumerable.Repeat(Vector2.Zero, vertexCount));
            return true;
        }

        if (stage.EffectiveRuntimeTexCoords.Count != vertexCount)
        {
            uv = ImmutableArray<Vector2>.Empty;
            diagnostic = $"Material pass {pass.Ordinal} exposes {stage.EffectiveRuntimeTexCoords.Count} effective stage-{stageOrdinal} UVs for {vertexCount} vertices.";
            return false;
        }

        //978B70 copies the authored UV words to the VB without validation.
        //NaNs in an unused or mapper-overridden coordinate do not remove a mesh.
        uv = ImmutableArray.CreateRange(stage.EffectiveRuntimeTexCoords);
        return true;
    }
}
