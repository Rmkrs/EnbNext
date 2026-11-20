using System.Collections.Immutable;
using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>
/// One run plus the already-compiled material/lighting state that must be
/// installed before that exact indexed range is drawn. Material state remains
/// per-run because may preserve several adjacent effective bindings in one
/// mesh submission without reordering triangles.
/// </summary>
public sealed class EnbW3dProgrammableLightingRunState(
    EnbW3dIndexedSubmissionRun Run,
    EnbW3dMaterialUniforms Material,
    EnbW3dVertexLightingSemantics Lighting,
    bool SpecularEnabled) : IEquatable<EnbW3dProgrammableLightingRunState>
{
    // Actual render state. Structural copies are explicit; owner updates mutate in place.
    public EnbW3dIndexedSubmissionRun Run {get;set;}=Run;
    public EnbW3dMaterialUniforms Material {get;set;}=Material;
    public EnbW3dVertexLightingSemantics Lighting {get;set;}=Lighting;
    public bool SpecularEnabled {get;set;}=SpecularEnabled;

    public override string ToString()
    {
        var text=new System.Text.StringBuilder("EnbW3dProgrammableLightingRunState { ");
        text.Append("Run = ").Append((object?)Run);
        text.Append(", Material = ").Append((object?)Material);
        text.Append(", Lighting = ").Append((object?)Lighting);
        text.Append(", SpecularEnabled = ").Append((object?)SpecularEnabled);
        return text.Append(" }").ToString();
    }
    public EnbW3dProgrammableLightingRunState Copy()=>(EnbW3dProgrammableLightingRunState)MemberwiseClone();
    public EnbW3dProgrammableLightingRunState SetRun(EnbW3dIndexedSubmissionRun value) {Run=value;return this;}
    public EnbW3dProgrammableLightingRunState SetMaterial(EnbW3dMaterialUniforms value) {Material=value;return this;}
    public EnbW3dProgrammableLightingRunState SetLighting(EnbW3dVertexLightingSemantics value) {Lighting=value;return this;}
    public EnbW3dProgrammableLightingRunState SetSpecularEnabled(bool value) {SpecularEnabled=value;return this;}
    public bool Equals(EnbW3dProgrammableLightingRunState? other)=>ReferenceEquals(this,other)||other is not null &&
        EqualityComparer<EnbW3dIndexedSubmissionRun>.Default.Equals(Run,other.Run) &&
        EqualityComparer<EnbW3dMaterialUniforms>.Default.Equals(Material,other.Material) &&
        EqualityComparer<EnbW3dVertexLightingSemantics>.Default.Equals(Lighting,other.Lighting) &&
        EqualityComparer<bool>.Default.Equals(SpecularEnabled,other.SpecularEnabled);
    public override bool Equals(object? other)=>other is EnbW3dProgrammableLightingRunState owner&&Equals(owner);
    public override int GetHashCode()
    {
        unchecked
        {
            int hash=EqualityComparer<Type>.Default.GetHashCode(typeof(EnbW3dProgrammableLightingRunState));
            hash=hash*-1521134295+EqualityComparer<EnbW3dIndexedSubmissionRun>.Default.GetHashCode(Run!);
            hash=hash*-1521134295+EqualityComparer<EnbW3dMaterialUniforms>.Default.GetHashCode(Material!);
            hash=hash*-1521134295+EqualityComparer<EnbW3dVertexLightingSemantics>.Default.GetHashCode(Lighting!);
            hash=hash*-1521134295+EqualityComparer<bool>.Default.GetHashCode(SpecularEnabled!);
            return hash;
        }
    }
    public static bool operator ==(EnbW3dProgrammableLightingRunState? left,EnbW3dProgrammableLightingRunState? right)=>ReferenceEquals(left,right)||left is not null&&left.Equals(right);
    public static bool operator !=(EnbW3dProgrammableLightingRunState? left,EnbW3dProgrammableLightingRunState? right)=>!(left==right);
    public void Deconstruct(out EnbW3dIndexedSubmissionRun Run,out EnbW3dMaterialUniforms Material,out EnbW3dVertexLightingSemantics Lighting,out bool SpecularEnabled)
    {
        Run=this.Run;
        Material=this.Material;
        Lighting=this.Lighting;
        SpecularEnabled=this.SpecularEnabled;
    }
}


/// <summary>
/// Backend-neutral plan for current-retail fixed-function vertex lighting.
/// Owns camera/projection, owns light/material/normal semantics and
/// owns exact indexed ranges. A concrete backend realizes this immutable state
/// without consulting mutable scene lights or reinterpreting W3D descriptors.
/// Realizes each run's already-compiled source/destination blend factors at submission.
/// </summary>
public sealed class EnbW3dProgrammableLightingLanePlan(
    EnbW3dIndexedSubmissionPlan Submission,
    Matrix4x4 CanonicalClipFromModel,
    Matrix4x4 CanonicalViewFromModel,
    Matrix4x4 ModelToWorld,
    Matrix4x4 NormalFromModel,
    Vector3 CameraWorldPosition,
    EnbW3dNormalStream Normals,
    EnbW3dColorStream Color1,
    EnbW3dUvStream TextureCoordinates0,
    EnbW3dLightEnvironment LightEnvironment,
    ImmutableArray<EnbW3dProgrammableLightingRunState> RunStates,
    EnbRenderDepthCompare DepthCompare,
    bool DepthWriteEnabled,
    EnbRenderCullMode CullMode) : IEquatable<EnbW3dProgrammableLightingLanePlan>
{
    // Actual render state. Structural copies are explicit; owner updates mutate in place.
    public EnbW3dIndexedSubmissionPlan Submission {get;set;}=Submission;
    public Matrix4x4 CanonicalClipFromModel {get;set;}=CanonicalClipFromModel;
    public Matrix4x4 CanonicalViewFromModel {get;set;}=CanonicalViewFromModel;
    public Matrix4x4 ModelToWorld {get;set;}=ModelToWorld;
    public Matrix4x4 NormalFromModel {get;set;}=NormalFromModel;
    public Vector3 CameraWorldPosition {get;set;}=CameraWorldPosition;
    public EnbW3dNormalStream Normals {get;set;}=Normals;
    public EnbW3dColorStream Color1 {get;set;}=Color1;
    public EnbW3dUvStream TextureCoordinates0 {get;set;}=TextureCoordinates0;
    public EnbW3dLightEnvironment LightEnvironment {get;set;}=LightEnvironment;
    public ImmutableArray<EnbW3dProgrammableLightingRunState> RunStates {get;set;}=RunStates;
    public EnbRenderDepthCompare DepthCompare {get;set;}=DepthCompare;
    public bool DepthWriteEnabled {get;set;}=DepthWriteEnabled;
    public EnbRenderCullMode CullMode {get;set;}=CullMode;
    public int VertexCount => Submission.Positions.Count;
    public EnbRenderClipRange ClipRange { get; set; }

    public override string ToString()
    {
        var text=new System.Text.StringBuilder("EnbW3dProgrammableLightingLanePlan { ");
        text.Append("Submission = ").Append((object?)Submission);
        text.Append(", CanonicalClipFromModel = ").Append((object?)CanonicalClipFromModel);
        text.Append(", CanonicalViewFromModel = ").Append((object?)CanonicalViewFromModel);
        text.Append(", ModelToWorld = ").Append((object?)ModelToWorld);
        text.Append(", NormalFromModel = ").Append((object?)NormalFromModel);
        text.Append(", CameraWorldPosition = ").Append((object?)CameraWorldPosition);
        text.Append(", Normals = ").Append((object?)Normals);
        text.Append(", Color1 = ").Append((object?)Color1);
        text.Append(", TextureCoordinates0 = ").Append((object?)TextureCoordinates0);
        text.Append(", LightEnvironment = ").Append((object?)LightEnvironment);
        text.Append(", RunStates = ").Append((object?)RunStates);
        text.Append(", DepthCompare = ").Append((object?)DepthCompare);
        text.Append(", DepthWriteEnabled = ").Append((object?)DepthWriteEnabled);
        text.Append(", CullMode = ").Append((object?)CullMode);
        text.Append(", VertexCount = ").Append((object?)VertexCount);
        text.Append(", ClipRange = ").Append((object?)ClipRange);
        return text.Append(" }").ToString();
    }
    public EnbW3dProgrammableLightingLanePlan Copy()=>(EnbW3dProgrammableLightingLanePlan)MemberwiseClone();
    public EnbW3dProgrammableLightingLanePlan SetSubmission(EnbW3dIndexedSubmissionPlan value) {Submission=value;return this;}
    public EnbW3dProgrammableLightingLanePlan SetCanonicalClipFromModel(Matrix4x4 value) {CanonicalClipFromModel=value;return this;}
    public EnbW3dProgrammableLightingLanePlan SetCanonicalViewFromModel(Matrix4x4 value) {CanonicalViewFromModel=value;return this;}
    public EnbW3dProgrammableLightingLanePlan SetModelToWorld(Matrix4x4 value) {ModelToWorld=value;return this;}
    public EnbW3dProgrammableLightingLanePlan SetNormalFromModel(Matrix4x4 value) {NormalFromModel=value;return this;}
    public EnbW3dProgrammableLightingLanePlan SetCameraWorldPosition(Vector3 value) {CameraWorldPosition=value;return this;}
    public EnbW3dProgrammableLightingLanePlan SetNormals(EnbW3dNormalStream value) {Normals=value;return this;}
    public EnbW3dProgrammableLightingLanePlan SetColor1(EnbW3dColorStream value) {Color1=value;return this;}
    public EnbW3dProgrammableLightingLanePlan SetTextureCoordinates0(EnbW3dUvStream value) {TextureCoordinates0=value;return this;}
    public EnbW3dProgrammableLightingLanePlan SetLightEnvironment(EnbW3dLightEnvironment value) {LightEnvironment=value;return this;}
    public EnbW3dProgrammableLightingLanePlan SetRunStates(ImmutableArray<EnbW3dProgrammableLightingRunState> value) {RunStates=value;return this;}
    public EnbW3dProgrammableLightingLanePlan SetDepthCompare(EnbRenderDepthCompare value) {DepthCompare=value;return this;}
    public EnbW3dProgrammableLightingLanePlan SetDepthWriteEnabled(bool value) {DepthWriteEnabled=value;return this;}
    public EnbW3dProgrammableLightingLanePlan SetCullMode(EnbRenderCullMode value) {CullMode=value;return this;}
    public EnbW3dProgrammableLightingLanePlan SetClipRange(EnbRenderClipRange value) {ClipRange=value;return this;}
    public bool Equals(EnbW3dProgrammableLightingLanePlan? other)=>ReferenceEquals(this,other)||other is not null &&
        EqualityComparer<EnbW3dIndexedSubmissionPlan>.Default.Equals(Submission,other.Submission) &&
        EqualityComparer<Matrix4x4>.Default.Equals(CanonicalClipFromModel,other.CanonicalClipFromModel) &&
        EqualityComparer<Matrix4x4>.Default.Equals(CanonicalViewFromModel,other.CanonicalViewFromModel) &&
        EqualityComparer<Matrix4x4>.Default.Equals(ModelToWorld,other.ModelToWorld) &&
        EqualityComparer<Matrix4x4>.Default.Equals(NormalFromModel,other.NormalFromModel) &&
        EqualityComparer<Vector3>.Default.Equals(CameraWorldPosition,other.CameraWorldPosition) &&
        EqualityComparer<EnbW3dNormalStream>.Default.Equals(Normals,other.Normals) &&
        EqualityComparer<EnbW3dColorStream>.Default.Equals(Color1,other.Color1) &&
        EqualityComparer<EnbW3dUvStream>.Default.Equals(TextureCoordinates0,other.TextureCoordinates0) &&
        EqualityComparer<EnbW3dLightEnvironment>.Default.Equals(LightEnvironment,other.LightEnvironment) &&
        EqualityComparer<ImmutableArray<EnbW3dProgrammableLightingRunState>>.Default.Equals(RunStates,other.RunStates) &&
        EqualityComparer<EnbRenderDepthCompare>.Default.Equals(DepthCompare,other.DepthCompare) &&
        EqualityComparer<bool>.Default.Equals(DepthWriteEnabled,other.DepthWriteEnabled) &&
        EqualityComparer<EnbRenderCullMode>.Default.Equals(CullMode,other.CullMode) &&
        EqualityComparer<EnbRenderClipRange>.Default.Equals(ClipRange,other.ClipRange);
    public override bool Equals(object? other)=>other is EnbW3dProgrammableLightingLanePlan owner&&Equals(owner);
    public override int GetHashCode()
    {
        unchecked
        {
            int hash=EqualityComparer<Type>.Default.GetHashCode(typeof(EnbW3dProgrammableLightingLanePlan));
            hash=hash*-1521134295+EqualityComparer<EnbW3dIndexedSubmissionPlan>.Default.GetHashCode(Submission!);
            hash=hash*-1521134295+EqualityComparer<Matrix4x4>.Default.GetHashCode(CanonicalClipFromModel!);
            hash=hash*-1521134295+EqualityComparer<Matrix4x4>.Default.GetHashCode(CanonicalViewFromModel!);
            hash=hash*-1521134295+EqualityComparer<Matrix4x4>.Default.GetHashCode(ModelToWorld!);
            hash=hash*-1521134295+EqualityComparer<Matrix4x4>.Default.GetHashCode(NormalFromModel!);
            hash=hash*-1521134295+EqualityComparer<Vector3>.Default.GetHashCode(CameraWorldPosition!);
            hash=hash*-1521134295+EqualityComparer<EnbW3dNormalStream>.Default.GetHashCode(Normals!);
            hash=hash*-1521134295+EqualityComparer<EnbW3dColorStream>.Default.GetHashCode(Color1!);
            hash=hash*-1521134295+EqualityComparer<EnbW3dUvStream>.Default.GetHashCode(TextureCoordinates0!);
            hash=hash*-1521134295+EqualityComparer<EnbW3dLightEnvironment>.Default.GetHashCode(LightEnvironment!);
            hash=hash*-1521134295+EqualityComparer<ImmutableArray<EnbW3dProgrammableLightingRunState>>.Default.GetHashCode(RunStates!);
            hash=hash*-1521134295+EqualityComparer<EnbRenderDepthCompare>.Default.GetHashCode(DepthCompare!);
            hash=hash*-1521134295+EqualityComparer<bool>.Default.GetHashCode(DepthWriteEnabled!);
            hash=hash*-1521134295+EqualityComparer<EnbRenderCullMode>.Default.GetHashCode(CullMode!);
            hash=hash*-1521134295+EqualityComparer<EnbRenderClipRange>.Default.GetHashCode(ClipRange!);
            return hash;
        }
    }
    public static bool operator ==(EnbW3dProgrammableLightingLanePlan? left,EnbW3dProgrammableLightingLanePlan? right)=>ReferenceEquals(left,right)||left is not null&&left.Equals(right);
    public static bool operator !=(EnbW3dProgrammableLightingLanePlan? left,EnbW3dProgrammableLightingLanePlan? right)=>!(left==right);
    public void Deconstruct(out EnbW3dIndexedSubmissionPlan Submission,out Matrix4x4 CanonicalClipFromModel,out Matrix4x4 CanonicalViewFromModel,out Matrix4x4 ModelToWorld,out Matrix4x4 NormalFromModel,out Vector3 CameraWorldPosition,out EnbW3dNormalStream Normals,out EnbW3dColorStream Color1,out EnbW3dUvStream TextureCoordinates0,out EnbW3dLightEnvironment LightEnvironment,out ImmutableArray<EnbW3dProgrammableLightingRunState> RunStates,out EnbRenderDepthCompare DepthCompare,out bool DepthWriteEnabled,out EnbRenderCullMode CullMode)
    {
        Submission=this.Submission;
        CanonicalClipFromModel=this.CanonicalClipFromModel;
        CanonicalViewFromModel=this.CanonicalViewFromModel;
        ModelToWorld=this.ModelToWorld;
        NormalFromModel=this.NormalFromModel;
        CameraWorldPosition=this.CameraWorldPosition;
        Normals=this.Normals;
        Color1=this.Color1;
        TextureCoordinates0=this.TextureCoordinates0;
        LightEnvironment=this.LightEnvironment;
        RunStates=this.RunStates;
        DepthCompare=this.DepthCompare;
        DepthWriteEnabled=this.DepthWriteEnabled;
        CullMode=this.CullMode;
    }
}


/// <summary>Per-invocation values, separate from retained geometry/material state.
/// Value ownership preserves queued and repeated views without mutating their plans.</summary>
public readonly record struct EnbW3dLightingFrameUniforms(
    Matrix4x4 CanonicalClipFromModel, Matrix4x4 CanonicalViewFromModel,
    Matrix4x4 ModelToWorld, Matrix4x4 NormalFromModel, Vector3 CameraWorldPosition,
    EnbRenderClipRange ClipRange, EnbW3dLightEnvironment LightEnvironment)
{
    public static EnbW3dLightingFrameUniforms From(EnbW3dProgrammableLightingLanePlan plan)
        => new(plan.CanonicalClipFromModel,plan.CanonicalViewFromModel,plan.ModelToWorld,
            plan.NormalFromModel,plan.CameraWorldPosition,plan.ClipRange,plan.LightEnvironment);
    public EnbW3dProgrammableLightingLanePlan Materialize(EnbW3dProgrammableLightingLanePlan definition,
        EnbW3dIndexedSubmissionPlan submission) => (definition).Copy().SetSubmission(submission).SetCanonicalClipFromModel(CanonicalClipFromModel).SetCanonicalViewFromModel(CanonicalViewFromModel).SetModelToWorld(ModelToWorld).SetNormalFromModel(NormalFromModel).SetCameraWorldPosition(CameraWorldPosition).SetClipRange(ClipRange).SetLightEnvironment(LightEnvironment);
}

/// <summary>
/// Pure reduction of the contracts into the first lit
/// programmable lane. This remains deliberately untextured/opaque: lighting is
/// added without simultaneously approximating texture combiners, blending,
/// alpha test, fog or mapper programs.
/// </summary>
public static class EnbW3dProgrammableLightingLaneCompiler
{
    /// <summary>Refresh uniforms after the owner has proved an unchanged rigid definition.</summary>
    public static bool TryResolveRetainedFrame(EnbW3dIndexedSubmissionPlan submission,
        EnbResolvedRenderCamera camera, out EnbW3dLightingFrameUniforms frame, out string? diagnostic)
    {
        frame=default;
        if(!TryResolveFrameTransforms(submission,camera,out var world,out var normal,out var view,out var clip,out diagnostic) ||
           !TryValidateLightEnvironment(submission.Packet.LightEnvironment,"frame",out diagnostic))return false;
        frame=new(clip,view,world,normal,camera.Position,camera.Projection.ClipRange,submission.Packet.LightEnvironment);
        return true;
    }

    private static bool FiniteNormals(IReadOnlyList<Vector3> normals,bool reuseValidatedNormals=false)
    {
        if(normals is EnbW3dNormalStream {IsKnownFinite:true})return true;
        if(reuseValidatedNormals && (normals is EnbW3dFrameStream<Vector3> {HasFiniteVector3Proof:true} ||
            normals is EnbW3dNormalStream {HasFiniteFrameProof:true}))return true;
        if(normals is EnbW3dFrameStream<Vector3> frame)
        {foreach(var normal in frame.ReadSpan)if(!IsFinite(normal))return false;}
        else foreach(var normal in normals)if(!IsFinite(normal))return false;
        return true;
    }
    private static EnbW3dNormalStream SnapshotNormals(IReadOnlyList<Vector3> normals)
        => EnbW3dNormalStream.Snapshot(normals);

    public static bool TryCompile(
        EnbW3dIndexedSubmissionPlan submission,
        EnbResolvedRenderCamera camera,
        out EnbW3dProgrammableLightingLanePlan plan,
        out string? diagnostic)
        => TryCompileCore(
            submission,
            camera,
            requiredTextureStageCount: 0,
            laneName: "R6D",
            allowTextureMapperPrograms: false,
            allowPerRunRasterState: false,
            requireLightingEnabled: true,
            out plan,
            out diagnostic);

    internal static bool TryCompileCore(
        EnbW3dIndexedSubmissionPlan submission,
        EnbResolvedRenderCamera camera,
        byte requiredTextureStageCount,
        string laneName,
        bool allowTextureMapperPrograms,
        bool allowPerRunRasterState,
        bool requireLightingEnabled,
        out EnbW3dProgrammableLightingLanePlan plan,
        out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(submission.Packet);
        ArgumentException.ThrowIfNullOrWhiteSpace(laneName);
        plan = default!;
        diagnostic = null;

        if (submission.Runs.IsDefaultOrEmpty)
        {
            diagnostic = $"Mesh '{submission.Packet.Mesh.Name}' has no R6A submission runs.";
            return false;
        }

        EnbW3dCompiledMaterialSemantics first = submission.Runs[0].Binding.Semantics;
        if (!TryValidateLitState(first, requiredTextureStageCount, laneName, allowTextureMapperPrograms, requireLightingEnabled, out diagnostic))
            return false;

        var runStates = ImmutableArray.CreateBuilder<EnbW3dProgrammableLightingRunState>(submission.Runs.Length);
        foreach (EnbW3dIndexedSubmissionRun run in submission.Runs)
        {
            EnbW3dCompiledMaterialSemantics semantics = run.Binding.Semantics;
            if (!TryValidateLitState(semantics, requiredTextureStageCount, laneName, allowTextureMapperPrograms, requireLightingEnabled, out diagnostic))
            {
                diagnostic = $"Mesh '{submission.Packet.Mesh.Name}' run at triangle {run.FirstTriangleOrdinal}: {diagnostic}";
                return false;
            }

            if (!allowPerRunRasterState &&
                (semantics.FixedFunction.DepthCompare != first.FixedFunction.DepthCompare ||
                 semantics.FixedFunction.DepthWriteEnabled != first.FixedFunction.DepthWriteEnabled ||
                 semantics.FixedFunction.CullMode != first.FixedFunction.CullMode))
            {
                diagnostic = $"Mesh '{submission.Packet.Mesh.Name}' changes depth/cull state between R6A runs; {laneName} requires one raster state for the packet.";
                return false;
            }

            if (!IsFinite(semantics.Uniforms))
            {
                diagnostic = $"Mesh '{submission.Packet.Mesh.Name}' run at triangle {run.FirstTriangleOrdinal} has non-finite material lighting uniforms.";
                return false;
            }

            runStates.Add(new EnbW3dProgrammableLightingRunState(
                run,
                semantics.Uniforms,
                semantics.VertexLighting,
                semantics.FixedFunction.SpecularEnabled));
        }

        int vertexCount = submission.Positions.Count;
        IReadOnlyList<Vector3> normals = submission.Normals;
        if (normals.Count != vertexCount)
        {
            diagnostic = $"Mesh '{submission.Packet.Mesh.Name}' exposes {normals.Count} normals for {vertexCount} lit submission vertices.";
            return false;
        }

        for (int i = 0; normals is not EnbW3dNormalStream {IsKnownFinite:true} && i < vertexCount; i++)
        {
            Vector3 normal = normals[i];
            if (!IsFinite(normal))
            {
                diagnostic = $"Mesh '{submission.Packet.Mesh.Name}' normal {i} is non-finite.";
                return false;
            }
        }

        if (!TryResolveFrameTransforms(submission, camera, out Matrix4x4 presentationWorld,
                out Matrix4x4 normalFromModel, out Matrix4x4 canonicalViewFromModel,
                out Matrix4x4 canonicalClipFromModel, out diagnostic))
            return false;

        if (!EnbW3dProgrammableShaderLaneCompiler.TryCompileColor1(
                submission.Packet.Mesh,
                vertexCount,
                out EnbW3dColorStream color1,
                out diagnostic))
        {
            return false;
        }

        if (!EnbW3dProgrammableShaderLaneCompiler.TryCompileStage0Uv(
                submission.Packet.Material.Source,
                vertexCount,
                out EnbW3dUvStream uv0,
                out diagnostic))
        {
            return false;
        }

        if (!TryValidateLightEnvironment(submission.Packet.LightEnvironment, laneName, out diagnostic))
            return false;

        plan = new EnbW3dProgrammableLightingLanePlan(
            submission,
            canonicalClipFromModel,
            canonicalViewFromModel,
            presentationWorld,
            normalFromModel,
            camera.Position,
            SnapshotNormals(normals),
            color1,
            uv0,
            submission.Packet.LightEnvironment,
            runStates.MoveToImmutable(),
            first.FixedFunction.DepthCompare,
            first.FixedFunction.DepthWriteEnabled,
            first.FixedFunction.CullMode) { ClipRange = camera.Projection.ClipRange };
        return true;
    }

    /// <summary>Refresh frame uniforms without revalidating/copying immutable vertex streams.</summary>
    public static bool TryUpdateFrame(EnbW3dProgrammableLightingLanePlan previous,
        EnbW3dIndexedSubmissionPlan submission, EnbResolvedRenderCamera camera,
        out EnbW3dProgrammableLightingLanePlan plan, out string? diagnostic, bool geometryWillBeUploaded = false,
        bool retainedDefinition = false,bool reuseValidatedNormals = false)
    {
        plan = default!;
        diagnostic = null;
        if (geometryWillBeUploaded && (!ReferenceEquals(previous.Submission.Packet.Mesh, submission.Packet.Mesh) ||
            !ReferenceEquals(previous.Submission.Packet.Material.Source, submission.Packet.Material.Source) ||
            previous.Submission.Runs.Length != submission.Runs.Length))
        {
            diagnostic = "A deformed frame update must retain its mesh and material runs.";
            return false;
        }
        // A live prepared GPU binding proves the same immutable descriptor/range.
        // Moving pose/light inputs below still refresh on every required draw.
        if (!retainedDefinition && !geometryWillBeUploaded && !EnbW3dIndexedSubmissionCompiler.CanReuseGeometry(previous.Submission, submission.Packet))
            return false;
        if (!TryResolveFrameTransforms(submission, camera, out var world, out var normal,
                out var view, out var clip, out diagnostic) ||
            !TryValidateLightEnvironment(submission.Packet.LightEnvironment, "frame", out diagnostic))
            return false;
        // 0093EFE0 -> 00958CC0 writes both skinned position and normal
        // streams each frame. A GPU reupload must not retain first-pose normals.
        var normals = previous.Normals;
        if (geometryWillBeUploaded)
        {
            if (submission.Normals.Count != submission.Positions.Count || !FiniteNormals(submission.Normals,reuseValidatedNormals))
            {
                diagnostic = "A deformed frame must provide one finite normal per vertex.";
                return false;
            }
            normals = SnapshotNormals(submission.Normals);
        }
        plan = (previous).Copy().SetSubmission(submission).SetNormals(normals).SetCanonicalClipFromModel(clip).SetCanonicalViewFromModel(view).SetClipRange(camera.Projection.ClipRange).SetModelToWorld(world).SetNormalFromModel(normal).SetCameraWorldPosition(camera.Position).SetLightEnvironment(submission.Packet.LightEnvironment).SetRunStates(retainedDefinition || previous.Submission.Runs.AsSpan().SequenceEqual(submission.Runs.AsSpan())
                ? previous.RunStates
                : previous.RunStates.Select((state, index) => state.Run == submission.Runs[index]
                    ? state : (state).Copy().SetRun(submission.Runs[index])).ToImmutableArray());
        return true;
    }

    // 0093EFE0/00958CC0: update required pose/uniform inputs on the retained
    // material owner. All failure checks precede mutation; no historical plan.
    internal static bool TryUpdateOwnedSkinFrame(EnbW3dProgrammableLightingLanePlan target,
        EnbW3dIndexedSubmissionPlan submission,EnbResolvedRenderCamera camera,out string? diagnostic)
    {
        diagnostic=null;
        if(submission.Normals.Count!=submission.Positions.Count || !FiniteNormals(submission.Normals,true))
        {diagnostic="A deformed frame must provide one finite normal per vertex.";return false;}
        if(!TryResolveRetainedFrame(submission,camera,out var frame,out diagnostic))return false;
        var normals=SnapshotNormals(submission.Normals);
        target.SetSubmission(submission).SetNormals(normals).SetCanonicalClipFromModel(frame.CanonicalClipFromModel)
            .SetCanonicalViewFromModel(frame.CanonicalViewFromModel).SetClipRange(frame.ClipRange)
            .SetModelToWorld(frame.ModelToWorld).SetNormalFromModel(frame.NormalFromModel)
            .SetCameraWorldPosition(frame.CameraWorldPosition).SetLightEnvironment(frame.LightEnvironment);
        return true;
    }

    internal static bool TryUpdateParticleFrame(EnbW3dProgrammableLightingLanePlan previous,
        EnbW3dIndexedSubmissionPlan submission, EnbResolvedRenderCamera camera,
        out EnbW3dProgrammableLightingLanePlan plan, out string? diagnostic)
    {
        plan=default!; diagnostic=null;
        if(!previous.Submission.Packet.CurrentRetailParticleBuffer || !submission.Packet.CurrentRetailParticleBuffer ||
            previous.RunStates.Length!=submission.Runs.Length || submission.Runs.IsDefaultOrEmpty ||
            !ReferenceEquals(previous.Submission.Packet.CurrentRetailMaterialMappers,submission.Packet.CurrentRetailMaterialMappers) ||
            !ReferenceEquals(previous.Submission.Packet.CurrentRetailSharedMaterialMappers,submission.Packet.CurrentRetailSharedMaterialMappers))return false;
        // A buffer's live range can grow or shrink. Its binding owner, shader,
        // mapper definition and texture binding must survive unchanged.
        for(int i=0;i<submission.Runs.Length;i++)
        {
            var a=previous.Submission.Runs[i].Binding; var b=submission.Runs[i].Binding;
            if(a.VertexMaterial!=b.VertexMaterial || a.Shader!=b.Shader ||
                a.Semantics!=b.Semantics || a.UsesImplicitVertexMaterial!=b.UsesImplicitVertexMaterial ||
                a.UsesImplicitShader!=b.UsesImplicitShader || a.CurrentRetailNullMaterial!=b.CurrentRetailNullMaterial ||
                a.Textures.Length!=b.Textures.Length)return false;
            for(int t=0;t<a.Textures.Length;t++)
                if(a.Textures[t]!=b.Textures[t] ||
                    !ReferenceEquals(a.Textures[t]?.CurrentRetailResolvedTexture,b.Textures[t]?.CurrentRetailResolvedTexture) ||
                    b.Textures[t] is {ResolveLogicalNameAtRuntime:true,CurrentRetailResolvedTexture:null})return false;
        }
        int count=submission.Positions.Count;
        var normals=submission.Normals;
        // Sprite normals are retained immutable definition data. Revalidate
        // only replacement streams; current positions/colors remain dynamic.
        bool retainedNormals=normals is EnbW3dNormalStream normalStream && normalStream.Equals(previous.Normals) ||
            normals is ImmutableArray<Vector3> immutable && ((EnbW3dNormalStream)immutable).Equals(previous.Normals);
        if(normals.Count!=count || !retainedNormals && !FiniteNormals(normals))
        {diagnostic="A particle frame must provide one finite normal per vertex.";return false;}
        if(!TryResolveFrameTransforms(submission,camera,out var world,out var normal,out var view,out var clip,out diagnostic) ||
            !TryValidateLightEnvironment(submission.Packet.LightEnvironment,"particle frame",out diagnostic) ||
            !EnbW3dProgrammableShaderLaneCompiler.TryCompileColor1(submission.Packet.Mesh,count,out var colors,out diagnostic) ||
            !EnbW3dProgrammableShaderLaneCompiler.TryCompileStage0Uv(submission.Packet.Material.Source,count,out var uv,out diagnostic))return false;
        bool owned=submission.Packet.Mesh.RetainsParticleFrame;
        var runs=previous.RunStates;
        if(owned)
        {
            for(int i=0;i<runs.Length;i++)runs[i].Run=submission.Runs[i];
        }
        else if(!previous.Submission.Runs.AsSpan().SequenceEqual(submission.Runs.AsSpan()))
        {
            var builder=ImmutableArray.CreateBuilder<EnbW3dProgrammableLightingRunState>(runs.Length);
            for(int i=0;i<runs.Length;i++)builder.Add(runs[i].Run==submission.Runs[i]?runs[i]:runs[i].Copy().SetRun(submission.Runs[i]));
            runs=builder.MoveToImmutable();
        }
        plan=(owned?previous:previous.Copy()).SetSubmission(submission).SetNormals(SnapshotNormals(normals)).SetColor1(colors).SetTextureCoordinates0(uv).SetCanonicalClipFromModel(clip).SetCanonicalViewFromModel(view).SetClipRange(camera.Projection.ClipRange).SetModelToWorld(world).SetNormalFromModel(normal).SetCameraWorldPosition(camera.Position).SetLightEnvironment(submission.Packet.LightEnvironment).SetRunStates(runs);
        return true;
    }

    private static bool TryResolveFrameTransforms(EnbW3dIndexedSubmissionPlan submission,
        EnbResolvedRenderCamera camera, out Matrix4x4 presentationWorld,
        out Matrix4x4 normalFromModel, out Matrix4x4 canonicalViewFromModel,
        out Matrix4x4 canonicalClipFromModel, out string? diagnostic)
    {
        presentationWorld = normalFromModel = canonicalViewFromModel = canonicalClipFromModel = default;
        diagnostic = null;
        if(submission.Packet.CurrentRetailViewPositions is not null)
        {
            if(!EnbRenderCameraMatrixCompiler.TryCreateWorldToCanonicalView(camera,out var worldToView) ||
                !Matrix4x4.Invert(worldToView,out presentationWorld))
            {
                diagnostic=$"Mesh '{submission.Packet.Mesh.Name}' has no finite inverse camera transform for its view-space vertices.";
                return false;
            }
            normalFromModel=Matrix4x4.Transpose(worldToView);
            canonicalViewFromModel=Matrix4x4.Identity;
            canonicalClipFromModel=camera.Projection.Matrix;
            return true;
        }
        if (!EnbCurrentRetailCameraGeometryRuntime.TryResolvePresentationWorldTransform(
                submission,
                camera,
                out presentationWorld,
                out EnbCurrentRetailW3dGeometryClass geometryClass,
                out diagnostic))
        {
            return false;
        }

        Matrix4x4 normalWorld = presentationWorld;
        if (geometryClass != EnbCurrentRetailW3dGeometryClass.Skin)
        {
            float meshModelScale = submission.Packet.CurrentRetailMeshModelScale;
            if (!float.IsFinite(meshModelScale))
            {
                diagnostic = $"Mesh '{submission.Packet.Mesh.Name}' has invalid current-retail MeshModel scale {meshModelScale} for normal transformation.";
                return false;
            }

            // Retail Create.Scale physically changes MeshModel positions. A
            // uniform scale does not dirty or rewrite the stored vertex normals,
            // and the later RenderObj transform therefore does not contain this
            // scale when D3D builds its inverse-transpose normal transform.
            // EnbNext keeps positions immutable and carries the same scale in the
            // presentation matrix, so remove only that representation-owned
            // linear factor before compiling the normal matrix. Authored and
            // animated RenderObj scale remains intact, and normals are still not
            // normalized afterward (D3DRS_NORMALIZENORMALS is false).
            if (meshModelScale == 0f)
            {
                if (submission.Packet.CurrentRetailUnscaledWorldTransform is not { } rigidWorld)
                {
                    diagnostic = $"Mesh '{submission.Packet.Mesh.Name}' has zero MeshModel scale without its retained RenderObj matrix.";
                    return false;
                }
                if (!EnbCurrentRetailCameraGeometryRuntime.TryResolvePresentationWorldTransform(
                        submission.Packet.Mesh.Attributes, submission.Packet.Mesh.Name, submission.VertexSource,
                        submission.Packet.DeformedGeometry is not null, 1f, rigidWorld, camera,
                        out normalWorld, out _, out diagnostic)) return false;
            }
            else
            {
                normalWorld.M11 /= meshModelScale;
                normalWorld.M12 /= meshModelScale;
                normalWorld.M13 /= meshModelScale;
                normalWorld.M21 /= meshModelScale;
                normalWorld.M22 /= meshModelScale;
                normalWorld.M23 /= meshModelScale;
                normalWorld.M31 /= meshModelScale;
                normalWorld.M32 /= meshModelScale;
                normalWorld.M33 /= meshModelScale;
            }
        }

        if (!Matrix4x4.Invert(normalWorld, out Matrix4x4 inverseWorld))
        {
            diagnostic = $"Mesh '{submission.Packet.Mesh.Name}' has a singular presentation world transform; current-retail fixed-function normal transformation is undefined.";
            return false;
        }
        normalFromModel = Matrix4x4.Transpose(inverseWorld);
        if (!IsFinite(normalFromModel))
        {
            diagnostic = $"Mesh '{submission.Packet.Mesh.Name}' produced a non-finite inverse-transpose normal transform.";
            return false;
        }

        if (!EnbRenderCameraMatrixCompiler.TryCreateCanonicalViewFromModel(
                camera,
                presentationWorld,
                out canonicalViewFromModel) ||
            !EnbRenderCameraMatrixCompiler.TryCreateCanonicalClipFromModel(
                camera,
                presentationWorld,
                out canonicalClipFromModel))
        {
            diagnostic = $"Mesh '{submission.Packet.Mesh.Name}' produced a non-finite R3 camera/world view or clip transform.";
            return false;
        }

        return true;
    }

    private static bool TryValidateLitState(
        EnbW3dCompiledMaterialSemantics semantics,
        byte requiredTextureStageCount,
        string laneName,
        bool allowTextureMapperPrograms,
        bool requireLightingEnabled,
        out string? diagnostic)
    {
        diagnostic = null;
        if (requireLightingEnabled && !semantics.VertexLighting.LightingEnabled)
        {
            diagnostic = $"{laneName} lit lane requires current-retail fixed-function vertex lighting to be enabled.";
            return false;
        }

        if (semantics.PermutationKey.TextureStageCount != requiredTextureStageCount)
        {
            diagnostic = $"{laneName} lit lane requires exactly {requiredTextureStageCount} W3D texture stage(s), got {semantics.PermutationKey.TextureStageCount}.";
            return false;
        }

        if (!EnbW3dProgrammableShaderLaneCompiler.TryValidateCurrentRetailAlphaTest(semantics.FixedFunction.AlphaTest, out diagnostic))
        {
            diagnostic = $"{laneName} lit lane: {diagnostic}";
            return false;
        }

        if (!allowTextureMapperPrograms &&
            (semantics.Mapper0Program.Kind != EnbW3dMapperRuntimeProgramKind.Disabled ||
             semantics.Mapper1Program.Kind != EnbW3dMapperRuntimeProgramKind.Disabled))
        {
            diagnostic = $"{laneName} lit lane does not yet execute W3D texture mapper programs.";
            return false;
        }

        if (semantics.VertexLighting.AmbientSource == EnbW3dMaterialColorSource.Color2 ||
            semantics.VertexLighting.DiffuseSource == EnbW3dMaterialColorSource.Color2 ||
            semantics.VertexLighting.EmissiveSource == EnbW3dMaterialColorSource.Color2)
        {
            diagnostic = $"{laneName} current-W3D lane refuses COLOR2 material sourcing because R4S proved no active W3D COLOR2 stream is submitted.";
            return false;
        }

        if (semantics.VertexLighting.SpecularSource != EnbW3dMaterialColorSource.Material)
        {
            diagnostic = $"{laneName} current-W3D lane requires the R4S-proven material specular source.";
            return false;
        }

        if (!Enum.IsDefined(semantics.FixedFunction.DepthCompare) ||
            !Enum.IsDefined(semantics.FixedFunction.CullMode) ||
            !Enum.IsDefined(semantics.FixedFunction.SourceBlend) ||
            !Enum.IsDefined(semantics.FixedFunction.DestinationBlend) ||
            !Enum.IsDefined(semantics.FixedFunction.FogMode) ||
            !Enum.IsDefined(semantics.VertexLighting.AmbientSource) ||
            !Enum.IsDefined(semantics.VertexLighting.DiffuseSource) ||
            !Enum.IsDefined(semantics.VertexLighting.EmissiveSource))
        {
            diagnostic = $"{laneName} lit lane received an undefined fixed-function depth/cull/blend/lighting selector.";
            return false;
        }

        return true;
    }

    private static bool TryValidateLightEnvironment(
        EnbW3dLightEnvironment environment,
        string laneName,
        out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(environment);
        diagnostic = null;

        if (environment.Lights.Length != EnbW3dLightEnvironment.CurrentRetailLightSlotCount)
        {
            diagnostic = $"{laneName} expected exactly {EnbW3dLightEnvironment.CurrentRetailLightSlotCount} snapshotted current-retail light slots, got {environment.Lights.Length}.";
            return false;
        }

        for (int i = 0; i < environment.Lights.Length; i++)
        {
            EnbW3dLightSlot slot = environment.Lights[i];
            if (!slot.Enabled)
                continue;

            EnbW3dCompiledLight light = slot.Light;
            if (!Enum.IsDefined(light.Type) ||
                (!environment.HasNativeStationArithmetic && !IsFinite(light.Diffuse)) ||
                !IsFinite(light.Specular) ||
                !IsFinite(light.Ambient) ||
                !IsFinite(light.Position) ||
                (!environment.HasNativeStationArithmetic && !IsFinite(light.Direction)) ||
                !float.IsFinite(light.Range) ||
                !float.IsFinite(light.Falloff) ||
                !float.IsFinite(light.Attenuation0) ||
                !float.IsFinite(light.Attenuation1) ||
                !float.IsFinite(light.Attenuation2) ||
                !float.IsFinite(light.Theta) ||
                !float.IsFinite(light.Phi))
            {
                diagnostic = $"{laneName} light slot {i} contains an undefined type or non-finite D3DLIGHT8 value.";
                return false;
            }
        }

        return true;
    }

    private static bool IsFinite(EnbW3dMaterialUniforms material)
        => IsFinite(material.Ambient) && IsFinite(material.Diffuse) &&
           IsFinite(material.Specular) && IsFinite(material.Emissive) &&
           float.IsFinite(material.Power) && float.IsFinite(material.DiffuseAlpha);

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFinite(Matrix4x4 value)
        => float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13) && float.IsFinite(value.M14) &&
           float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M23) && float.IsFinite(value.M24) &&
           float.IsFinite(value.M31) && float.IsFinite(value.M32) && float.IsFinite(value.M33) && float.IsFinite(value.M34) &&
           float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43) && float.IsFinite(value.M44);
}
