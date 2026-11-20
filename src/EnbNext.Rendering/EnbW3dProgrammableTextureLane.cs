using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>
/// Finite programmable implementation key for the current-retail stage-0
/// texture programs, including the two bump-environment forms. Raw W3D selector aliases are intentionally absent:
/// shader selection is keyed from the already-compiled stage program.
/// </summary>
public enum EnbW3dProgrammableStage0ProgramKind : byte
{
    SelectTexture = 0,
    ModulateTextureDiffuse = 1,
    AddTextureDiffuseColorModulateAlpha = 2,
    Modulate2XTextureDiffuseColorModulateAlpha = 3,
    BumpEnvironmentMap = 4,
    BumpEnvironmentMapLuminance = 5,
    PassDiffuse = 6,
}

public enum EnbW3dProgrammableStage1ColorProgramKind : byte
{
    Disabled = 0,
    PassCurrent = 1,
    SelectTexture = 2,
    ModulateTextureCurrent = 3,
    AddSmoothTextureCurrent = 4,
    AddTextureCurrent = 5,
    SubtractTextureCurrent = 6,
    SubtractCurrentTexture = 7,
    BlendTextureAlpha = 8,
    BlendCurrentAlpha = 9,
    AddSignedTextureCurrent = 10,
    AddSigned2XTextureCurrent = 11,
    Modulate2XTextureCurrent = 12,
    ModulateCurrentAlphaAddTextureColor = 13,
}

public enum EnbW3dProgrammableStage1AlphaProgramKind : byte
{
    Disabled = 0,
    PassCurrent = 1,
    SelectTexture = 2,
    ModulateTextureCurrent = 3,
    AddSmoothTextureCurrent = 4,
}

/// <summary>
/// Capability-independent key for the exact stage-1 program. The two
/// components remain separate because current retail independently selects color
/// and alpha, then normalizes a half-active stage by explicitly passing CURRENT
/// through the inactive component.
/// </summary>
public readonly record struct EnbW3dProgrammableStage1Program(
    EnbW3dProgrammableStage1ColorProgramKind Color,
    EnbW3dProgrammableStage1AlphaProgramKind Alpha)
{
    public bool Enabled => Color != EnbW3dProgrammableStage1ColorProgramKind.Disabled ||
                           Alpha != EnbW3dProgrammableStage1AlphaProgramKind.Disabled;

    public static EnbW3dProgrammableStage1Program Disabled => new(
        EnbW3dProgrammableStage1ColorProgramKind.Disabled,
        EnbW3dProgrammableStage1AlphaProgramKind.Disabled);
}

/// <summary>
/// One run plus the exact texture images, current-retail sampler
/// semantics, mip-reduction plans and finite programs that must be
/// installed for that indexed range. This keeps the stage-0 members as
/// the compatibility surface and adds stage 1 without carrying raw selector
/// identities into a concrete backend.
/// </summary>
public sealed record EnbW3dProgrammableTextureRunState(
    EnbW3dProgrammableLightingRunState LightingRun,
    EnbW3dRenderTextureBinding Texture0,
    EnbTextureImage? Image0,
    EnbTextureUploadMipPlan UploadMipPlan,
    EnbW3dTextureSamplerSemantics Sampler,
    EnbW3dTextureStageSemantics Stage0Combiner,
    EnbW3dProgrammableStage0ProgramKind Stage0Program)
{
    public EnbW3dRenderTextureBinding? Texture1 { get; init; }
    public EnbTextureImage? Image1 { get; init; }
    public EnbTextureUploadMipPlan? UploadMipPlan1 { get; init; }
    public EnbW3dTextureSamplerSemantics? Sampler1 { get; init; }
    public EnbW3dTextureStageSemantics Stage1Combiner { get; init; }
    public EnbW3dProgrammableStage1Program Stage1Program { get; init; } = EnbW3dProgrammableStage1Program.Disabled;
    public bool Stage0TerminatedByNullTextureCascade { get; init; }
    public bool Stage1TerminatedByNullTextureCascade { get; init; }
    public EnbW3dBumpEnvironmentSemantics BumpEnvironment { get; init; } = EnbW3dBumpEnvironmentSemantics.RetailDefault;
    public EnbW3dBumpSemanticTexture? BumpTexture0 { get; init; }
    public EnbW3dMapperRuntimeProgram Mapper0Program { get; init; } = EnbW3dMapperRuntimeProgram.Disabled;
    public EnbW3dMapperRuntimeProgram Mapper1Program { get; init; } = EnbW3dMapperRuntimeProgram.Disabled;

    public bool IsBumpStage0 => Stage0Program is EnbW3dProgrammableStage0ProgramKind.BumpEnvironmentMap
        or EnbW3dProgrammableStage0ProgramKind.BumpEnvironmentMapLuminance;

    public bool SamplesTexture0 => Stage0Program != EnbW3dProgrammableStage0ProgramKind.PassDiffuse;
}

/// <summary>
/// Backend-neutral plan. These retain camera, normal,
/// lighting, material and exact-run ownership. These supply the finite
/// two-stage combiner, authored addressing, runtime filtering and mip plans.
/// </summary>
public sealed class EnbW3dProgrammableTextureLanePlan(
    EnbW3dProgrammableLightingLanePlan LightingPlan,
    ImmutableArray<EnbW3dProgrammableTextureRunState> RunStates,
    EnbRenderTextureRuntimeSettings RuntimeSettings,
    EnbRenderTextureBackendCapabilities BackendCapabilities) : IEquatable<EnbW3dProgrammableTextureLanePlan>
{
    // Actual render state. Structural copies are explicit; owner updates mutate in place.
    public EnbW3dProgrammableLightingLanePlan LightingPlan {get;set;}=LightingPlan;
    public ImmutableArray<EnbW3dProgrammableTextureRunState> RunStates {get;set;}=RunStates;
    public EnbRenderTextureRuntimeSettings RuntimeSettings {get;set;}=RuntimeSettings;
    public EnbRenderTextureBackendCapabilities BackendCapabilities {get;set;}=BackendCapabilities;
    public EnbW3dIndexedSubmissionPlan Submission => LightingPlan.Submission;
    public int VertexCount => LightingPlan.VertexCount;
    public EnbW3dUvStream TextureCoordinates1 { get; set; }
    internal EnbW3dUvOwner? ZeroUvOwner;
    public Matrix4x4 RetailGeneratedCoordinateViewFromWorld { get; set; } = Matrix4x4.Identity;
    public Matrix4x4 CanonicalCameraProjection { get; set; } = Matrix4x4.Identity;

    public override string ToString()
    {
        var text=new System.Text.StringBuilder("EnbW3dProgrammableTextureLanePlan { ");
        text.Append("LightingPlan = ").Append((object?)LightingPlan);
        text.Append(", RunStates = ").Append((object?)RunStates);
        text.Append(", RuntimeSettings = ").Append((object?)RuntimeSettings);
        text.Append(", BackendCapabilities = ").Append((object?)BackendCapabilities);
        text.Append(", Submission = ").Append((object?)Submission);
        text.Append(", VertexCount = ").Append((object?)VertexCount);
        text.Append(", TextureCoordinates1 = ").Append((object?)TextureCoordinates1);
        text.Append(", RetailGeneratedCoordinateViewFromWorld = ").Append((object?)RetailGeneratedCoordinateViewFromWorld);
        text.Append(", CanonicalCameraProjection = ").Append((object?)CanonicalCameraProjection);
        return text.Append(" }").ToString();
    }
    public EnbW3dProgrammableTextureLanePlan Copy()=>(EnbW3dProgrammableTextureLanePlan)MemberwiseClone();
    public EnbW3dProgrammableTextureLanePlan SetLightingPlan(EnbW3dProgrammableLightingLanePlan value) {LightingPlan=value;return this;}
    public EnbW3dProgrammableTextureLanePlan SetRunStates(ImmutableArray<EnbW3dProgrammableTextureRunState> value) {RunStates=value;return this;}
    public EnbW3dProgrammableTextureLanePlan SetRuntimeSettings(EnbRenderTextureRuntimeSettings value) {RuntimeSettings=value;return this;}
    public EnbW3dProgrammableTextureLanePlan SetBackendCapabilities(EnbRenderTextureBackendCapabilities value) {BackendCapabilities=value;return this;}
    public EnbW3dProgrammableTextureLanePlan SetTextureCoordinates1(EnbW3dUvStream value) {TextureCoordinates1=value;return this;}
    public EnbW3dProgrammableTextureLanePlan SetRetailGeneratedCoordinateViewFromWorld(Matrix4x4 value) {RetailGeneratedCoordinateViewFromWorld=value;return this;}
    public EnbW3dProgrammableTextureLanePlan SetCanonicalCameraProjection(Matrix4x4 value) {CanonicalCameraProjection=value;return this;}
    public bool Equals(EnbW3dProgrammableTextureLanePlan? other)=>ReferenceEquals(this,other)||other is not null &&
        EqualityComparer<EnbW3dProgrammableLightingLanePlan>.Default.Equals(LightingPlan,other.LightingPlan) &&
        EqualityComparer<ImmutableArray<EnbW3dProgrammableTextureRunState>>.Default.Equals(RunStates,other.RunStates) &&
        EqualityComparer<EnbRenderTextureRuntimeSettings>.Default.Equals(RuntimeSettings,other.RuntimeSettings) &&
        EqualityComparer<EnbRenderTextureBackendCapabilities>.Default.Equals(BackendCapabilities,other.BackendCapabilities) &&
        EqualityComparer<EnbW3dUvStream>.Default.Equals(TextureCoordinates1,other.TextureCoordinates1) &&
        EqualityComparer<Matrix4x4>.Default.Equals(RetailGeneratedCoordinateViewFromWorld,other.RetailGeneratedCoordinateViewFromWorld) &&
        EqualityComparer<Matrix4x4>.Default.Equals(CanonicalCameraProjection,other.CanonicalCameraProjection);
    public override bool Equals(object? other)=>other is EnbW3dProgrammableTextureLanePlan owner&&Equals(owner);
    public override int GetHashCode()
    {
        unchecked
        {
            int hash=EqualityComparer<Type>.Default.GetHashCode(typeof(EnbW3dProgrammableTextureLanePlan));
            hash=hash*-1521134295+EqualityComparer<EnbW3dProgrammableLightingLanePlan>.Default.GetHashCode(LightingPlan!);
            hash=hash*-1521134295+EqualityComparer<ImmutableArray<EnbW3dProgrammableTextureRunState>>.Default.GetHashCode(RunStates!);
            hash=hash*-1521134295+EqualityComparer<EnbRenderTextureRuntimeSettings>.Default.GetHashCode(RuntimeSettings!);
            hash=hash*-1521134295+EqualityComparer<EnbRenderTextureBackendCapabilities>.Default.GetHashCode(BackendCapabilities!);
            hash=hash*-1521134295+EqualityComparer<EnbW3dUvStream>.Default.GetHashCode(TextureCoordinates1!);
            hash=hash*-1521134295+EqualityComparer<Matrix4x4>.Default.GetHashCode(RetailGeneratedCoordinateViewFromWorld!);
            hash=hash*-1521134295+EqualityComparer<Matrix4x4>.Default.GetHashCode(CanonicalCameraProjection!);
            return hash;
        }
    }
    public static bool operator ==(EnbW3dProgrammableTextureLanePlan? left,EnbW3dProgrammableTextureLanePlan? right)=>ReferenceEquals(left,right)||left is not null&&left.Equals(right);
    public static bool operator !=(EnbW3dProgrammableTextureLanePlan? left,EnbW3dProgrammableTextureLanePlan? right)=>!(left==right);
    public void Deconstruct(out EnbW3dProgrammableLightingLanePlan LightingPlan,out ImmutableArray<EnbW3dProgrammableTextureRunState> RunStates,out EnbRenderTextureRuntimeSettings RuntimeSettings,out EnbRenderTextureBackendCapabilities BackendCapabilities)
    {
        LightingPlan=this.LightingPlan;
        RunStates=this.RunStates;
        RuntimeSettings=this.RuntimeSettings;
        BackendCapabilities=this.BackendCapabilities;
    }
}


/// <summary>
/// Resolves diffuse-only zero-stage and currently-closed one- or two-stage textured lighting lanes
/// semantics, including the stage-0 bump pair. The caller owns texture discovery/asset
/// caching and supplies decoded EnbTextureImage instances; this compiler never
/// reads files or MIX archives itself.
/// </summary>
public static class EnbW3dProgrammableTextureLaneCompiler
{
    /// <summary>Reuse authored textures/mips/materials while refreshing all frame-owned uniforms.</summary>
    public static bool TryUpdateFrame(EnbW3dProgrammableTextureLanePlan previous,
        EnbW3dIndexedSubmissionPlan submission, EnbResolvedRenderCamera camera,
        out EnbW3dProgrammableTextureLanePlan plan, out string? diagnostic, bool geometryWillBeUploaded = false,
        bool retainedDefinition = false,bool reuseValidatedNormals = false)
    {
        plan = default!;
        if (!EnbW3dProgrammableLightingLaneCompiler.TryUpdateFrame(previous.LightingPlan,
                submission, camera, out var lighting, out diagnostic, geometryWillBeUploaded, retainedDefinition,reuseValidatedNormals))
            return false;
        plan = (previous).Copy().SetLightingPlan(lighting).SetCanonicalCameraProjection(camera.Projection.Matrix).SetRunStates(previous.LightingPlan.RunStates.Equals(lighting.RunStates)
                ? previous.RunStates
                : previous.RunStates.Select((state, index) => ReferenceEquals(state.LightingRun,lighting.RunStates[index])
                    ? state : state with { LightingRun = lighting.RunStates[index] }).ToImmutableArray()).SetRetailGeneratedCoordinateViewFromWorld(EnbW3dGeneratedTextureCoordinates.CreateRetailCameraViewFromWorld(camera));
        return true;
    }

    /// <summary>Retain a particle buffer's authored lane while replacing every changing stream.</summary>
    public static bool TryUpdateParticleFrame(EnbW3dProgrammableTextureLanePlan previous,
        EnbW3dIndexedSubmissionPlan submission, EnbResolvedRenderCamera camera,
        EnbRenderTextureRuntimeSettings settings, EnbRenderTextureBackendCapabilities capabilities,
        out EnbW3dProgrammableTextureLanePlan plan, out string? diagnostic)
    {
        plan=default!;diagnostic=null;
        settings=EnbRenderTextureRuntimeSettings.FromRetailValues(settings.TextureReduction,(int)settings.TextureFilter);
        if(previous.RuntimeSettings!=settings || previous.BackendCapabilities!=capabilities)return false;
        var uv1=previous.TextureCoordinates1;
        int vertexCount=submission.Positions.Count;
        // Validate all fallible texture work before mutating the owned lane.
        bool stage1=false;
        foreach(var stage in submission.Packet.Material.Source.TextureStages)
            if(stage.Ordinal==1&&stage.EffectiveRuntimeTexCoords.Count!=0){stage1=true;break;}
        if(!stage1)foreach(var stage in previous.Submission.Packet.Material.Source.TextureStages)
            if(stage.Ordinal==1&&stage.EffectiveRuntimeTexCoords.Count!=0){stage1=true;break;}
        if(!stage1 && submission.Packet.Mesh.RetainsParticleFrame)
        {
            // This lane owns a zero-filled capacity range, not a new array for
            // every changing live count. The stream captures the active length
            // so the backend still detects a vertex-buffer size change.
            previous.ZeroUvOwner??=new();
            previous.ZeroUvOwner.Prepare(vertexCount,0,[]);
            uv1=new(previous.ZeroUvOwner);
        }
        else if(uv1.Length!=vertexCount||stage1)
        {
            if(!EnbW3dProgrammableShaderLaneCompiler.TryCompileStageUv(submission.Packet.Material.Source,1,vertexCount,out var coordinates,out diagnostic))return false;
            uv1=coordinates;
        }
        if(!EnbW3dProgrammableLightingLaneCompiler.TryUpdateParticleFrame(previous.LightingPlan,submission,camera,out var lighting,out diagnostic))return false;
        var runs=previous.RunStates;
        if(!previous.LightingPlan.RunStates.Equals(lighting.RunStates))
        {
            var builder=ImmutableArray.CreateBuilder<EnbW3dProgrammableTextureRunState>(runs.Length);
            for(int i=0;i<runs.Length;i++)builder.Add(ReferenceEquals(runs[i].LightingRun,lighting.RunStates[i])
                ?runs[i]:runs[i] with {LightingRun=lighting.RunStates[i]});
            runs=builder.MoveToImmutable();
        }
        plan=(submission.Packet.Mesh.RetainsParticleFrame?previous:previous.Copy()).SetLightingPlan(lighting).SetRunStates(runs).SetTextureCoordinates1(uv1).SetCanonicalCameraProjection(camera.Projection.Matrix).SetRetailGeneratedCoordinateViewFromWorld(EnbW3dGeneratedTextureCoordinates.CreateRetailCameraViewFromWorld(camera));
        return true;
    }

    public static bool TryCompile(
        EnbW3dIndexedSubmissionPlan submission,
        EnbResolvedRenderCamera camera,
        EnbRenderTextureRuntimeSettings runtimeSettings,
        EnbRenderTextureBackendCapabilities backendCapabilities,
        Func<EnbW3dRenderTextureBinding, EnbTextureImage?> imageLookup,
        out EnbW3dProgrammableTextureLanePlan plan,
        out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(submission.Packet);
        ArgumentNullException.ThrowIfNull(imageLookup);
        plan = default!;
        diagnostic = null;

        if (submission.Runs.IsDefaultOrEmpty)
        {
            diagnostic = $"Mesh '{submission.Packet.Mesh.Name}' has no R6A submission runs.";
            return false;
        }

        byte textureStageCount = submission.Runs[0].Binding.Semantics.PermutationKey.TextureStageCount;
        if (textureStageCount == 0)
            return TryCompileUntextured(submission,camera,runtimeSettings,backendCapabilities,out plan,out diagnostic);
        if (textureStageCount is not (1 or 2))
        {
            diagnostic = $"R6G texture lane requires one or two W3D texture stages, got {textureStageCount}.";
            return false;
        }

        runtimeSettings = EnbRenderTextureRuntimeSettings.FromRetailValues(
            runtimeSettings.TextureReduction,
            (int)runtimeSettings.TextureFilter);

        if (!EnbW3dProgrammableLightingLaneCompiler.TryCompileCore(
                submission,
                camera,
                requiredTextureStageCount: textureStageCount,
                laneName: textureStageCount == 2 ? "R6G" : "R6F",
                allowTextureMapperPrograms: true,
                allowPerRunRasterState: true,
                requireLightingEnabled: false,
                out EnbW3dProgrammableLightingLanePlan lightingPlan,
                out diagnostic))
        {
            return false;
        }

        EnbW3dUvStream uv1;
        EnbW3dUvOwner? zeroUvOwner=null;
        if (textureStageCount == 2)
        {
            if (!EnbW3dProgrammableShaderLaneCompiler.TryCompileStageUv(
                    submission.Packet.Material.Source,
                    stageOrdinal: 1,
                    lightingPlan.VertexCount,
                    out var compiledUv1,
                    out diagnostic))
            {
                return false;
            }
            uv1=compiledUv1;
        }
        else if(submission.Packet.Mesh.RetainsParticleFrame)
        {
            zeroUvOwner=new();zeroUvOwner.Prepare(lightingPlan.VertexCount,0,[]);uv1=new(zeroUvOwner);
        }
        else
        {
            uv1 = ImmutableArray.CreateRange(Enumerable.Repeat(Vector2.Zero, lightingPlan.VertexCount));
        }

        var runStates = ImmutableArray.CreateBuilder<EnbW3dProgrammableTextureRunState>(submission.Runs.Length);
        for (int i = 0; i < submission.Runs.Length; i++)
        {
            EnbW3dIndexedSubmissionRun run = submission.Runs[i];
            EnbW3dCompiledMaterialSemantics semantics = run.Binding.Semantics;
            EnbW3dTextureCombinerSemantics combiners = semantics.FixedFunction.TextureCombinerSemantics;
            //00975480/0094C040 bind NULL explicitly. The D3D cascade
            //terminates before either stage's combiner is evaluated, including
            //an authored DISABLE program. Preserve its metadata for auditing.
            if(run.Binding.Textures.Length==textureStageCount &&
                run.Binding.Textures[0] is not {IsUnbound:false} &&
                combiners.Stage0.Color.Argument1==EnbW3dTextureArgument.Texture)
            {
                runStates.Add(new(lightingPlan.RunStates[i],new(uint.MaxValue,string.Empty,null),
                    null,default,default,combiners.Stage0,EnbW3dProgrammableStage0ProgramKind.PassDiffuse)
                {
                    Stage0TerminatedByNullTextureCascade=true,Stage1Combiner=combiners.Stage1,
                    Stage1Program=EnbW3dProgrammableStage1Program.Disabled,
                    Mapper0Program=semantics.Mapper0Program,Mapper1Program=semantics.Mapper1Program,
                });
                continue;
            }
            if (!TryClassifyStage0Program(
                    semantics.FixedFunction.TextureCombinerSemantics.Stage0,
                    out EnbW3dProgrammableStage0ProgramKind stage0Program,
                    out diagnostic))
            {
                diagnostic = $"Mesh '{submission.Packet.Mesh.Name}' run at triangle {run.FirstTriangleOrdinal}: {diagnostic}";
                return false;
            }

            if (!TryClassifyNonBumpStage1Program(combiners.Stage1, out EnbW3dProgrammableStage1Program stage1Program, out diagnostic))
            {
                diagnostic = $"Mesh '{submission.Packet.Mesh.Name}' run at triangle {run.FirstTriangleOrdinal}: {diagnostic}";
                return false;
            }

            bool bumpStage0 = stage0Program is EnbW3dProgrammableStage0ProgramKind.BumpEnvironmentMap
                or EnbW3dProgrammableStage0ProgramKind.BumpEnvironmentMapLuminance;
            EnbW3dMapperRuntimeProgram mapper0Program = semantics.Mapper0Program;
            EnbW3dMapperRuntimeProgram mapper1Program = semantics.Mapper1Program;
            if (!IsR6lRealizedMapper(mapper0Program))
            {
                diagnostic = $"Mesh '{submission.Packet.Mesh.Name}' run at triangle {run.FirstTriangleOrdinal}: R6L mapper slice does not realize mapper 0 {DescribeMapper(mapper0Program)}.";
                return false;
            }
            if (!IsR6lRealizedMapper(mapper1Program))
            {
                diagnostic = $"Mesh '{submission.Packet.Mesh.Name}' run at triangle {run.FirstTriangleOrdinal}: R6L mapper slice does not realize mapper 1 {DescribeMapper(mapper1Program)}.";
                return false;
            }
            //00939300 uses mapper+8 for all texture and bump states. A non-bump
            //combiner ignores its bump matrix but still consumes the linear UV transform.
            if (run.Binding.Textures.Length != textureStageCount)
            {
                diagnostic = $"Mesh '{submission.Packet.Mesh.Name}' run at triangle {run.FirstTriangleOrdinal} must resolve exactly {textureStageCount} texture binding(s) with a real stage-0 texture for R6G.";
                return false;
            }

            var texture0 = run.Binding.Textures[0] ?? new EnbW3dRenderTextureBinding(uint.MaxValue, string.Empty, null);

            EnbW3dRenderTextureBinding? texture1Candidate = textureStageCount == 2
                ? run.Binding.Textures[1]
                : null;
            bool hasBoundStage1Texture = texture1Candidate is { IsUnbound: false };

            // Current retail resolves/binds stage 1 independently. A missing/unbound stage-1
            // pointer is installed as NULL without rewriting the authored fixed-function
            // combiner. In the legacy Direct3D texture cascade, COLORARG1=TEXTURE with a
            // NULL texture terminates that stage and every following stage. Preserve the
            // authored combiner for diagnostics, but compile the effective backend program
            // as disabled when that exact condition is met.
            if (!TryResolveD3d8NullTextureCascade(
                    combiners.Stage1,
                    stage1Program,
                    hasBoundStage1Texture,
                    out EnbW3dProgrammableStage1Program effectiveStage1Program,
                    out bool stage1TerminatedByNullTextureCascade,
                    out diagnostic))
            {
                diagnostic = $"Mesh '{submission.Packet.Mesh.Name}' run at triangle {run.FirstTriangleOrdinal}: {diagnostic}";
                return false;
            }

            if (bumpStage0 &&
                (textureStageCount != 2 || !stage1Program.Enabled || !hasBoundStage1Texture))
            {
                diagnostic = $"Mesh '{submission.Packet.Mesh.Name}' run at triangle {run.FirstTriangleOrdinal}: current-retail stage-0 bump mapping requires a real following texture/blending stage.";
                return false;
            }

            bool samplesTexture0 = stage0Program != EnbW3dProgrammableStage0ProgramKind.PassDiffuse;
            EnbTextureImage? image0 = null;
            EnbTextureUploadMipPlan upload0 = default;
            EnbW3dTextureSamplerSemantics sampler0 = default;
            if (samplesTexture0 &&
                !TryResolveTexture(texture0, runtimeSettings, backendCapabilities, imageLookup,
                    out image0, out upload0, out sampler0, out diagnostic))
            {
                diagnostic = $"Mesh '{submission.Packet.Mesh.Name}' run at triangle {run.FirstTriangleOrdinal}: {diagnostic}";
                return false;
            }

            EnbW3dBumpSemanticTexture? bumpTexture0 = null;
            if (bumpStage0)
            {
                if (!texture0.Sampling.LegacyCapabilityFormatRequested)
                {
                    diagnostic = $"Mesh '{submission.Packet.Mesh.Name}' run at triangle {run.FirstTriangleOrdinal}: stage-0 bump program requires the current-retail TextureInfo 0x1000 capability-format request.";
                    return false;
                }
                if (!EnbW3dBumpTextureCompiler.TryCompile(image0!, upload0, out bumpTexture0, out diagnostic))
                {
                    diagnostic = $"Mesh '{submission.Packet.Mesh.Name}' run at triangle {run.FirstTriangleOrdinal}: {diagnostic}";
                    return false;
                }
            }

            var runState = new EnbW3dProgrammableTextureRunState(
                lightingPlan.RunStates[i],
                texture0,
                image0,
                upload0,
                sampler0,
                combiners.Stage0,
                stage0Program)
            {
                Stage1Combiner = combiners.Stage1,
                Stage1Program = effectiveStage1Program,
                Stage1TerminatedByNullTextureCascade = stage1TerminatedByNullTextureCascade,
                BumpEnvironment = combiners.BumpEnvironment,
                BumpTexture0 = bumpTexture0,
                Mapper0Program = mapper0Program,
                Mapper1Program = mapper1Program,
            };

            if (effectiveStage1Program.Enabled)
            {
                if (texture1Candidate is not EnbW3dRenderTextureBinding texture1 || texture1.IsUnbound)
                {
                    diagnostic = $"Mesh '{submission.Packet.Mesh.Name}' run at triangle {run.FirstTriangleOrdinal} has no bound stage-1 texture for its active two-stage R6G material.";
                    return false;
                }

                if (!TryResolveTexture(texture1, runtimeSettings, backendCapabilities, imageLookup,
                        out EnbTextureImage image1, out EnbTextureUploadMipPlan upload1, out EnbW3dTextureSamplerSemantics sampler1, out diagnostic))
                {
                    diagnostic = $"Mesh '{submission.Packet.Mesh.Name}' run at triangle {run.FirstTriangleOrdinal}: stage 1: {diagnostic}";
                    return false;
                }

                runState = runState with
                {
                    Texture1 = texture1,
                    Image1 = image1,
                    UploadMipPlan1 = upload1,
                    Sampler1 = sampler1,
                };
            }

            runStates.Add(runState);
        }

        plan = new EnbW3dProgrammableTextureLanePlan(
            lightingPlan,
            runStates.ToImmutable(),
            runtimeSettings,
            backendCapabilities)
        {
            TextureCoordinates1 = uv1,
            ZeroUvOwner = zeroUvOwner,
            CanonicalCameraProjection = camera.Projection.Matrix,
            RetailGeneratedCoordinateViewFromWorld = EnbW3dGeneratedTextureCoordinates.CreateRetailCameraViewFromWorld(camera),
        };
        return true;
    }

    private static bool TryCompileUntextured(EnbW3dIndexedSubmissionPlan submission, EnbResolvedRenderCamera camera,
        EnbRenderTextureRuntimeSettings settings, EnbRenderTextureBackendCapabilities capabilities,
        out EnbW3dProgrammableTextureLanePlan plan, out string? diagnostic)
    {
        plan=default!;
        if (!EnbW3dProgrammableLightingLaneCompiler.TryCompileCore(submission,camera,0,"untextured GPU",
                // Authored materials can retain mapper metadata without any
                // texture stage. The strict pass-diffuse checks below establish
                // that texture coordinates cannot contribute to this draw.
                allowTextureMapperPrograms:true,allowPerRunRasterState:true,requireLightingEnabled:false,
                out var lighting,out diagnostic)) return false;
        var states=ImmutableArray.CreateBuilder<EnbW3dProgrammableTextureRunState>(submission.Runs.Length);
        for(int i=0;i<submission.Runs.Length;i++)
        {
            var run=submission.Runs[i];
            var combiners=run.Binding.Semantics.FixedFunction.TextureCombinerSemantics;
            if(run.Binding.Textures.Length!=0 ||
                !TryClassifyStage0Program(combiners.Stage0,out var stage0,out diagnostic) ||
                stage0!=EnbW3dProgrammableStage0ProgramKind.PassDiffuse ||
                !TryClassifyNonBumpStage1Program(combiners.Stage1,out var stage1,out diagnostic) || stage1.Enabled)
            { diagnostic="Zero-stage GPU material must pass diffuse with no texture bindings or active stage 1."; return false; }
            // The backend descriptor uses the existing explicit unbound token.
            // No W3D stage, image, lookup, sampler or GPU texture is fabricated.
            states.Add(new(lighting.RunStates[i],new(uint.MaxValue,"",null),null,default,default,
                combiners.Stage0,stage0) { Stage1Combiner=combiners.Stage1, Stage1Program=stage1 });
        }
        plan=new(lighting,states.MoveToImmutable(),settings,capabilities)
        {
            TextureCoordinates1=ImmutableArray.CreateRange(Enumerable.Repeat(Vector2.Zero,lighting.VertexCount)),
            CanonicalCameraProjection = camera.Projection.Matrix,
            RetailGeneratedCoordinateViewFromWorld=EnbW3dGeneratedTextureCoordinates.CreateRetailCameraViewFromWorld(camera),
        };
        diagnostic=null; return true;
    }


    private static bool IsR6lRealizedMapper(EnbW3dMapperRuntimeProgram program)
        => program.Kind is
            EnbW3dMapperRuntimeProgramKind.Disabled or
            EnbW3dMapperRuntimeProgramKind.FixedHalfBias or
            EnbW3dMapperRuntimeProgramKind.ProjectedLinear or
            EnbW3dMapperRuntimeProgramKind.Linear or
            EnbW3dMapperRuntimeProgramKind.StaticScale or
            EnbW3dMapperRuntimeProgramKind.GridAtlas or
            EnbW3dMapperRuntimeProgramKind.CenteredRotation or
            EnbW3dMapperRuntimeProgramKind.SinusoidalOffset or
            EnbW3dMapperRuntimeProgramKind.SteppedOffset or
            EnbW3dMapperRuntimeProgramKind.PingPongOffset or
            EnbW3dMapperRuntimeProgramKind.AxisProjection or
            EnbW3dMapperRuntimeProgramKind.Randomized or
            EnbW3dMapperRuntimeProgramKind.VerticalEnvironmentScroll or
            EnbW3dMapperRuntimeProgramKind.BumpEnvironment or
            EnbW3dMapperRuntimeProgramKind.GridAtlasAxisProjection;

    private static string DescribeMapper(EnbW3dMapperRuntimeProgram program)
        => $"kind={program.Kind}, source={program.CoordinateSource}, shape={program.TransformShape}, view={program.RequiresViewTransform}, random={program.RequiresRandomSource}";

    public static bool TryClassifyStage0Program(
        EnbW3dTextureStageSemantics stage0,
        out EnbW3dProgrammableStage0ProgramKind program,
        out string? diagnostic)
    {
        diagnostic = null;

        var bump = new EnbW3dTextureStageSemantics(
            Channel(EnbW3dTextureOperation.BumpEnvironmentMap, EnbW3dTextureArgument.Texture, EnbW3dTextureArgument.Diffuse),
            Channel(EnbW3dTextureOperation.Disabled, EnbW3dTextureArgument.Texture, EnbW3dTextureArgument.Current));
        var bumpLuminance = new EnbW3dTextureStageSemantics(
            Channel(EnbW3dTextureOperation.BumpEnvironmentMapLuminance, EnbW3dTextureArgument.Texture, EnbW3dTextureArgument.Diffuse),
            Channel(EnbW3dTextureOperation.Disabled, EnbW3dTextureArgument.Texture, EnbW3dTextureArgument.Current));

        if (stage0 == bump) { program = EnbW3dProgrammableStage0ProgramKind.BumpEnvironmentMap; return true; }
        if (stage0 == bumpLuminance) { program = EnbW3dProgrammableStage0ProgramKind.BumpEnvironmentMapLuminance; return true; }
        return TryClassifyNonBumpStage0Program(stage0, out program, out diagnostic);
    }

    public static bool TryClassifyNonBumpStage0Program(
        EnbW3dTextureStageSemantics stage0,
        out EnbW3dProgrammableStage0ProgramKind program,
        out string? diagnostic)
    {
        diagnostic = null;

        var selectTexture = new EnbW3dTextureStageSemantics(
            Channel(EnbW3dTextureOperation.SelectArgument1, EnbW3dTextureArgument.Texture, EnbW3dTextureArgument.Current),
            Channel(EnbW3dTextureOperation.SelectArgument1, EnbW3dTextureArgument.Texture, EnbW3dTextureArgument.Current));
        var modulate = new EnbW3dTextureStageSemantics(
            Channel(EnbW3dTextureOperation.Modulate, EnbW3dTextureArgument.Texture, EnbW3dTextureArgument.Diffuse),
            Channel(EnbW3dTextureOperation.Modulate, EnbW3dTextureArgument.Texture, EnbW3dTextureArgument.Diffuse));
        var addColor = new EnbW3dTextureStageSemantics(
            Channel(EnbW3dTextureOperation.Add, EnbW3dTextureArgument.Texture, EnbW3dTextureArgument.Diffuse),
            Channel(EnbW3dTextureOperation.Modulate, EnbW3dTextureArgument.Texture, EnbW3dTextureArgument.Diffuse));
        var modulate2XColor = new EnbW3dTextureStageSemantics(
            Channel(EnbW3dTextureOperation.Modulate2X, EnbW3dTextureArgument.Texture, EnbW3dTextureArgument.Diffuse),
            Channel(EnbW3dTextureOperation.Modulate, EnbW3dTextureArgument.Texture, EnbW3dTextureArgument.Diffuse));
        var passDiffuse = new EnbW3dTextureStageSemantics(
            Channel(EnbW3dTextureOperation.SelectArgument2, EnbW3dTextureArgument.Texture, EnbW3dTextureArgument.Diffuse),
            Channel(EnbW3dTextureOperation.SelectArgument2, EnbW3dTextureArgument.Texture, EnbW3dTextureArgument.Diffuse));

        if (stage0 == selectTexture) { program = EnbW3dProgrammableStage0ProgramKind.SelectTexture; return true; }
        if (stage0 == modulate) { program = EnbW3dProgrammableStage0ProgramKind.ModulateTextureDiffuse; return true; }
        if (stage0 == addColor) { program = EnbW3dProgrammableStage0ProgramKind.AddTextureDiffuseColorModulateAlpha; return true; }
        if (stage0 == modulate2XColor) { program = EnbW3dProgrammableStage0ProgramKind.Modulate2XTextureDiffuseColorModulateAlpha; return true; }
        if (stage0 == passDiffuse) { program = EnbW3dProgrammableStage0ProgramKind.PassDiffuse; return true; }

        program = default;
        diagnostic = stage0.Color.Operation is EnbW3dTextureOperation.BumpEnvironmentMap or EnbW3dTextureOperation.BumpEnvironmentMapLuminance
            ? "R6G non-bump classifier excludes the R6H current-retail stage-0 bump-environment programs by design."
            : $"R6H does not recognize current-retail stage-0 program color={stage0.Color.Operation} alpha={stage0.Alpha.Operation}.";
        return false;
    }

    public static bool TryClassifyNonBumpStage1Program(
        EnbW3dTextureStageSemantics stage1,
        out EnbW3dProgrammableStage1Program program,
        out string? diagnostic)
    {
        diagnostic = null;
        if (!TryClassifyStage1Color(stage1.Color, out EnbW3dProgrammableStage1ColorProgramKind color) ||
            !TryClassifyStage1Alpha(stage1.Alpha, out EnbW3dProgrammableStage1AlphaProgramKind alpha))
        {
            program = default;
            diagnostic = $"R6G does not recognize current-retail stage-1 program color={stage1.Color.Operation}/{stage1.Color.Argument1}/{stage1.Color.Argument2} alpha={stage1.Alpha.Operation}/{stage1.Alpha.Argument1}/{stage1.Alpha.Argument2}.";
            return false;
        }

        bool colorDisabled = color == EnbW3dProgrammableStage1ColorProgramKind.Disabled;
        bool alphaDisabled = alpha == EnbW3dProgrammableStage1AlphaProgramKind.Disabled;
        if (colorDisabled != alphaDisabled)
        {
            program = default;
            diagnostic = "R6G received a half-disabled stage-1 program that was not normalized to explicit CURRENT pass-through by R4E.";
            return false;
        }

        program = new EnbW3dProgrammableStage1Program(color, alpha);
        return true;
    }

    private static bool TryClassifyStage1Color(EnbW3dTextureChannelSemantics channel, out EnbW3dProgrammableStage1ColorProgramKind program)
    {
        program = channel switch
        {
            { Operation: EnbW3dTextureOperation.Disabled, Argument1: EnbW3dTextureArgument.Texture, Argument2: EnbW3dTextureArgument.Current } => EnbW3dProgrammableStage1ColorProgramKind.Disabled,
            { Operation: EnbW3dTextureOperation.SelectArgument2, Argument1: EnbW3dTextureArgument.Texture, Argument2: EnbW3dTextureArgument.Current } => EnbW3dProgrammableStage1ColorProgramKind.PassCurrent,
            { Operation: EnbW3dTextureOperation.SelectArgument1, Argument1: EnbW3dTextureArgument.Texture, Argument2: EnbW3dTextureArgument.Current } => EnbW3dProgrammableStage1ColorProgramKind.SelectTexture,
            { Operation: EnbW3dTextureOperation.Modulate, Argument1: EnbW3dTextureArgument.Texture, Argument2: EnbW3dTextureArgument.Current } => EnbW3dProgrammableStage1ColorProgramKind.ModulateTextureCurrent,
            { Operation: EnbW3dTextureOperation.AddSmooth, Argument1: EnbW3dTextureArgument.Texture, Argument2: EnbW3dTextureArgument.Current } => EnbW3dProgrammableStage1ColorProgramKind.AddSmoothTextureCurrent,
            { Operation: EnbW3dTextureOperation.Add, Argument1: EnbW3dTextureArgument.Texture, Argument2: EnbW3dTextureArgument.Current } => EnbW3dProgrammableStage1ColorProgramKind.AddTextureCurrent,
            { Operation: EnbW3dTextureOperation.Subtract, Argument1: EnbW3dTextureArgument.Texture, Argument2: EnbW3dTextureArgument.Current } => EnbW3dProgrammableStage1ColorProgramKind.SubtractTextureCurrent,
            { Operation: EnbW3dTextureOperation.Subtract, Argument1: EnbW3dTextureArgument.Current, Argument2: EnbW3dTextureArgument.Texture } => EnbW3dProgrammableStage1ColorProgramKind.SubtractCurrentTexture,
            { Operation: EnbW3dTextureOperation.BlendTextureAlpha, Argument1: EnbW3dTextureArgument.Texture, Argument2: EnbW3dTextureArgument.Current } => EnbW3dProgrammableStage1ColorProgramKind.BlendTextureAlpha,
            { Operation: EnbW3dTextureOperation.BlendCurrentAlpha, Argument1: EnbW3dTextureArgument.Texture, Argument2: EnbW3dTextureArgument.Current } => EnbW3dProgrammableStage1ColorProgramKind.BlendCurrentAlpha,
            { Operation: EnbW3dTextureOperation.AddSigned, Argument1: EnbW3dTextureArgument.Texture, Argument2: EnbW3dTextureArgument.Current } => EnbW3dProgrammableStage1ColorProgramKind.AddSignedTextureCurrent,
            { Operation: EnbW3dTextureOperation.AddSigned2X, Argument1: EnbW3dTextureArgument.Texture, Argument2: EnbW3dTextureArgument.Current } => EnbW3dProgrammableStage1ColorProgramKind.AddSigned2XTextureCurrent,
            { Operation: EnbW3dTextureOperation.Modulate2X, Argument1: EnbW3dTextureArgument.Texture, Argument2: EnbW3dTextureArgument.Current } => EnbW3dProgrammableStage1ColorProgramKind.Modulate2XTextureCurrent,
            { Operation: EnbW3dTextureOperation.ModulateAlphaAddColor, Argument1: EnbW3dTextureArgument.Current, Argument2: EnbW3dTextureArgument.Texture } => EnbW3dProgrammableStage1ColorProgramKind.ModulateCurrentAlphaAddTextureColor,
            _ => (EnbW3dProgrammableStage1ColorProgramKind)byte.MaxValue,
        };
        return (byte)program != byte.MaxValue;
    }

    private static bool TryClassifyStage1Alpha(EnbW3dTextureChannelSemantics channel, out EnbW3dProgrammableStage1AlphaProgramKind program)
    {
        program = channel switch
        {
            { Operation: EnbW3dTextureOperation.Disabled, Argument1: EnbW3dTextureArgument.Texture, Argument2: EnbW3dTextureArgument.Current } => EnbW3dProgrammableStage1AlphaProgramKind.Disabled,
            { Operation: EnbW3dTextureOperation.SelectArgument2, Argument1: EnbW3dTextureArgument.Texture, Argument2: EnbW3dTextureArgument.Current } => EnbW3dProgrammableStage1AlphaProgramKind.PassCurrent,
            { Operation: EnbW3dTextureOperation.SelectArgument1, Argument1: EnbW3dTextureArgument.Texture, Argument2: EnbW3dTextureArgument.Current } => EnbW3dProgrammableStage1AlphaProgramKind.SelectTexture,
            { Operation: EnbW3dTextureOperation.Modulate, Argument1: EnbW3dTextureArgument.Texture, Argument2: EnbW3dTextureArgument.Current } => EnbW3dProgrammableStage1AlphaProgramKind.ModulateTextureCurrent,
            { Operation: EnbW3dTextureOperation.AddSmooth, Argument1: EnbW3dTextureArgument.Texture, Argument2: EnbW3dTextureArgument.Current } => EnbW3dProgrammableStage1AlphaProgramKind.AddSmoothTextureCurrent,
            _ => (EnbW3dProgrammableStage1AlphaProgramKind)byte.MaxValue,
        };
        return (byte)program != byte.MaxValue;
    }

    /// <summary>
    /// Resolves the current-retail NULL-texture fixed-function cascade boundary.
    /// Current client.exe keeps the authored stage-1 combiner installed while binding
    /// a NULL texture independently. Legacy Direct3D terminates that stage (and later
    /// stages) when COLORARG1 is TEXTURE and the stage texture is NULL.
    /// </summary>
    public static bool TryResolveD3d8NullTextureCascade(
        EnbW3dTextureStageSemantics stage1,
        EnbW3dProgrammableStage1Program authoredProgram,
        bool hasBoundTexture,
        out EnbW3dProgrammableStage1Program effectiveProgram,
        out bool terminated,
        out string? diagnostic)
    {
        effectiveProgram = authoredProgram;
        terminated = false;
        diagnostic = null;

        if (!authoredProgram.Enabled || hasBoundTexture)
            return true;

        if (stage1.Color.Argument1 == EnbW3dTextureArgument.Texture)
        {
            effectiveProgram = EnbW3dProgrammableStage1Program.Disabled;
            terminated = true;
            return true;
        }

        diagnostic =
            $"active stage 1 has no bound texture, but the closed current-retail NULL-texture cascade only applies when COLORARG1=TEXTURE; got {stage1.Color.Argument1}.";
        return false;
    }

    private static bool TryResolveTexture(
        EnbW3dRenderTextureBinding texture,
        EnbRenderTextureRuntimeSettings runtimeSettings,
        EnbRenderTextureBackendCapabilities backendCapabilities,
        Func<EnbW3dRenderTextureBinding, EnbTextureImage?> imageLookup,
        out EnbTextureImage image,
        out EnbTextureUploadMipPlan uploadMipPlan,
        out EnbW3dTextureSamplerSemantics sampler,
        out string? diagnostic)
    {
        image = default!;
        uploadMipPlan = default;
        sampler = default;
        diagnostic = null;
        if (texture.IsUnbound)
        {
            diagnostic = "uses the retail per-triangle unbound texture sentinel; R6G requires a real image for every active texture stage.";
            return false;
        }
        if (texture.IsMissing)
        {
            diagnostic = $"references missing texture '{texture.LogicalName}'.";
            return false;
        }

        EnbTextureImage? resolved = texture.CurrentRetailResolvedTexture?.Image ?? imageLookup(texture);
        if (resolved is null)
        {
            diagnostic = $"has no decoded image for texture '{texture.LogicalName}'.";
            return false;
        }
        image = resolved;
        // Named emitter/line definitions retain the texture's runtime key rather
        // than a rendering-layer pointer. Recover its recolored owner after the
        // image lookup; an explicit descriptor owner always takes precedence.
        if(texture.CurrentRetailTextureState is null &&
            EnbCurrentRetailEffectMeshHsvRuntime.GetRecoloredImageState(image) is {} retainedTextureState)
            texture=texture with {CurrentRetailTextureState=retainedTextureState};
        sampler = EnbW3dTextureSamplerCompiler.Compile(texture, runtimeSettings, backendCapabilities);
        try
        {
            uploadMipPlan = EnbTextureUploadMipPlanner.Compile(image, runtimeSettings,
                retailCreationSelector:texture.Sampling.RetailCreationSelector,
                retailCreationMipLevels:texture.CurrentRetailTextureState?.RetailCreationMipLevels);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException)
        {
            diagnostic = $"Texture '{texture.LogicalName}' cannot produce the current-retail upload mip plan: {ex.Message}";
            return false;
        }
        return true;
    }

    private static EnbW3dTextureChannelSemantics Channel(
        EnbW3dTextureOperation operation,
        EnbW3dTextureArgument argument1,
        EnbW3dTextureArgument argument2)
        => new(operation, argument1, argument2);
}
