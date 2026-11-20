using System.Diagnostics;
using System.Buffers;
using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Rendering.OpenGL;

public sealed record EnbOpenGlStage0TextureMipUpload(
    int Level,
    int Width,
    int Height,
    ReadOnlyMemory<EnbColor32> Pixels)
{
    // The immutable decoded image is the recovery source. Upload directly from
    // its RGBA layout instead of retaining a second byte-for-byte CPU image.
    internal EnbTextureMipLevel? SourceMip { get; init; }
    public int RgbaByteLength => checked(Width * Height * 4);
    public ReadOnlySpan<byte> RgbaBytes => MemoryMarshal.AsBytes(SourceMip is {} source ? source.Pixels.Span : Pixels.Span);
    public ImmutableArray<byte> PackedBytes { get; init; } = [];
    public ReadOnlyMemory<byte> CompressedBlocks { get; init; }
    public int GlInternalFormat { get; init; } = 0x1908;
    public int GlPixelFormat { get; init; } = 0x1908;
    public int GlPixelType { get; init; } = 0x1401;
}

public sealed record EnbOpenGlStage0TextureUploadPlan(
    EnbW3dProgrammableTextureRunState RunState,
    ImmutableArray<EnbOpenGlStage0TextureMipUpload> Mips,
    int GlWrapS,
    int GlWrapT,
    int GlMinFilter,
    int GlMagFilter,
    float MaximumAnisotropy,
    bool RequiresExplicitMaximumMipLevel)
{
    public int MaximumMipLevel => Mips.Length - 1;
}

public sealed record EnbOpenGlTextureStageUploadPlan(
    int StageOrdinal,
    EnbW3dRenderTextureBinding Texture,
    EnbTextureImage Image,
    EnbTextureUploadMipPlan UploadMipPlan,
    EnbW3dTextureSamplerSemantics Sampler,
    ImmutableArray<EnbOpenGlStage0TextureMipUpload> Mips,
    int GlWrapS,
    int GlWrapT,
    int GlMinFilter,
    int GlMagFilter,
    float MaximumAnisotropy,
    bool RequiresExplicitMaximumMipLevel)
{
    public int MaximumMipLevel => Mips.Length - 1;
}

public sealed record EnbOpenGlStage0TextureMeshResource(
    int VertexBuffer,
    int TextureCoordinate1Buffer,
    int IndexBuffer,
    int VertexArray,
    EnbOpenGlLitShaderUploadPlan GeometryUploadPlan,
    ImmutableArray<int> TextureIdsByRun,
    ImmutableArray<EnbOpenGlStage0TextureUploadPlan> TextureUploadPlans,
    ImmutableArray<int> Texture1IdsByRun,
    ImmutableArray<EnbOpenGlTextureStageUploadPlan> Texture1UploadPlans,
    ImmutableArray<EnbW3dMapperRuntimeState> Mapper0RuntimeStates,
    ImmutableArray<EnbW3dMapperRuntimeState> Mapper1RuntimeStates,
    EnbW3dProgrammableTextureLanePlan LanePlan)
{
    // GPU resources retain the latest submission across invocation drains,
    // including streaming updates made with a record initializer.
    private EnbW3dProgrammableTextureLanePlan _lanePlan=RetainLane(LanePlan);
    public EnbW3dProgrammableTextureLanePlan LanePlan
    {get=>_lanePlan;init=>_lanePlan=RetainLane(value);}
    private EnbOpenGlLitShaderUploadPlan _geometryUploadPlan=RetainGeometry(GeometryUploadPlan);
    public EnbOpenGlLitShaderUploadPlan GeometryUploadPlan
    {get=>_geometryUploadPlan;init=>_geometryUploadPlan=RetainGeometry(value);}
    private static EnbW3dProgrammableTextureLanePlan RetainLane(EnbW3dProgrammableTextureLanePlan lane)
    {lane.Submission.Packet.RetainAfterDrain();return lane;}
    private static EnbOpenGlLitShaderUploadPlan RetainGeometry(EnbOpenGlLitShaderUploadPlan plan)
    {plan.LanePlan.Submission.Packet.RetainAfterDrain();return plan;}
    // Last uploaded UV range is distinct from the current mutable particle lane.
    public EnbW3dUvStream UploadedTextureCoordinates1 {get;init;}=LanePlan.TextureCoordinates1;
    public ImmutableArray<int> NullMaterialCoordinateBuffers { get; init; } = [];
    internal EnbOpenGlSharedGeometry? SharedGeometry { get; init; }
    internal EnbOpenGlSkinUploadScratch? SkinUploadScratch { get; init; }
    public EnbW3dNPatchGeometry? NPatchGeometry { get; init; }
}

/// <summary>CPU-only storage retained across device release; contains no GL names.</summary>
public sealed record EnbOpenGlTextureMeshRecoveryState(
    ImmutableArray<EnbW3dSharedMaterialMappers> Mappers,
    ImmutableArray<EnbOpenGlStage0TextureUploadPlan> Textures0,
    ImmutableArray<EnbOpenGlTextureStageUploadPlan> Textures1)
{
    public static EnbOpenGlTextureMeshRecoveryState Retain(EnbOpenGlStage0TextureMeshResource resource)
        => new(resource.Mapper0RuntimeStates.Zip(resource.Mapper1RuntimeStates,
            static (stage0,stage1)=>new EnbW3dSharedMaterialMappers(stage0,stage1)).ToImmutableArray(),
            resource.TextureUploadPlans,resource.Texture1UploadPlans);
}

public static class EnbOpenGlStage0TextureUploadPlanner
{
    private const int GlRepeat = 0x2901;
    private const int GlClampToEdge = 0x812F;
    private const int GlNearest = 0x2600;
    private const int GlLinear = 0x2601;
    private const int GlNearestMipmapNearest = 0x2700;
    private const int GlLinearMipmapNearest = 0x2701;
    private const int GlNearestMipmapLinear = 0x2702;
    private const int GlLinearMipmapLinear = 0x2703;

    public static bool TryPlan(
        EnbW3dProgrammableTextureRunState runState,
        out EnbOpenGlStage0TextureUploadPlan plan,
        out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(runState);
        if (!runState.SamplesTexture0)
        {
            diagnostic = null;
            plan = new EnbOpenGlStage0TextureUploadPlan(
                runState,
                ImmutableArray<EnbOpenGlStage0TextureMipUpload>.Empty,
                0, 0, 0, 0, 0f, false);
            return true;
        }

        if (runState.BumpTexture0 is EnbW3dBumpSemanticTexture bumpTexture)
            return TryPlanBumpStage0(runState, bumpTexture, out plan, out diagnostic);

        if (runState.Image0 is not EnbTextureImage image0)
        {
            diagnostic = $"Texture '{runState.Texture0.LogicalName}' has no decoded stage-0 image for a program that samples stage 0.";
            plan = default!;
            return false;
        }

        if (!TryPlanStage(0, runState.Texture0, image0, runState.UploadMipPlan, runState.Sampler, out EnbOpenGlTextureStageUploadPlan generic, out diagnostic))
        {
            plan = default!;
            return false;
        }

        plan = new EnbOpenGlStage0TextureUploadPlan(
            runState, generic.Mips, generic.GlWrapS, generic.GlWrapT, generic.GlMinFilter, generic.GlMagFilter, generic.MaximumAnisotropy, generic.RequiresExplicitMaximumMipLevel);
        return true;
    }

    private static bool TryPlanBumpStage0(
        EnbW3dProgrammableTextureRunState runState,
        EnbW3dBumpSemanticTexture bumpTexture,
        out EnbOpenGlStage0TextureUploadPlan plan,
        out string? diagnostic)
    {
        plan = default!;
        diagnostic = null;
        if (bumpTexture.Mips.Length != runState.UploadMipPlan.UploadMipCount || bumpTexture.Mips.IsDefaultOrEmpty)
        {
            diagnostic = $"Texture '{runState.Texture0.LogicalName}' bump semantic mip count no longer matches its R4J upload plan.";
            return false;
        }

        var mips = ImmutableArray.CreateBuilder<EnbOpenGlStage0TextureMipUpload>(bumpTexture.Mips.Length);
        for (int uploadLevel = 0; uploadLevel < bumpTexture.Mips.Length; uploadLevel++)
        {
            EnbW3dBumpSemanticMip source = bumpTexture.Mips[uploadLevel];
            if (source.SourceMipIndex != runState.UploadMipPlan.FirstSourceMip + uploadLevel)
            {
                diagnostic = $"Texture '{runState.Texture0.LogicalName}' bump semantic mip {uploadLevel} no longer corresponds to the R4J source mip.";
                return false;
            }
            if (source.Width <= 0 || source.Height <= 0 || source.Pixels.Length != checked(source.Width * source.Height))
            {
                diagnostic = $"Texture '{runState.Texture0.LogicalName}' bump semantic mip {uploadLevel} has invalid dimensions/pixel count.";
                return false;
            }

            mips.Add(new EnbOpenGlStage0TextureMipUpload(uploadLevel, source.Width, source.Height, source.Pixels.AsMemory()));
        }

        int glMinFilter = MapMinFilter(runState.Sampler, mips.Count);
        int glMagFilter = runState.Sampler.MagFilter == EnbRenderTextureMinMagFilter.Point ? GlNearest : GlLinear;
        EnbOpenGlStage0TextureMipUpload last = mips[^1];
        bool usesMipFiltering = mips.Count > 1 && runState.Sampler.MipFilter != EnbRenderTextureMipFilter.None;
        bool requiresExplicitMaximumMipLevel = usesMipFiltering && (last.Width != 1 || last.Height != 1);
        plan = new EnbOpenGlStage0TextureUploadPlan(
            runState,
            mips.ToImmutable(),
            MapAddressMode(runState.Sampler.AddressU),
            MapAddressMode(runState.Sampler.AddressV),
            glMinFilter,
            glMagFilter,
            runState.Sampler.MaximumAnisotropy,
            requiresExplicitMaximumMipLevel);
        return true;
    }

    public static bool TryPlanStage(
        int stageOrdinal,
        EnbW3dRenderTextureBinding texture,
        EnbTextureImage image,
        EnbTextureUploadMipPlan mipPlan,
        EnbW3dTextureSamplerSemantics sampler,
        out EnbOpenGlTextureStageUploadPlan plan,
        out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(texture);
        ArgumentNullException.ThrowIfNull(image);
        plan = default!;
        diagnostic = null;

        if (mipPlan.UploadMipCount <= 0 || mipPlan.FirstSourceMip < 0)
        {
            diagnostic = $"Texture '{texture.LogicalName}' stage {stageOrdinal} has an invalid R4J upload mip plan.";
            return false;
        }

        if (mipPlan.FirstSourceMip + mipPlan.UploadMipCount > image.MipLevels.Count)
        {
            diagnostic = $"Texture '{texture.LogicalName}' stage {stageOrdinal} upload mip plan exceeds its decoded authored mip chain.";
            return false;
        }

        var mips = ImmutableArray.CreateBuilder<EnbOpenGlStage0TextureMipUpload>(mipPlan.UploadMipCount);
        for (int uploadLevel = 0; uploadLevel < mipPlan.UploadMipCount; uploadLevel++)
        {
            EnbTextureMipLevel source = image.MipLevels[mipPlan.FirstSourceMip + uploadLevel];
            if (source.Width <= 0 || source.Height <= 0)
            {
                diagnostic = $"Texture '{texture.LogicalName}' mip {mipPlan.FirstSourceMip + uploadLevel} has invalid dimensions {source.Width}x{source.Height}.";
                return false;
            }

            ReadOnlySpan<EnbColor32> pixels = source.CompressedBlocks.IsEmpty ? source.Pixels.Span : default;
            if (source.CompressedBlocks.IsEmpty && pixels.Length != checked(source.Width * source.Height))
            {
                diagnostic = $"Texture '{texture.LogicalName}' mip {mipPlan.FirstSourceMip + uploadLevel} has {pixels.Length} pixels for {source.Width}x{source.Height}.";
                return false;
            }

            var upload=new EnbOpenGlStage0TextureMipUpload(uploadLevel, source.Width, source.Height,
                source.CompressedBlocks.IsEmpty ? source.Pixels : default)
                {SourceMip=source.CompressedBlocks.IsEmpty ? null : source};
            if(!source.CompressedBlocks.IsEmpty)
            {
                int internalFormat=source.CompressedFourCc switch
                {
                    0x31545844=>0x83F1, // RGBA DXT1: preserves transparent selector.
                    0x32545844 or 0x33545844=>0x83F2,
                    0x34545844 or 0x35545844=>0x83F3,
                    _=>0,
                };
                int blockBytes=source.CompressedFourCc==0x31545844?8:16;
                if(internalFormat==0 || source.CompressedBlocks.Length!=checked(((source.Width+3)/4)*((source.Height+3)/4)*blockBytes))
                {
                    diagnostic=$"Texture '{texture.LogicalName}' has invalid compressed storage.";
                    return false;
                }
                upload=upload with {CompressedBlocks=source.CompressedBlocks,GlInternalFormat=internalFormat};
            }
            if(image.CurrentRetailSurfaceFormat is 4 or 6 or 7)
            {
                var packed=source.CurrentRetailPackedPixels.ToArray();
                if(packed.Length!=checked(pixels.Length*2))
                {
                    diagnostic=$"Texture '{texture.LogicalName}' has invalid packed storage.";
                    return false;
                }
                for(int pixel=0;pixel<pixels.Length;pixel++)
                {
                    ushort word=System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(packed.AsSpan(pixel*2,2));
                    ushort reordered=image.CurrentRetailSurfaceFormat switch
                    {
                        6 => (ushort)((word&0x7fff)<<1 | word>>15),
                        7 => (ushort)((word&0x0fff)<<4 | word>>12),
                        _ => word,
                    };
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(packed.AsSpan(pixel*2,2),reordered);
                }
                upload=upload with
                {
                    PackedBytes=ImmutableCollectionsMarshal.AsImmutableArray(packed),
                    GlInternalFormat=image.CurrentRetailSurfaceFormat switch {4=>0x8d62,6=>0x8057,_=>0x8056},
                    GlPixelFormat=image.CurrentRetailSurfaceFormat==4?0x1907:0x1908,
                    GlPixelType=image.CurrentRetailSurfaceFormat switch {4=>0x8363,6=>0x8034,_=>0x8033},
                };
            }
            mips.Add(upload);
        }

        int glMinFilter = MapMinFilter(sampler, mipPlan.UploadMipCount);
        int glMagFilter = sampler.MagFilter == EnbRenderTextureMinMagFilter.Point ? GlNearest : GlLinear;
        EnbOpenGlStage0TextureMipUpload last = mips[^1];
        bool usesMipFiltering = mipPlan.UploadMipCount > 1 && sampler.MipFilter != EnbRenderTextureMipFilter.None;
        bool requiresExplicitMaximumMipLevel = usesMipFiltering && (last.Width != 1 || last.Height != 1);

        plan = new EnbOpenGlTextureStageUploadPlan(
            stageOrdinal, texture, image, mipPlan, sampler, mips.ToImmutable(),
            MapAddressMode(sampler.AddressU), MapAddressMode(sampler.AddressV),
            glMinFilter, glMagFilter, sampler.MaximumAnisotropy, requiresExplicitMaximumMipLevel);
        return true;
    }

    public static int MapAddressMode(EnbRenderTextureAddressMode mode)
        => mode switch
        {
            EnbRenderTextureAddressMode.Wrap => GlRepeat,
            EnbRenderTextureAddressMode.Clamp => GlClampToEdge,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported R4H texture address mode."),
        };

    public static int MapMinFilter(EnbW3dTextureSamplerSemantics sampler, int uploadedMipCount)
    {
        if (uploadedMipCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(uploadedMipCount));

        bool pointBase = sampler.MinFilter == EnbRenderTextureMinMagFilter.Point;
        if (uploadedMipCount == 1 || sampler.MipFilter == EnbRenderTextureMipFilter.None)
            return pointBase ? GlNearest : GlLinear;

        return (pointBase, sampler.MipFilter) switch
        {
            (true, EnbRenderTextureMipFilter.Point) => GlNearestMipmapNearest,
            (false, EnbRenderTextureMipFilter.Point) => GlLinearMipmapNearest,
            (true, EnbRenderTextureMipFilter.Linear) => GlNearestMipmapLinear,
            (false, EnbRenderTextureMipFilter.Linear) => GlLinearMipmapLinear,
            _ => throw new ArgumentOutOfRangeException(nameof(sampler), sampler, "Unsupported R4J texture min/mip filter combination."),
        };
    }
}

public sealed partial class EnbOpenGlStage0TextureBackend
{
    // Explicit optional capability. Ordinary submissions retain their accepted
    // buffers/output when disabled; enabled resources use the same GPU shaders.
    public bool NPatchCapabilityEnabled { get; init; }
    private readonly int _npatchSegments = EnbW3dNPatchCompiler.RetailSegments;
    public int NPatchSegments { get => _npatchSegments; init => _npatchSegments = EnbW3dNPatchCompiler.NormalizeSegments(unchecked((uint)value)); }

    private EnbW3dProgrammableTextureLanePlan PrepareNPatches(EnbW3dProgrammableTextureLanePlan lane,
        out EnbW3dNPatchGeometry? geometry)
    {
        geometry = null;
        if (!EnbW3dNPatchCompiler.IsEligible(lane.Submission.Packet, NPatchCapabilityEnabled, NPatchSegments)) return lane;
        geometry = EnbW3dNPatchCompiler.Compile(lane.Submission, NPatchSegments);
        var submission = lane.Submission with { NPatchGeometry = geometry, Runs = geometry.Runs };
        var lighting = (lane.LightingPlan).Copy().SetSubmission(submission).SetNormals(geometry.Normals).SetColor1(geometry.Interpolate(lane.LightingPlan.Color1)).SetTextureCoordinates0(geometry.Interpolate(lane.LightingPlan.TextureCoordinates0)).SetRunStates(lane.LightingPlan.RunStates.Select((state, i) => (state).Copy().SetRun(submission.Runs[i])).ToImmutableArray());
        return (lane).Copy().SetLightingPlan(lighting).SetTextureCoordinates1(geometry.Interpolate(lane.TextureCoordinates1)).SetRunStates(lane.RunStates.Select((state, i) => state with { LightingRun = lighting.RunStates[i] }).ToImmutableArray());
    }

    public static EnbOpenGlIndexedUploadPlan RebindNPatchCommands(EnbOpenGlStage0TextureMeshResource resource,
        EnbW3dIndexedSubmissionPlan current)
    {
        var source = resource.GeometryUploadPlan.IndexedPlan;
        if (resource.NPatchGeometry is null) throw new InvalidOperationException("Resource has no N-patch topology.");
        if (source.DrawCommands.Length != current.Runs.Length) throw new InvalidOperationException("N-patch run topology changed.");
        for (int i = 0; i < current.Runs.Length; i++)
        {
            var old = resource.LanePlan.Submission.Runs[i]; var next = current.Runs[i];
            if (old.StartIndex != next.StartIndex || old.IndexCount != next.IndexCount ||
                old.MinVertexIndex != next.MinVertexIndex || old.MaxVertexIndex != next.MaxVertexIndex)
                throw new InvalidOperationException("N-patch source range changed; recreate its GPU resource.");
        }
        return source with { DrawCommands = source.DrawCommands.Select((command, i) => command with
            { SourceRun = command.SourceRun with { Binding = current.Runs[i].Binding } }).ToImmutableArray() };
    }
    private const int GlUnsignedInt = 0x1405;
    private const int GlStreamDraw = 0x88E0;
    private const int GlDepthBufferBit = 0x00000100;
    private const int GlDepthTest = 0x0B71;
    private const int GlCullFace = 0x0B44;
    private const int GlBlend = 0x0BE2;
    private const int GlTexture2D = 0x0DE1;
    private const int GlTexture0 = 0x84C0;
    private const int GlTexture1 = 0x84C1;
    private const int GlRgba = 0x1908;
    private const int GlUnsignedByte = 0x1401;
    private const int GlTextureMagFilter = 0x2800;
    private const int GlTextureMinFilter = 0x2801;
    private const int GlTextureWrapS = 0x2802;
    private const int GlTextureWrapT = 0x2803;
    private const int GlTextureMaxLevel = 0x813D;
    private const int GlTextureMaxAnisotropyExt = 0x84FE;
    private const int GlUnpackAlignment = 0x0CF5;

    private const int PositionAttributeIndex = 0;
    private const int NormalAttributeIndex = 1;
    private const int Color1AttributeIndex = 2;
    private const int TextureCoordinate0AttributeIndex = 3;
    private const int TextureCoordinate1AttributeIndex = 4;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void GlUniform4fProc(int location, float x, float y, float z, float w);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void GlCullFaceProc(int mode);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void GlBlendFuncProc(int sourceFactor, int destinationFactor);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void GlBlendFuncSeparateProc(int source, int destination, int sourceAlpha, int destinationAlpha);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void GlFrontFaceProc(int mode);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void GlUniform1iProc(int location, int value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void GlDepthIntegerProc(int name, out int value);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void GlGenTexturesProc(int count, out int texture);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void GlDeleteTexturesProc(int count, ref int texture);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void GlActiveTextureProc(int textureUnit);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void GlBindTextureProc(int target, int texture);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void GlTexParameteriProc(int target, int parameterName, int value);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void GlTexParameterfProc(int target, int parameterName, float value);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void GlPixelStoreiProc(int parameterName, int value);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void GlTexImage2DProc(
        int target,
        int level,
        int internalFormat,
        int width,
        int height,
        int border,
        int format,
        int type,
        IntPtr pixels);

    private static readonly EnbW3dRetailMapperRandomSource s_mapperRandomSource = EnbW3dRetailMapperRandomSource.Process;

    private readonly record struct LightUniformLocations(
        int State,
        int Diffuse,
        int Specular,
        int Ambient,
        int PositionPhi,
        int DirectionTheta,
        int Attenuation);

    private int _program;
    private int _hdrUniform, _uploadedHdr = -1;
    private int _clipFromModelUniform = -1;
    private int _canonicalClipFromModelUniform = -1;
    private int _viewFromModelUniform = -1;
    private int _sceneDepthUniform = -1;
    private int _sceneDepthBiasUniform = -1;
    private int _retailGeneratedViewFromWorldUniform = -1;
    private int _worldFromModelUniform = -1;
    private int _effectProjectionUniform = -1;
    private int _effectProjectionEnabledUniform = -1;
    private int _effectProjection1Uniform = -1;
    private int _effectProjection1EnabledUniform = -1;
    private int _normalFromModelUniform = -1;
    private int _cameraWorldPositionUniform = -1;
    private int _globalAmbientUniform = -1;
    private int _lightingEnabledUniform = -1;
    private int _materialAmbientUniform = -1;
    private int _materialDiffuseUniform = -1;
    private int _materialSpecularUniform = -1;
    private int _materialEmissiveUniform = -1;
    private int _materialSourcesUniform = -1;
    private int _materialPowerSpecularUniform = -1;
    private int _texture0Uniform = -1;
    private int _texture0FromFramebufferUniform = -1;
    private int _texture1Uniform = -1;
    private int _stage0ProgramUniform = -1;
    private int _stage1ColorProgramUniform = -1;
    private int _stage1AlphaProgramUniform = -1;
    private int _mapper0ScaleOffsetUniform = -1;
    private int _mapper0CrossUniform = -1;
    private int _mapper0KindUniform = -1;
    private int _mapper0SourceUniform = -1;
    private int _mapper0AxisUniform = -1;
    private int _mapper1ScaleOffsetUniform = -1;
    private int _mapper1CrossUniform = -1;
    private int _mapper1KindUniform = -1;
    private int _mapper1SourceUniform = -1;
    private int _mapper1AxisUniform = -1;
    private int _perturbationMatrixUniform = -1;
    private int _perturbationLuminanceUniform = -1;
    private int _opaqueCoverageUniform = -1;
    private int _alphaTestModeUniform = -1;
    private int _alphaTestReferenceUniform = -1;
    private int _fogRangeUniform = -1;
    private int _fogEnabledUniform = -1;
    private int _fogColorUniform = -1;
    private ImmutableArray<LightUniformLocations> _lightUniforms = ImmutableArray<LightUniformLocations>.Empty;
    private GlUniform4fProc? _uniform4f;
    private GlUniform1iProc? _uniform1i;
    private GlDepthIntegerProc? _depthInteger;
    private GlCullFaceProc? _cullFace;
    private GlBlendFuncProc? _blendFunc;
    private GlBlendFuncSeparateProc? _blendFuncSeparate;
    public bool RetainBlendProcedure { get; set; }
    private GlFrontFaceProc? _frontFace;
    private GlGenTexturesProc? _genTextures;
    private GlDeleteTexturesProc? _deleteTextures;
    private GlActiveTextureProc? _activeTexture;
    private GlBindTextureProc? _bindTexture;
    private GlTexParameteriProc? _texParameteri;
    private GlTexParameterfProc? _texParameterf;
    private GlPixelStoreiProc? _pixelStorei;
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void GlCompressedTexImage2DProc(int target,int level,int internalFormat,int width,int height,int border,int imageSize,IntPtr data);
    private GlCompressedTexImage2DProc? _compressedTexImage2D;
    private GlTexImage2DProc? _texImage2D;

    public bool IsInitialized => _program != 0;
    public EnbOpenGlContextIdentity? ContextIdentity { get; private set; }

    public void Initialize(GlInterface gl)
    {
        using var programWork=EnbHitchEvents.Measure("program.initialize.stage0");
        InvalidateUniformCache();
        ArgumentNullException.ThrowIfNull(gl);
        if (IsInitialized)
            throw new InvalidOperationException("The R6E OpenGL stage-0 texture backend is already initialized.");

        _uniformSlotByLocation.Clear();
        _registeredUniformLocations.Clear();

        AdoptResourceContext(gl);
        GlVersion version = gl.ContextInfo.Version;
        if (!IsSupportedShaderContext(version))
            throw new NotSupportedException($"R6E requires desktop OpenGL 2.0+ or OpenGL ES 2.0+; the host supplied {version}.");

        (string vertexSource, string fragmentSource) = CreateShaderSources(version);
        fragmentSource=EnbOpenGlSceneDepth.FragmentShader(fragmentSource,version);
        int vertexShader = gl.CreateShader(GlConsts.GL_VERTEX_SHADER);
        int fragmentShader = gl.CreateShader(GlConsts.GL_FRAGMENT_SHADER);
        int program = 0;
        try
        {
            string? vertexError = gl.CompileShaderAndGetError(vertexShader, vertexSource);
            if (!string.IsNullOrWhiteSpace(vertexError))
                throw new InvalidOperationException($"R6E OpenGL vertex shader compilation failed: {vertexError}");

            string? fragmentError = gl.CompileShaderAndGetError(fragmentShader, fragmentSource);
            if (!string.IsNullOrWhiteSpace(fragmentError))
                throw new InvalidOperationException($"R6E OpenGL fragment shader compilation failed: {fragmentError}");

            program = gl.CreateProgram();
            gl.AttachShader(program, vertexShader);
            gl.AttachShader(program, fragmentShader);
            gl.BindAttribLocationString(program, PositionAttributeIndex, "aPosition");
            gl.BindAttribLocationString(program, NormalAttributeIndex, "aNormal");
            gl.BindAttribLocationString(program, Color1AttributeIndex, "aColor1");
            gl.BindAttribLocationString(program, TextureCoordinate0AttributeIndex, "aTexCoord0");
            gl.BindAttribLocationString(program, TextureCoordinate1AttributeIndex, "aTexCoord1");
            string? linkError = gl.LinkProgramAndGetError(program);
            if (!string.IsNullOrWhiteSpace(linkError))
                throw new InvalidOperationException($"R6E OpenGL W3D lighting program link failed: {linkError}");

            _clipFromModelUniform = RequireUniform(gl, program, "uInstanceMatrices[0]");
            _canonicalClipFromModelUniform = RequireUniform(gl, program, "uInstanceMatrices[1]");
            _viewFromModelUniform = RequireUniform(gl, program, "uInstanceMatrices[2]");
            _sceneDepthUniform = RequireUniform(gl, program, "uSceneDepth");
            _sceneDepthBiasUniform = RequireUniform(gl, program, "uSceneDepthBias");
            _retailGeneratedViewFromWorldUniform = RequireUniform(gl, program, "uRetailGeneratedViewFromWorld");
            _worldFromModelUniform = RequireUniform(gl, program, "uInstanceMatrices[3]");
            _effectProjectionUniform = RequireUniform(gl, program, "uEffectProjection");
            _effectProjectionEnabledUniform = RequireUniform(gl, program, "uEffectProjectionEnabled");
            _effectProjection1Uniform = RequireUniform(gl, program, "uEffectProjection1");
            _effectProjection1EnabledUniform = RequireUniform(gl, program, "uEffectProjection1Enabled");
            _normalFromModelUniform = RequireUniform(gl, program, "uInstanceMatrices[4]");
            _cameraWorldPositionUniform = RequireUniform(gl, program, "uCameraWorldPosition");
            _globalAmbientUniform = RequireUniform(gl, program, "uGlobalAmbient");
            _lightingEnabledUniform = RequireUniform(gl, program, "uLightingEnabled");
            _materialAmbientUniform = RequireUniform(gl, program, "uMaterialAmbient");
            _materialDiffuseUniform = RequireUniform(gl, program, "uMaterialDiffuse");
            _materialSpecularUniform = RequireUniform(gl, program, "uMaterialSpecular");
            _materialEmissiveUniform = RequireUniform(gl, program, "uMaterialEmissive");
            _materialSourcesUniform = RequireUniform(gl, program, "uMaterialSources");
            _materialPowerSpecularUniform = RequireUniform(gl, program, "uMaterialPowerSpecular");
            _texture0Uniform = RequireUniform(gl, program, "uTexture0");
            _texture0FromFramebufferUniform = RequireUniform(gl, program, "uTexture0FromFramebuffer");
            _texture1Uniform = RequireUniform(gl, program, "uTexture1");
            _stage0ProgramUniform = RequireUniform(gl, program, "uStage0Program");
            _stage1ColorProgramUniform = RequireUniform(gl, program, "uStage1ColorProgram");
            _stage1AlphaProgramUniform = RequireUniform(gl, program, "uStage1AlphaProgram");
            _mapper0ScaleOffsetUniform = RequireUniform(gl, program, "uMapper0ScaleOffset");
            _mapper0CrossUniform = RequireUniform(gl, program, "uMapper0Cross");
            _mapper0KindUniform = RequireUniform(gl, program, "uMapper0Kind");
            _mapper0SourceUniform = RequireUniform(gl, program, "uMapper0Source");
            _mapper0AxisUniform = RequireUniform(gl, program, "uMapper0Axis");
            _mapper1ScaleOffsetUniform = RequireUniform(gl, program, "uMapper1ScaleOffset");
            _mapper1CrossUniform = RequireUniform(gl, program, "uMapper1Cross");
            _mapper1KindUniform = RequireUniform(gl, program, "uMapper1Kind");
            _mapper1SourceUniform = RequireUniform(gl, program, "uMapper1Source");
            _mapper1AxisUniform = RequireUniform(gl, program, "uMapper1Axis");
            _perturbationMatrixUniform = RequireUniform(gl, program, "uPerturbationMatrix");
            _perturbationLuminanceUniform = RequireUniform(gl, program, "uPerturbationLuminance");
            _opaqueCoverageUniform = RequireUniform(gl, program, "uOpaqueCoverage");
            _alphaTestModeUniform = RequireUniform(gl, program, "uAlphaTestMode");
            _alphaTestReferenceUniform = RequireUniform(gl, program, "uAlphaTestReference");
            _fogRangeUniform = RequireUniform(gl, program, "uFogRange");
            _fogEnabledUniform = RequireUniform(gl, program, "uFogEnabled");
            _fogColorUniform = RequireUniform(gl, program, "uFogColor");

            var lightUniforms = ImmutableArray.CreateBuilder<LightUniformLocations>(EnbW3dLightEnvironment.CurrentRetailLightSlotCount);
            for (int i = 0; i < EnbW3dLightEnvironment.CurrentRetailLightSlotCount; i++)
            {
                lightUniforms.Add(new LightUniformLocations(
                    RequireUniform(gl, program, $"uLight{i}State"),
                    RequireUniform(gl, program, $"uLight{i}Diffuse"),
                    RequireUniform(gl, program, $"uLight{i}Specular"),
                    RequireUniform(gl, program, $"uLight{i}Ambient"),
                    RequireUniform(gl, program, $"uLight{i}PositionPhi"),
                    RequireUniform(gl, program, $"uLight{i}DirectionTheta"),
                    RequireUniform(gl, program, $"uLight{i}Attenuation")));
            }

            var subDataAddress=gl.GetProcAddress("glBufferSubData");
            _geometrySubData=subDataAddress==IntPtr.Zero?null:Marshal.GetDelegateForFunctionPointer<GlGeometrySubDataProc>(subDataAddress);
            _uniform4f = LoadRequiredProc<GlUniform4fProc>(gl, "glUniform4f");
            _uniform1i = LoadRequiredProc<GlUniform1iProc>(gl, "glUniform1i");
            _depthInteger = LoadRequiredProc<GlDepthIntegerProc>(gl, "glGetIntegerv");
            _genTextures = LoadRequiredProc<GlGenTexturesProc>(gl, "glGenTextures");
            _deleteTextures = LoadRequiredProc<GlDeleteTexturesProc>(gl, "glDeleteTextures");
            _activeTexture = LoadRequiredProc<GlActiveTextureProc>(gl, "glActiveTexture");
            _bindTexture = LoadRequiredProc<GlBindTextureProc>(gl, "glBindTexture");
            _texParameteri = LoadRequiredProc<GlTexParameteriProc>(gl, "glTexParameteri");
            _texParameterf = LoadRequiredProc<GlTexParameterfProc>(gl, "glTexParameterf");
            _pixelStorei = LoadRequiredProc<GlPixelStoreiProc>(gl, "glPixelStorei");
            _texImage2D = LoadRequiredProc<GlTexImage2DProc>(gl, "glTexImage2D");
            _compressedTexImage2D = LoadRequiredProc<GlCompressedTexImage2DProc>(gl, "glCompressedTexImage2D");
            _lightUniforms = lightUniforms.ToImmutable();
            _uniformLocations = _registeredUniformLocations.ToArray();
            _vectorUniformValues = new UniformSlot<Vector4>[_uniformLocations.Length];
            _matrixUniformValues = new UniformSlot<Matrix4x4>[_uniformLocations.Length];
            _integerUniformValues = new UniformSlot<int>[_uniformLocations.Length];
            _scalarUniformValues = new UniformSlot<float>[_uniformLocations.Length];
            _program = program;
            _hdrUniform=gl.GetUniformLocationString(program,"uHighDynamicRange");
            program = 0;
            ContextIdentity = new EnbOpenGlContextIdentity(
                version.Type,
                version.Major,
                version.Minor,
                gl.Vendor ?? string.Empty,
                gl.Renderer ?? string.Empty,
                gl.Version ?? string.Empty);
        }
        finally
        {
            gl.DeleteShader(vertexShader);
            gl.DeleteShader(fragmentShader);
            if (program != 0)
                gl.DeleteProgram(program);
        }
    }

    public bool TryCreateResource(
        GlInterface gl,
        EnbW3dProgrammableTextureLanePlan lanePlan,
        out EnbOpenGlStage0TextureMeshResource resource,
        out string? diagnostic)
        => TryCreateResource(gl, lanePlan, CurrentMapperClockMilliseconds(), out resource, out diagnostic);

    public bool TryCreateResource(
        GlInterface gl,
        EnbW3dProgrammableTextureLanePlan lanePlan,
        uint mapperStartMilliseconds,
        out EnbOpenGlStage0TextureMeshResource resource,
        out string? diagnostic,
        EnbOpenGlTextureMeshRecoveryState? recovery = null)
    {
        ArgumentNullException.ThrowIfNull(gl);
        ArgumentNullException.ThrowIfNull(lanePlan);
        EnsureInitialized();
        InterruptRetainedSubmission(materialChanged:false);
        resource = default!;
        diagnostic = null;

        if (lanePlan.LightingPlan.Normals.Length != lanePlan.VertexCount ||
            lanePlan.LightingPlan.Color1.Length != lanePlan.VertexCount ||
            lanePlan.LightingPlan.TextureCoordinates0.Length != lanePlan.VertexCount ||
            lanePlan.TextureCoordinates1.Length != lanePlan.VertexCount)
        {
            diagnostic = "Texture lane vertex streams do not match submission positions.";
            return false;
        }
        var sourceLane = lanePlan;
        lanePlan = PrepareNPatches(lanePlan, out var npatchGeometry);
        EnbW3dProgrammableLightingLanePlan lightingLane = lanePlan.LightingPlan;
        bool supportsUnsignedIntIndices = EnbOpenGlIndexedSubmissionBackend.SupportsUnsignedIntIndices(
            gl.ContextInfo.Version,
            gl.ContextInfo.Extensions);
        if (!EnbOpenGlLitShaderUploadPlanner.TryPlan(
                lightingLane,
                supportsUnsignedIntIndices,
                out EnbOpenGlLitShaderUploadPlan geometryUploadPlan,
                out diagnostic))
        {
            return false;
        }

        if (lanePlan.RunStates.Length != geometryUploadPlan.IndexedPlan.DrawCommands.Length)
        {
            diagnostic = $"R6G has {lanePlan.RunStates.Length} texture run states for {geometryUploadPlan.IndexedPlan.DrawCommands.Length} R6A/OpenGL draw commands.";
            return false;
        }
        if (lanePlan.TextureCoordinates1.Length != geometryUploadPlan.VertexCount)
        {
            diagnostic = $"R6G has {lanePlan.TextureCoordinates1.Length} stage-1 UV values for {geometryUploadPlan.VertexCount} submission vertices.";
            return false;
        }

        var retainedMappers = recovery?.Mappers ?? default;
        if (recovery is not null && (retainedMappers.Length != lanePlan.RunStates.Length ||
            recovery.Textures0.Length != lanePlan.RunStates.Length ||
            recovery.Textures1.Length != lanePlan.RunStates.Count(run=>run.Texture1 is not null)))
        {
            diagnostic = "Retained texture/mapper runs do not match the recovered draw.";
            return false;
        }
        var texture0Plans = ImmutableArray.CreateBuilder<EnbOpenGlStage0TextureUploadPlan>(lanePlan.RunStates.Length);
        var texture1Plans = ImmutableArray.CreateBuilder<EnbOpenGlTextureStageUploadPlan>();
        // Pixel/sampler sharing is independent of material-owned mapper state.
        // The image object is the decoded-content identity, including procedural
        // decals/atlases; a matching filename alone is not sufficient.

        bool PlanImage(int stage, EnbW3dRenderTextureBinding texture, EnbTextureImage image,
            EnbTextureUploadMipPlan mips, EnbW3dTextureSamplerSemantics sampler,
            out EnbOpenGlTextureStageUploadPlan plan, out string? reason)
        {
            var key = new TexturePlanKey(mips, sampler);
            var imagePlans=TextureSharingEnabled?_imageUploadPlans.GetValue(image,static _=>new()):null;
            if (imagePlans is not null && imagePlans.TryGetValue(key, out var cached))
            {
                plan = cached with { StageOrdinal = stage, Texture = texture };
                reason = null;
                return true;
            }
            if (!EnbOpenGlStage0TextureUploadPlanner.TryPlanStage(stage, texture, image, mips, sampler, out plan, out reason)) return false;
            imagePlans?.Add(key, plan);
            return true;
        }
        var mapper0RuntimeStates = ImmutableArray.CreateBuilder<EnbW3dMapperRuntimeState>(lanePlan.RunStates.Length);
        var mapper1RuntimeStates = ImmutableArray.CreateBuilder<EnbW3dMapperRuntimeState>(lanePlan.RunStates.Length);
        for (int runIndex = 0; runIndex < lanePlan.RunStates.Length; runIndex++)
        {
            EnbW3dProgrammableTextureRunState runState = lanePlan.RunStates[runIndex];
            EnbW3dMapperRuntimeState mapper0Runtime;
            EnbW3dMapperRuntimeState mapper1Runtime;
            var packet = lanePlan.Submission.Packet;
            var shared = retainedMappers.IsDefault ? packet.CurrentRetailSharedMaterialMappers : retainedMappers[runIndex];
            if (retainedMappers.IsDefault && lanePlan.Submission.Runs[runIndex].Binding.VertexMaterial is { } material &&
                packet.CurrentRetailMaterialMappers is { } owners &&
                owners.TryGetValue(material.Material, out var materialMappers))
                shared = materialMappers;
            // A material may already carry explicit native mapper pointers even
            // when its caller has not supplied a packet-level owner lookup.
            // GPU allocation must not replace those pointers or restart them.
            if (shared is null && lanePlan.Submission.Runs[runIndex].Binding.VertexMaterial is { Material: var ownedMaterial } &&
                (ownedMaterial.CurrentRetailMapper0Owner is not null || ownedMaterial.CurrentRetailMapper1Owner is not null) &&
                !EnbW3dSharedMaterialMappers.TryCreate(ownedMaterial, mapperStartMilliseconds,
                    out shared, out diagnostic, s_mapperRandomSource))
                return false;
            if (shared is not null)
            {
                mapper0Runtime = shared.Stage0;
                mapper1Runtime = shared.Stage1;
            }
            else if (!EnbW3dMapperRuntimeState.TryCreate(runState.Mapper0Program, mapperStartMilliseconds,
                         s_mapperRandomSource, out mapper0Runtime, out diagnostic) ||
                     !EnbW3dMapperRuntimeState.TryCreate(runState.Mapper1Program, mapperStartMilliseconds,
                         s_mapperRandomSource, out mapper1Runtime, out diagnostic))
            {
                return false;
            }
            mapper0RuntimeStates.Add(mapper0Runtime);
            mapper1RuntimeStates.Add(mapper1Runtime);
            bool terminatesStage0 = lanePlan.Submission.Runs[0].Binding.Semantics.PermutationKey.TextureStageCount > 0 &&
                runState.Texture0.IsUnbound &&
                runState.Stage0Combiner.Color.Argument1 == EnbW3dTextureArgument.Texture;
            var compiledStage0=EnbW3dProgrammableStage0ProgramKind.PassDiffuse;
            if ((!terminatesStage0 && !EnbW3dProgrammableTextureLaneCompiler.TryClassifyStage0Program(
                    runState.Stage0Combiner,
                    out compiledStage0,
                    out diagnostic)) || compiledStage0 != runState.Stage0Program)
            {
                diagnostic ??= $"R6H stage-0 program key {runState.Stage0Program} no longer matches its compiled R4E combiner semantics.";
                return false;
            }
            var compiledStage1=EnbW3dProgrammableStage1Program.Disabled;
            if (!terminatesStage0 && !EnbW3dProgrammableTextureLaneCompiler.TryClassifyNonBumpStage1Program(
                    runState.Stage1Combiner,
                    out compiledStage1,
                    out diagnostic))
            {
                return false;
            }

            if (terminatesStage0 != runState.Stage0TerminatedByNullTextureCascade ||
                (terminatesStage0 && (runState.Texture1 is not null || runState.Stage1Program.Enabled ||
                    runState.Stage1TerminatedByNullTextureCascade)))
            {
                diagnostic = "Stage-0 null texture cascade does not match the retained bindings/effective programs.";
                return false;
            }
            bool hasBoundStage1Texture = runState.Texture1 is { IsUnbound: false };
            if (!terminatesStage0 && (!EnbW3dProgrammableTextureLaneCompiler.TryResolveD3d8NullTextureCascade(
                    runState.Stage1Combiner,
                    compiledStage1,
                    hasBoundStage1Texture,
                    out EnbW3dProgrammableStage1Program effectiveStage1,
                    out bool stage1TerminatedByNullTextureCascade,
                    out diagnostic) ||
                effectiveStage1 != runState.Stage1Program ||
                stage1TerminatedByNullTextureCascade != runState.Stage1TerminatedByNullTextureCascade))
            {
                diagnostic ??= "R6Z9 effective stage-1 program no longer matches the authored R4E combiner plus current-retail NULL-texture cascade semantics.";
                return false;
            }

            EnbOpenGlStage0TextureUploadPlan texture0Plan;
            if (recovery is not null)
                texture0Plan = recovery.Textures0[runIndex] with { RunState = runState };
            else if (runState.SamplesTexture0 && runState.BumpTexture0 is null && runState.Image0 is { } image0)
            {
                if (!PlanImage(0, runState.Texture0, image0, runState.UploadMipPlan, runState.Sampler, out var generic, out diagnostic)) return false;
                texture0Plan = new(runState, generic.Mips, generic.GlWrapS, generic.GlWrapT,
                    generic.GlMinFilter, generic.GlMagFilter, generic.MaximumAnisotropy, generic.RequiresExplicitMaximumMipLevel);
            }
            else if (!EnbOpenGlStage0TextureUploadPlanner.TryPlan(runState, out texture0Plan, out diagnostic))
                return false;
            texture0Plans.Add(texture0Plan);

            if (runState.Texture1 is EnbW3dRenderTextureBinding texture1)
            {
                if (runState.Image1 is not EnbTextureImage image1 ||
                    runState.UploadMipPlan1 is not EnbTextureUploadMipPlan upload1 ||
                    runState.Sampler1 is not EnbW3dTextureSamplerSemantics sampler1)
                {
                    diagnostic = $"Texture '{texture1.LogicalName}' has incomplete R6G stage-1 image/mip/sampler state.";
                    return false;
                }
                EnbOpenGlTextureStageUploadPlan texture1Plan;
                if (recovery is not null) texture1Plan = recovery.Textures1[texture1Plans.Count];
                else if (!PlanImage(1, texture1, image1, upload1, sampler1, out texture1Plan, out diagnostic))
                    return false;
                texture1Plans.Add(texture1Plan);
            }
            else if (runState.Stage1Program.Enabled)
            {
                diagnostic = "R6G active stage-1 program has no stage-1 texture state.";
                return false;
            }
        }

        bool supportsTextureMaxLevel = SupportsTextureMaximumLevel(gl.ContextInfo.Version);
        bool supportsAnisotropy = SupportsAnisotropicFiltering(gl.ContextInfo.Extensions);
        foreach (EnbOpenGlStage0TextureUploadPlan texturePlan in texture0Plans)
        {
            if (!texturePlan.RunState.SamplesTexture0)
                continue;
            if (!ValidateTextureCapabilities(texturePlan.RunState.Texture0.LogicalName, texturePlan.RequiresExplicitMaximumMipLevel, texturePlan.MaximumAnisotropy, supportsTextureMaxLevel, supportsAnisotropy, gl.ContextInfo.Version, out diagnostic))
                return false;
        }
        foreach (EnbOpenGlTextureStageUploadPlan texturePlan in texture1Plans)
        {
            if (!ValidateTextureCapabilities(texturePlan.Texture.LogicalName, texturePlan.RequiresExplicitMaximumMipLevel, texturePlan.MaximumAnisotropy, supportsTextureMaxLevel, supportsAnisotropy, gl.ContextInfo.Version, out diagnostic))
                return false;
        }

        DrainErrors(gl);
        int vertexBuffer = 0;
        int uv1Buffer = 0;
        int indexBuffer = 0;
        int vertexArray = 0;
        EnbOpenGlSharedGeometry? sharedGeometry=null;
        bool shareGeometry=GeometrySharingEnabled && IsShareableGeometry(lanePlan,npatchGeometry);
        var texture0Ids = ImmutableArray.CreateBuilder<int>(texture0Plans.Count);
        var texture1Ids = ImmutableArray.CreateBuilder<int>(lanePlan.RunStates.Length);
        var textureObjects = new Dictionary<TextureObjectKey, int>();
        var nullCoordinateBuffers = new List<int>();
        int AcquireTexture(ImmutableArray<EnbOpenGlStage0TextureMipUpload> mips, int wrapS, int wrapT,
            int min, int mag, int maximumMip, float anisotropy, string name)
        {
            var key = new TextureObjectKey(mips, wrapS, wrapT, min, mag, maximumMip, anisotropy);
            if ((TextureSharingEnabled || _runtimeTextureNames.Contains(name)) && textureObjects.TryGetValue(key, out int found)) return found;
            var dynamicKey = (name, wrapS, wrapT, min, mag, maximumMip, anisotropy);
            if (_runtimeTextureNames.Contains(name) && _sharedRuntimeTextures.TryGetValue(dynamicKey, out var shared))
            {
                _sharedRuntimeTextures[dynamicKey] = (shared.Id, shared.References + 1);
                textureObjects.Add(key, shared.Id);
                return shared.Id;
            }
            bool shareImmutable=TextureSharingEnabled&&!_runtimeTextureNames.Contains(name);
            if(shareImmutable && _sharedImageTextures.TryGetValue(key,out var sharedImage))
            {
                _sharedImageTextures[key]=(sharedImage.Id,sharedImage.References+1);
                textureObjects.Add(key,sharedImage.Id);
                return sharedImage.Id;
            }
            int id = CreateTextureObject(mips, wrapS, wrapT, min, mag, maximumMip, anisotropy, supportsTextureMaxLevel, name);
            if (_runtimeTextureNames.Contains(name))
            {
                _sharedRuntimeTextures.Add(dynamicKey, (id, 1));
                _runtimeTextureKeys.Add(id, dynamicKey);
            }
            if(shareImmutable)
            {
                _sharedImageTextures.Add(key,(id,1));
                _imageTextureKeys.Add(id,key);
            }
            if (TextureSharingEnabled || _runtimeTextureNames.Contains(name)) textureObjects.Add(key, id);
            return id;
        }
        try
        {
            bool useVertexArray = gl.IsGenVertexArraysAvailable && gl.IsBindVertexArrayAvailable && gl.IsDeleteVertexArraysAvailable;
            if (EnbOpenGlIndexedSubmissionBackend.RequiresVertexArray(gl.ContextInfo.Version) && !useVertexArray)
            {
                diagnostic = $"OpenGL context {gl.ContextInfo.Version} requires a vertex array object for the R6G core-profile draw path, but the host did not expose VAO support.";
                return false;
            }

            if(shareGeometry)
            {
                sharedGeometry=AcquireSharedGeometry(lanePlan,geometryUploadPlan);
                if(sharedGeometry is null && useVertexArray && _geometrySubData is not null)
                    sharedGeometry=CreateContainerGeometry(gl,lanePlan,geometryUploadPlan);
            }
            if(sharedGeometry is {} shared)
            {
                vertexBuffer=shared.Vertex;uv1Buffer=shared.Uv;indexBuffer=shared.Index;vertexArray=shared.Vao;
            }
            else
            {
            if (useVertexArray)
            {
                vertexArray = gl.GenVertexArray();
                gl.BindVertexArray(vertexArray);
            }

            vertexBuffer = gl.GenBuffer();
            gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, vertexBuffer);
            UploadArray(gl, GlConsts.GL_ARRAY_BUFFER, geometryUploadPlan.VertexElements.AsSpan(), geometryUploadPlan.IndexedPlan.VertexSource == EnbW3dSubmissionVertexSource.CpuDeformed ? GlStreamDraw : GlConsts.GL_STATIC_DRAW);
            BindVertexLayout(gl);

            var uv1Elements = ImmutableArray.CreateBuilder<float>(checked(lanePlan.TextureCoordinates1.Length * 2));
            foreach (Vector2 uv in lanePlan.TextureCoordinates1)
            {
                //978B70 transfers both UV words without filtering. Unused
                //or mapper-overridden authored NaNs do not reject the mesh.
                uv1Elements.Add(uv.X);
                uv1Elements.Add(uv.Y);
            }
            uv1Buffer = gl.GenBuffer();
            gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, uv1Buffer);
            UploadArray(gl, GlConsts.GL_ARRAY_BUFFER, uv1Elements.ToImmutable().AsSpan(), geometryUploadPlan.IndexedPlan.VertexSource == EnbW3dSubmissionVertexSource.CpuDeformed ? GlStreamDraw : GlConsts.GL_STATIC_DRAW);
            BindTextureCoordinate1Layout(gl);

            //00930C10 resets TEXCOORDINDEX to the stage ordinal for null
            // materials. Expanded descriptors can mix these with normal runs.
            if (lanePlan.RunStates.Any(UsesNullMaterialCoordinates))
                for (int stage = 0; stage < 2; stage++)
                {
                    var coordinates = lanePlan.Submission.Packet.Mesh.RetailTextureCoordinateSets
                        .FirstOrDefault(set => set.Index == stage)?.Coordinates;
                    if (npatchGeometry is not null && coordinates is { Count: > 0 })
                    {
                        if (coordinates.Count != sourceLane.VertexCount)
                        {
                            diagnostic = "Null-material coordinate stream does not match submission vertices.";
                            return false;
                        }
                        coordinates = npatchGeometry.Interpolate(coordinates);
                    }
                    if (coordinates is { Count: > 0 } && coordinates.Count != geometryUploadPlan.VertexCount)
                    {
                        diagnostic = "Null-material coordinate stream does not match submission vertices.";
                        return false;
                    }
                    var elements = new float[checked(geometryUploadPlan.VertexCount * 2)];
                    for (int vertex = 0; vertex < geometryUploadPlan.VertexCount; vertex++)
                    {
                        var uv = coordinates is { Count: > 0 } ? coordinates[vertex] : Vector2.Zero;
                        elements[vertex * 2] = uv.X;
                        elements[vertex * 2 + 1] = uv.Y;
                    }
                    int buffer = gl.GenBuffer();
                    nullCoordinateBuffers.Add(buffer);
                    gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, buffer);
                    UploadArray(gl, GlConsts.GL_ARRAY_BUFFER, elements.AsSpan(), GlConsts.GL_STATIC_DRAW);
                }

            indexBuffer = gl.GenBuffer();
            gl.BindBuffer(GlConsts.GL_ELEMENT_ARRAY_BUFFER, indexBuffer);
            if (geometryUploadPlan.IndexedPlan.IndexElementType == EnbOpenGlIndexElementType.UnsignedShort)
                UploadArray(gl, GlConsts.GL_ELEMENT_ARRAY_BUFFER, geometryUploadPlan.IndexedPlan.UnsignedShortIndices.AsSpan(), GlConsts.GL_STATIC_DRAW);
            else
                UploadArray(gl, GlConsts.GL_ELEMENT_ARRAY_BUFFER, geometryUploadPlan.IndexedPlan.UnsignedIntIndices.AsSpan(), GlConsts.GL_STATIC_DRAW);

            if (useVertexArray)
                gl.BindVertexArray(0);
            gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, 0);
            if (!useVertexArray)
                gl.BindBuffer(GlConsts.GL_ELEMENT_ARRAY_BUFFER, 0);

            }

            RequireProc(_pixelStorei, "glPixelStorei")(GlUnpackAlignment, 1);
            RequireProc(_activeTexture, "glActiveTexture")(GlTexture0);
            foreach (EnbOpenGlStage0TextureUploadPlan texturePlan in texture0Plans)
            {
                if (!texturePlan.RunState.SamplesTexture0)
                {
                    texture0Ids.Add(0);
                    continue;
                }

                int textureId = AcquireTexture(texturePlan.Mips, texturePlan.GlWrapS, texturePlan.GlWrapT, texturePlan.GlMinFilter, texturePlan.GlMagFilter, texturePlan.MaximumMipLevel, texturePlan.MaximumAnisotropy, texturePlan.RunState.Texture0.LogicalName);
                texture0Ids.Add(textureId);
            }

            int stage1PlanIndex = 0;
            RequireProc(_activeTexture, "glActiveTexture")(GlTexture1);
            foreach (EnbW3dProgrammableTextureRunState runState in lanePlan.RunStates)
            {
                if (runState.Texture1 is null)
                {
                    texture1Ids.Add(0);
                    continue;
                }
                EnbOpenGlTextureStageUploadPlan texturePlan = texture1Plans[stage1PlanIndex++];
                int textureId = AcquireTexture(texturePlan.Mips, texturePlan.GlWrapS, texturePlan.GlWrapT, texturePlan.GlMinFilter, texturePlan.GlMagFilter, texturePlan.MaximumMipLevel, texturePlan.MaximumAnisotropy, texturePlan.Texture.LogicalName);
                texture1Ids.Add(textureId);
            }
            RequireProc(_bindTexture, "glBindTexture")(GlTexture2D, 0);
            RequireProc(_activeTexture, "glActiveTexture")(GlTexture0);
            RequireProc(_bindTexture, "glBindTexture")(GlTexture2D, 0);

            int error = gl.GetError();
            if (error != GlConsts.GL_NO_ERROR)
            {
                diagnostic = $"R6G OpenGL resource creation ended with GL error 0x{error:X4}.";
                return false;
            }

            if(shareGeometry && sharedGeometry is null)
                sharedGeometry=RegisterSharedGeometry(lanePlan,geometryUploadPlan,vertexBuffer,uv1Buffer,indexBuffer,vertexArray);
            resource = new EnbOpenGlStage0TextureMeshResource(
                vertexBuffer,
                uv1Buffer,
                indexBuffer,
                vertexArray,
                geometryUploadPlan,
                texture0Ids.ToImmutable(),
                texture0Plans.ToImmutable(),
                texture1Ids.ToImmutable(),
                texture1Plans.ToImmutable(),
                mapper0RuntimeStates.ToImmutable(),
                mapper1RuntimeStates.ToImmutable(),
                sourceLane) { NullMaterialCoordinateBuffers = nullCoordinateBuffers.ToImmutableArray(), NPatchGeometry = npatchGeometry, SharedGeometry=sharedGeometry };
            sharedGeometry=null;
            nullCoordinateBuffers.Clear();
            vertexBuffer = 0;
            uv1Buffer = 0;
            indexBuffer = 0;
            vertexArray = 0;
            texture0Ids.Clear();
            texture1Ids.Clear();
            return true;
        }
        finally
        {
            foreach (int buffer in nullCoordinateBuffers) gl.DeleteBuffer(buffer);
            RequireProc(_pixelStorei, "glPixelStorei")(GlUnpackAlignment, 4);
            foreach (int textureId in texture0Ids.Concat(texture1Ids).Where(id => id != 0).Distinct())
                DeleteTexture(textureId);
            if(sharedGeometry is not null)
            {
                ReleaseSharedGeometry(gl,sharedGeometry);
                vertexArray=vertexBuffer=uv1Buffer=indexBuffer=0;
            }
            if (vertexArray != 0 && gl.IsDeleteVertexArraysAvailable)
                gl.DeleteVertexArray(vertexArray);
            if (indexBuffer != 0)
                gl.DeleteBuffer(indexBuffer);
            if (uv1Buffer != 0)
                gl.DeleteBuffer(uv1Buffer);
            if (vertexBuffer != 0)
                gl.DeleteBuffer(vertexBuffer);
        }
    }

    private static void UploadStreamingVertices(GlInterface gl,EnbOpenGlLitShaderUploadPlan geometry)
    {
        int count=checked(geometry.VertexCount*EnbOpenGlLitShaderUploadPlan.FloatElementsPerVertex);
        var rented=ArrayPool<float>.Shared.Rent(count);
        try
        {
            var vertices=rented.AsSpan(0,count);
            geometry.CopyVertexElementsTo(vertices);
            UploadArray(gl,GlConsts.GL_ARRAY_BUFFER,vertices,GlStreamDraw);
        }
        finally { ArrayPool<float>.Shared.Return(rented); }
    }

    public bool MeasureSubmissionStages { get; set; }
    private readonly long[] _submissionTicks=new long[8];
    public ReadOnlySpan<long> SubmissionStageTicks => _submissionTicks;
    public void ResetSubmissionTiming()
    {
        Array.Clear(_submissionTicks);
        _retainedRequests = _retainedSkips = _retainedBoundaries = _retainedDraws = 0;
        _materialApplication.ResetCounters();
    }
    public string SubmissionTiming => !MeasureSubmissionStages ? "" :
        " � submit stages setup/uniform/state/mapper/mapper-uniform/texture/draw/cleanup ms " +
        string.Join("/",_submissionTicks.Select(t=>(t*1000d/Stopwatch.Frequency).ToString("0.000",System.Globalization.CultureInfo.InvariantCulture)));
    private void SubmissionStage(int index,ref long last)
    {
        if(!MeasureSubmissionStages)return;
        long now=Stopwatch.GetTimestamp();_submissionTicks[index]+=now-last;last=now;
    }

    public long RetainedSkinUploadFrames {get;private set;}

    public EnbOpenGlStage0TextureMeshResource UpdateGeometry(GlInterface gl,EnbOpenGlStage0TextureMeshResource resource,EnbW3dProgrammableTextureLanePlan lane)
        =>UpdateGeometryCore(gl,resource,lane,true);
    public EnbOpenGlStage0TextureMeshResource UpdateOwnedSkinGeometry(GlInterface gl,EnbOpenGlStage0TextureMeshResource resource,EnbW3dSkinLaneOwner owner)
    {
        if(!owner.HasFrame || !ReferenceEquals(owner.Definition,resource.LanePlan))
            throw new InvalidOperationException("Skin upload requires the resource's active material owner.");
        return UpdateGeometryCore(gl,resource,owner.Current,false);
    }
    private EnbOpenGlStage0TextureMeshResource UpdateGeometryCore(GlInterface gl,EnbOpenGlStage0TextureMeshResource resource,EnbW3dProgrammableTextureLanePlan lane,bool retainInvocation)
    {
        // VBO streaming changes buffer bindings only. It does not invalidate
        // the active program, textures, uniforms, target or raster state.
        var sourceLane = lane;
        lane = PrepareNPatches(lane, out var npatchGeometry);
        if(!retainInvocation && npatchGeometry is not null)throw new InvalidOperationException("Owned skin upload cannot replace tessellated topology.");
        if(!EnbOpenGlLitShaderUploadPlanner.TryPlan(lane.LightingPlan,
            EnbOpenGlIndexedSubmissionBackend.SupportsUnsignedIntIndices(gl.ContextInfo.Version,gl.ContextInfo.Extensions),out var geometry,out var diagnostic,resource.GeometryUploadPlan))
            throw new InvalidDataException(diagnostic);
        if(geometry.VertexCount!=resource.GeometryUploadPlan.VertexCount||geometry.IndexedPlan.IndexCount!=resource.GeometryUploadPlan.IndexedPlan.IndexCount)
            throw new InvalidOperationException("Animated geometry must preserve its topology.");
        var skinScratch=lane.Submission.Packet.DeformedGeometry is not null && npatchGeometry is null
            ? resource.SkinUploadScratch??new EnbOpenGlSkinUploadScratch() : null;
        var originalResource=resource;
        resource=EnsurePrivateGeometry(gl,resource);
        try
        {
        gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER,resource.VertexBuffer);
        if(skinScratch is null)UploadStreamingVertices(gl,geometry);
        else {UploadArray(gl,GlConsts.GL_ARRAY_BUFFER,skinScratch.Prepare(geometry),GlStreamDraw);RetainedSkinUploadFrames++;}
        gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER,0);
        FinishPrivateGeometryMutation(gl,originalResource,resource);
        // Owned skin uploads are consumed synchronously. Keep static GPU size/index
        // metadata, not the visible task or current mutable lane after its drain.
        return retainInvocation
            ?resource with {GeometryUploadPlan=geometry,LanePlan=sourceLane,NPatchGeometry=npatchGeometry,SkinUploadScratch=skinScratch}
            :resource with {SkinUploadScratch=skinScratch};
        }
        catch
        {
            AbandonPrivateGeometryMutation(gl,originalResource,resource);
            throw;
        }
    }

    public EnbOpenGlStage0TextureMeshResource UpdateTerrainSeaGeometry(GlInterface gl,
        EnbOpenGlStage0TextureMeshResource resource,EnbW3dProgrammableTextureLanePlan lane)
        => UpdateStreamedTerrainGeometry(gl,resource,lane);

    public EnbOpenGlStage0TextureMeshResource UpdateStreamedTerrainGeometry(GlInterface gl,
        EnbOpenGlStage0TextureMeshResource resource,EnbW3dProgrammableTextureLanePlan lane)
    {
        if((!lane.Submission.Packet.CurrentRetailTerrainSea&&!lane.Submission.Packet.CurrentRetailTerrainDisplacement)||
            resource.LanePlan.RunStates.Length!=lane.RunStates.Length||resource.NullMaterialCoordinateBuffers.Length!=0)
            throw new InvalidOperationException("Streamed terrain geometry requires its retained material passes.");
        return UpdateStreamedGeometry(gl,resource,lane);
    }

    public static bool CanStreamParticleGeometry(EnbOpenGlStage0TextureMeshResource resource,EnbW3dProgrammableTextureLanePlan lane)
    {
        var prior=resource.LanePlan;
        if(!prior.Submission.Packet.CurrentRetailParticleBuffer || !lane.Submission.Packet.CurrentRetailParticleBuffer ||
            prior.RunStates.Length!=lane.RunStates.Length || !resource.NullMaterialCoordinateBuffers.IsEmpty || resource.NPatchGeometry is not null)return false;
        for(int i=0;i<lane.RunStates.Length;i++)
        {
            var a=prior.RunStates[i];var b=lane.RunStates[i];
            // Geometry, colors, UVs and run ranges change freely; texture and
            // mapper owners must remain identical to the retained GPU binding.
            if(a.Texture0!=b.Texture0 || a.Texture1!=b.Texture1 || !ReferenceEquals(a.Image0,b.Image0) || !ReferenceEquals(a.Image1,b.Image1) ||
                a.UploadMipPlan!=b.UploadMipPlan || a.UploadMipPlan1!=b.UploadMipPlan1 || a.Sampler!=b.Sampler || a.Sampler1!=b.Sampler1 ||
                a.Mapper0Program!=b.Mapper0Program || a.Mapper1Program!=b.Mapper1Program || a.IsBumpStage0 || b.IsBumpStage0)return false;
        }
        return true;
    }

    public EnbOpenGlStage0TextureMeshResource UpdateStreamedParticleGeometry(GlInterface gl,
        EnbOpenGlStage0TextureMeshResource resource,EnbW3dProgrammableTextureLanePlan lane)
    {
        if(!CanStreamParticleGeometry(resource,lane))throw new InvalidOperationException("Particle streaming changed retained materials.");
        return UpdateStreamedGeometry(gl,resource,lane);
    }

    private EnbOpenGlStage0TextureMeshResource UpdateStreamedGeometry(GlInterface gl,
        EnbOpenGlStage0TextureMeshResource resource,EnbW3dProgrammableTextureLanePlan lane)
    {
        // Keep the submission cache across an internal VB/IB update. The VAO
        // binding is the only cached draw state changed by these uploads.
        if(!EnbOpenGlLitShaderUploadPlanner.TryPlan(lane.LightingPlan,
            EnbOpenGlIndexedSubmissionBackend.SupportsUnsignedIntIndices(gl.ContextInfo.Version,gl.ContextInfo.Extensions),out var geometry,out var diagnostic,resource.GeometryUploadPlan))
            throw new InvalidDataException(diagnostic);
        var originalResource=resource;
        resource=EnsurePrivateGeometry(gl,resource);
        try
        {
        if(resource.VertexArray!=0)gl.BindVertexArray(resource.VertexArray);
        else if(gl.IsBindVertexArrayAvailable)
        {
            gl.BindVertexArray(0);
            _retainedVao=null;
        }
        gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER,resource.VertexBuffer);
        UploadStreamingVertices(gl,geometry);
        if(!lane.TextureCoordinates1.AsSpan().SequenceEqual(resource.UploadedTextureCoordinates1.AsSpan()))
        {
            var uv1=ArrayPool<float>.Shared.Rent(lane.TextureCoordinates1.Length*2);
            try
            {
                for(int i=0;i<lane.TextureCoordinates1.Length;i++)
                {uv1[i*2]=lane.TextureCoordinates1[i].X;uv1[i*2+1]=lane.TextureCoordinates1[i].Y;}
                gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER,resource.TextureCoordinate1Buffer);
                UploadArray(gl,GlConsts.GL_ARRAY_BUFFER,uv1.AsSpan(0,lane.TextureCoordinates1.Length*2),GlStreamDraw);
            }
            finally {ArrayPool<float>.Shared.Return(uv1);}
        }
        var oldIndices=resource.GeometryUploadPlan.IndexedPlan;
        if(geometry.IndexedPlan.IndexElementType!=oldIndices.IndexElementType ||
            !geometry.IndexedPlan.UnsignedShortIndices.Equals(oldIndices.UnsignedShortIndices) ||
            !geometry.IndexedPlan.UnsignedIntIndices.Equals(oldIndices.UnsignedIntIndices))
        {
            gl.BindBuffer(GlConsts.GL_ELEMENT_ARRAY_BUFFER,resource.IndexBuffer);
            if(geometry.IndexedPlan.IndexElementType==EnbOpenGlIndexElementType.UnsignedShort)
                UploadArray(gl,GlConsts.GL_ELEMENT_ARRAY_BUFFER,geometry.IndexedPlan.UnsignedShortIndices.AsSpan(),GlStreamDraw);
            else UploadArray(gl,GlConsts.GL_ELEMENT_ARRAY_BUFFER,geometry.IndexedPlan.UnsignedIntIndices.AsSpan(),GlStreamDraw);
        }
        if(resource.VertexArray!=0)
        {
            gl.BindVertexArray(0);
            _retainedVao=_retainedSubmissionGl is null?null:0;
        }
        else gl.BindBuffer(GlConsts.GL_ELEMENT_ARRAY_BUFFER,0);
        gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER,0);
        FinishPrivateGeometryMutation(gl,originalResource,resource);
        return resource with {GeometryUploadPlan=geometry,LanePlan=lane,UploadedTextureCoordinates1=lane.TextureCoordinates1};
        }
        catch
        {
            AbandonPrivateGeometryMutation(gl,originalResource,resource);
            throw;
        }
    }

    public EnbOpenGlSubmissionReceipt Submit(
        GlInterface gl,
        int framebuffer,
        int pixelWidth,
        int pixelHeight,
        EnbOpenGlStage0TextureMeshResource resource,
        bool clearColorAndDepth = true)
        => Submit(gl, framebuffer, pixelWidth, pixelHeight, resource, CurrentMapperClockMilliseconds(), clearColorAndDepth);

    public EnbOpenGlSubmissionReceipt Submit(
        GlInterface gl,
        int framebuffer,
        int pixelWidth,
        int pixelHeight,
        EnbOpenGlStage0TextureMeshResource resource,
        EnbW3dSceneFogState sceneFog,
        EnbW3dFogPipelineSemantics fogPipeline,
        bool rendererSupportsFog = true,
        bool clearColorAndDepth = true,
        float? currentRetailPltDayNightPhase = null,
        bool compositeOpaqueCoverage = false,
        EnbOpenGlRenderTexture? borrowedStage0Texture = null,
        int viewportInset = 0,
        bool retailCapturePixelCenters = false)
        => Submit(
            gl, framebuffer, pixelWidth, pixelHeight, resource, CurrentMapperClockMilliseconds(),
            sceneFog, fogPipeline, rendererSupportsFog, clearColorAndDepth, currentRetailPltDayNightPhase, compositeOpaqueCoverage,
            borrowedStage0Texture, viewportInset, retailCapturePixelCenters);

    public EnbOpenGlSubmissionReceipt Submit(
        GlInterface gl,
        int framebuffer,
        int pixelWidth,
        int pixelHeight,
        EnbOpenGlStage0TextureMeshResource resource,
        uint mapperNowMilliseconds,
        bool clearColorAndDepth = true)
        => Submit(
            gl, framebuffer, pixelWidth, pixelHeight, resource, mapperNowMilliseconds,
            new EnbW3dSceneFogState(false, 0u, 0f, 1f),
            EnbW3dFogPipelineSemantics.FromCurrentRetailRangeCapability(false),
            rendererSupportsFog: false,
            clearColorAndDepth);

    public EnbOpenGlSubmissionReceipt Submit(
        GlInterface gl,
        int framebuffer,
        int pixelWidth,
        int pixelHeight,
        EnbOpenGlStage0TextureMeshResource resource,
        uint mapperNowMilliseconds,
        EnbW3dSceneFogState sceneFog,
        EnbW3dFogPipelineSemantics fogPipeline,
        bool rendererSupportsFog = true,
        bool clearColorAndDepth = true,
        float? currentRetailPltDayNightPhase = null,
        bool compositeOpaqueCoverage = false,
        EnbOpenGlRenderTexture? borrowedStage0Texture = null,
        int viewportInset = 0,
        bool retailCapturePixelCenters = false,
        EnbW3dProgrammableTextureLanePlan? frameLane = null,
        EnbOpenGlIndexedUploadPlan? frameIndices = null,
        EnbW3dDrawTask? framePacket = null,
        EnbW3dLightingFrameUniforms? frameUniforms = null,
        Matrix4x4? frameGeneratedView = null, Matrix4x4? frameProjection = null)
    {
        ArgumentNullException.ThrowIfNull(gl);
        ArgumentNullException.ThrowIfNull(resource);
        EnsureInitialized();
        if (pixelWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(pixelWidth));
        if (pixelHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(pixelHeight));
        if (viewportInset < 0 || 2L * viewportInset >= pixelWidth || 2L * viewportInset >= pixelHeight)
            throw new ArgumentOutOfRangeException(nameof(viewportInset));
        if (borrowedStage0Texture is not null)
        {
            borrowedStage0Texture.Validate(gl);
            if (borrowedStage0Texture.Framebuffer == framebuffer)
                throw new InvalidOperationException("A capture target cannot sample itself while rendering.");
        }

        using var reuseSubmission = EnbWorkReuseDiagnostics.Measure("submit.textured");
        EnbW3dProgrammableTextureLanePlan texturedLane = frameLane ?? resource.LanePlan;
        var currentPacket=framePacket ?? texturedLane.Submission.Packet;
        EnbOpenGlIndexedUploadPlan indexed = frameIndices ?? resource.GeometryUploadPlan.IndexedPlan;
        EnbW3dProgrammableLightingLanePlan lane = texturedLane.LightingPlan;
        var uniforms=frameUniforms ?? EnbW3dLightingFrameUniforms.From(lane);
        var generatedView=frameGeneratedView ?? texturedLane.RetailGeneratedCoordinateViewFromWorld;
        var cameraProjection=frameProjection ?? texturedLane.CanonicalCameraProjection;
        if (EnbWorkReuseDiagnostics.Active)
        {
            using var inspection = EnbWorkReuseDiagnostics.Inspect();
            var packet = currentPacket;
            EnbWorkReuseDiagnostics.ObservePacket("submit.packet", packet);
            EnbWorkReuseDiagnostics.Observe("submit.target", (packet.RenderIdentity, packet.OrderKey, packet.RenderInstancePath),
                framebuffer, pixelWidth, pixelHeight, viewportInset, _program, clearColorAndDepth);
            EnbWorkReuseDiagnostics.Observe("submit.transforms", (packet.RenderIdentity, packet.OrderKey, packet.RenderInstancePath),
                uniforms.CanonicalClipFromModel, uniforms.CanonicalViewFromModel, uniforms.ModelToWorld, uniforms.NormalFromModel);
            EnbWorkReuseDiagnostics.Observe("submit.adjacentRequestedTarget", 0, framebuffer, pixelWidth, pixelHeight, viewportInset, _program);
            EnbWorkReuseDiagnostics.Add("submit.calls");
            EnbWorkReuseDiagnostics.Add("submit.runs", indexed.DrawCommands.Length);
        }
        if (texturedLane.RunStates.Length != indexed.DrawCommands.Length ||
            resource.TextureIdsByRun.Length != texturedLane.RunStates.Length ||
            resource.Texture1IdsByRun.Length != texturedLane.RunStates.Length ||
            resource.Mapper0RuntimeStates.Length != texturedLane.RunStates.Length ||
            resource.Mapper1RuntimeStates.Length != texturedLane.RunStates.Length)
        {
            throw new InvalidOperationException("R6G texture/run-state count no longer matches the R6A/OpenGL draw-command count.");
        }

        long stageClock=MeasureSubmissionStages?Stopwatch.GetTimestamp():0;
        if (_retainedSubmissionGl is not null && !ReferenceEquals(_retainedSubmissionGl, gl))
            throw new InvalidOperationException("Retained submission context changed inside a pass.");
        if (!_retainedDrawPending) DrainErrors(gl);
        if (!ReuseRetainedState(ref _retainedTarget, (framebuffer, pixelWidth, pixelHeight, viewportInset)))
        {
            _retainedDepthRange=null;
            gl.BindFramebuffer(GlConsts.GL_FRAMEBUFFER, framebuffer);
            gl.Viewport(viewportInset, viewportInset, pixelWidth - 2 * viewportInset, pixelHeight - 2 * viewportInset);
        }
        if (clearColorAndDepth)
        {
            gl.DepthMask(1);
            gl.ClearColor(0.012f, 0.028f, 0.050f, 1f);
            gl.ClearDepth(1.0f);
            gl.Clear(GlConsts.GL_COLOR_BUFFER_BIT | GlDepthBufferBit);
        }

        if (clearColorAndDepth) _retainedRaster = null;
        // Each draw below applies its authored run state before submission.
        // Preserve the empty-resource state transition, but avoid applying a
        // temporary lane state that the first run immediately overwrites.
        if (indexed.DrawCommands.IsEmpty)
            ApplyRasterState(gl, lane);
        if (!_retainedProgram) gl.UseProgram(_program);
        int hdr=gl.HighDynamicRange?1:0;
        if(_uploadedHdr!=hdr){gl.Uniform1i(_hdrUniform,hdr);_uploadedHdr=hdr;}
        if (_retainedSubmissionGl is not null)
        {
            _retainedProgram = true;
            // Establish cleanup responsibility before any operation can throw.
            _retainedDrawPending = true;
            _retainedLastVao = resource.VertexArray != 0;
            _retainedDraws++;
        }
        SubmissionStage(0,ref stageClock);
        var rasterClip = EnbOpenGlFirstW3dShaderBackend.CreateOpenGlClipFromCanonical(uniforms.CanonicalClipFromModel);
        if (retailCapturePixelCenters)
            rasterClip *= EnbOpenGlRenderTexture.RetailCaptureRasterTranslation(
                pixelWidth - 2 * viewportInset, pixelHeight - 2 * viewportInset);
        UploadInstanceMatrices(gl, rasterClip, uniforms.CanonicalClipFromModel,
            uniforms.CanonicalViewFromModel, uniforms.ModelToWorld, uniforms.NormalFromModel);
        var depth = EnbOpenGlSceneDepth.Uniforms(uniforms.ClipRange, uniforms.CanonicalClipFromModel);
        UploadVector4(_sceneDepthUniform,depth);
        uint requestedBias=currentPacket.CurrentRetailDepthBias;
        float depthBias=0;
        if(requestedBias!=0)
        {
            // Current D3D8 driver1017504B..A8 publishes -bias units and
            // -.25*bias slope (11E65FBC=3E800000), corroborated by native
            // flat/sloped D24 controls. The shader re-encodes canonical depth.
            // Attachment format is constant within this owned framebuffer
            // scope. External GL work and target changes invalidate it.
            double range;
            if(_retainedSubmissionGl is not null && _retainedDepthRange is {} cachedRange)range=cachedRange;
            else
            {
                RequireProc(_depthInteger,"glGetIntegerv")(0x0D56,out int bits);
                range=bits>0?Math.Pow(2,bits)-1:0;
                if(_retainedSubmissionGl is not null)_retainedDepthRange=range;
            }
            if(range>0)depthBias=(float)(requestedBias/range);
        }
        double near=uniforms.ClipRange.NearPlane,far=uniforms.ClipRange.FarPlane,span=far-near;
        UploadVector4(_sceneDepthBiasUniform,new(depthBias,requestedBias*.25f,(float)(near*far/span),(float)span));
        UploadSystemNumericsMatrix(gl, _retailGeneratedViewFromWorldUniform, generatedView);
        var effectProjection=currentPacket.CurrentRetailEffectProjection;
        UploadSystemNumericsMatrix(gl,_effectProjectionUniform,effectProjection ?? Matrix4x4.Identity);
        UploadInteger(_effectProjectionEnabledUniform,effectProjection.HasValue?1:0);
        var effectProjection1=currentPacket.CurrentRetailEffectProjection1;
        UploadSystemNumericsMatrix(gl,_effectProjection1Uniform,effectProjection1 ?? Matrix4x4.Identity);
        UploadInteger(_effectProjection1EnabledUniform,effectProjection1.HasValue?1:0);
        UploadVector4(_cameraWorldPositionUniform, new Vector4(uniforms.CameraWorldPosition, 0f));
        UploadVector4(_globalAmbientUniform, new Vector4(DecodeRgb24(uniforms.LightEnvironment.GlobalAmbientRgb24), 0f));
        UploadLightEnvironment(uniforms.LightEnvironment);
        bool anyMaterialFog = texturedLane.RunStates.Any(run => run.LightingRun.Run.Binding.Semantics.FixedFunction.FogMode != EnbW3dFogMode.Disabled);
        EnbOpenGlFogUniformState frameFog = EnbOpenGlFirstW3dShaderBackend.MapFog(
            anyMaterialFog ? EnbW3dFogMode.SceneColor : EnbW3dFogMode.Disabled,
            sceneFog,
            rendererSupportsFog,
            fogPipeline);
        UploadFogFrameState(frameFog,currentPacket.CurrentRetailTerrainDisplacement);
        UploadInteger(_texture0Uniform, 0);
        // Authored image rows start at the top; framebuffer texture rows start at the bottom.
        UploadInteger(_texture0FromFramebufferUniform, borrowedStage0Texture is null ? 0 : 1);
        UploadInteger(_texture1Uniform, 1);
        if (resource.VertexArray != 0)
        {
            if (!ReuseRetainedState(ref _retainedVao, resource.VertexArray))
                gl.BindVertexArray(resource.VertexArray);
        }
        else
        {
            if (_retainedVao is not null) gl.BindVertexArray(0);
            _retainedVao = null;
            gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, resource.VertexBuffer);
            BindVertexLayout(gl);
            gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, resource.TextureCoordinate1Buffer);
            BindTextureCoordinate1Layout(gl);
            gl.BindBuffer(GlConsts.GL_ELEMENT_ARRAY_BUFFER, resource.IndexBuffer);
        }

        int glIndexType = indexed.IndexElementType == EnbOpenGlIndexElementType.UnsignedShort
            ? GlConsts.GL_UNSIGNED_SHORT
            : GlUnsignedInt;
        int drawCalls = 0;
        int primitiveCount = 0; int vertexCount = 0;
        int indexCount = 0;
        int stage1SamplerIndex = 0;
        SubmissionStage(1,ref stageClock);
        for (int i = 0; i < indexed.DrawCommands.Length; i++)
        {
            EnbOpenGlIndexedDrawCommand command = indexed.DrawCommands[i];
            EnbW3dProgrammableTextureRunState textureRun = texturedLane.RunStates[i];
            if (EnbWorkReuseDiagnostics.Active)
            {
                using var inspection = EnbWorkReuseDiagnostics.Inspect();
                var packet = currentPacket;
                var key = (packet.RenderIdentity, packet.OrderKey, packet.RenderInstancePath, i);
                EnbWorkReuseDiagnostics.Observe("submit.runMaterial", key,
                    textureRun.LightingRun.Run.Binding.Semantics, textureRun.LightingRun.Run.Binding.VertexMaterial,
                    textureRun.LightingRun.Run.Binding.Shader, resource.TextureIdsByRun[i], resource.Texture1IdsByRun[i],
                    textureRun.Stage0Program, textureRun.Stage1Program, textureRun.Mapper0Program, textureRun.Mapper1Program,
                    compositeOpaqueCoverage, sceneFog, fogPipeline);
                EnbWorkReuseDiagnostics.Observe("submit.runRange", key, command);
                EnbWorkReuseDiagnostics.Observe("submit.adjacentRequestedMaterial", 0, textureRun.LightingRun.Run.Binding.Semantics,
                    resource.TextureIdsByRun[i], resource.Texture1IdsByRun[i], textureRun.Stage0Program, textureRun.Stage1Program);
                if(textureRun.Mapper0Program.RequiresMutableState)EnbWorkReuseDiagnostics.Add("submit.mutableMapperStages");
                if(textureRun.Mapper1Program.RequiresMutableState)EnbWorkReuseDiagnostics.Add("submit.mutableMapperStages");
            }
            if (!resource.NullMaterialCoordinateBuffers.IsEmpty)
            {
                bool nullMaterial = UsesNullMaterialCoordinates(textureRun);
                gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, nullMaterial
                    ? resource.NullMaterialCoordinateBuffers[0] : resource.VertexBuffer);
                gl.VertexAttribPointer(TextureCoordinate0AttributeIndex, 2, GlConsts.GL_FLOAT, 0,
                    nullMaterial ? 2 * sizeof(float) : EnbOpenGlLitShaderUploadPlan.StrideBytes,
                    nullMaterial ? IntPtr.Zero : new IntPtr(EnbOpenGlLitShaderUploadPlan.TextureCoordinate0OffsetBytes));
                gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, nullMaterial
                    ? resource.NullMaterialCoordinateBuffers[1] : resource.TextureCoordinate1Buffer);
                BindTextureCoordinate1Layout(gl);
            }
            EnbW3dProgrammableLightingRunState runState = textureRun.LightingRun;
            var expectedRun = resource.NPatchGeometry is {} patch
                ? patch.Runs[i] with { Binding = runState.Run.Binding } : runState.Run;
            if (!ReferenceEquals(command.SourceRun, expectedRun) && command.SourceRun != expectedRun)
                throw new InvalidOperationException($"R6G run state {i} no longer corresponds to its R6A draw command.");

            UploadRunState(runState);
            EnbW3dFixedFunctionState fixedFunction = runState.Run.Binding.Semantics.FixedFunction;
            ApplyRasterState(gl, fixedFunction.DepthCompare, fixedFunction.DepthWriteEnabled, fixedFunction.CullMode);
            ApplyBlendState(gl, fixedFunction, compositeOpaqueCoverage);
            ApplyAlphaTestState(gl, fixedFunction.AlphaTest);
            // Texture alpha can carry reflection/detail masks even on opaque geometry.
            // A transparent compositor surface needs coverage, unlike a retail backbuffer.
            // Keep alpha testing and all authored RGB/stage/blend operations intact.
            UploadScalar(gl,_opaqueCoverageUniform, compositeOpaqueCoverage && !fixedFunction.BlendEnabled ? 1f : 0f);
            ApplyFogState(gl, EnbOpenGlFirstW3dShaderBackend.MapFog(fixedFunction.FogMode, sceneFog, rendererSupportsFog, fogPipeline));
            UploadInteger(_stage0ProgramUniform, (int)textureRun.Stage0Program);
            UploadInteger(_stage1ColorProgramUniform, (int)textureRun.Stage1Program.Color);
            UploadInteger(_stage1AlphaProgramUniform, (int)textureRun.Stage1Program.Alpha);
            SubmissionStage(2,ref stageClock);
            EnbW3dMapperRuntimeEvaluation mapper0,mapper1;
            if (_materialScopeActive)
                _materialApplication.Evaluate(runState.Run.Binding.CurrentRetailNullMaterial ? null :
                    runState.Run.Binding.VertexMaterial?.Material,
                    resource.Mapper0RuntimeStates[i],resource.Mapper1RuntimeStates[i],mapperNowMilliseconds,
                    effectProjection.HasValue,effectProjection1.HasValue,out mapper0,out mapper1);
            else
            {
                mapper0 = effectProjection.HasValue
                    ? resource.Mapper0RuntimeStates[i].EvaluateMatrix(mapperNowMilliseconds)
                    : resource.Mapper0RuntimeStates[i].Evaluate(mapperNowMilliseconds);
                mapper1 = effectProjection1.HasValue
                    ? resource.Mapper1RuntimeStates[i].EvaluateMatrix(mapperNowMilliseconds)
                    : resource.Mapper1RuntimeStates[i].Evaluate(mapperNowMilliseconds);
            }
            if (currentRetailPltDayNightPhase is float pltDayNightPhase)
            {
                if (!EnbCurrentRetailPlanetRuntime.TryEvaluateDayNightMapperOverride(
                        textureRun.Texture0,
                        textureRun.Mapper0Program,
                        pltDayNightPhase,
                        out EnbW3dMapperRuntimeEvaluation mapper0Override,
                        out bool mapper0Applied,
                        out string? mapper0Diagnostic))
                {
                    throw new InvalidOperationException(
                        mapper0Diagnostic ?? "Current-retail PLT_DN mapper-0 override failed.");
                }

                if (!EnbCurrentRetailPlanetRuntime.TryEvaluateDayNightMapperOverride(
                        textureRun.Texture1,
                        textureRun.Mapper1Program,
                        pltDayNightPhase,
                        out EnbW3dMapperRuntimeEvaluation mapper1Override,
                        out bool mapper1Applied,
                        out string? mapper1Diagnostic))
                {
                    throw new InvalidOperationException(
                        mapper1Diagnostic ?? "Current-retail PLT_DN mapper-1 override failed.");
                }

                if (mapper0Applied)
                    mapper0 = mapper0Override;
                if (mapper1Applied)
                    mapper1 = mapper1Override;
            }
            if (currentPacket.CurrentRetailStage0UvOffset is Vector2 controlledOffset)
                mapper0 = new EnbW3dMapperRuntimeEvaluation(textureRun.Mapper0Program.BaseUniforms.Scale,
                    Vector2.Zero, controlledOffset, new Vector4(1f, 0f, 0f, 1f), false);
            if (currentPacket.CurrentRetailEffectMapper is { } effectMapper)
                mapper0=effectMapper;
            if (currentPacket.CurrentRetailEffectMapper1 is { } effectMapper1)
                mapper1=effectMapper1;
            if (effectProjection is { } passProjection)
                UploadSystemNumericsMatrix(gl, _effectProjectionUniform,
                    EnbCurrentRetailPassEffectDefinition.ComposeParent(passProjection,cameraProjection,
                        generatedView,textureRun.Mapper0Program,mapper0));
            if (effectProjection1 is { } passProjection1)
                UploadSystemNumericsMatrix(gl, _effectProjection1Uniform,
                    EnbCurrentRetailPassEffectDefinition.ComposeParent(passProjection1,cameraProjection,
                        generatedView,textureRun.Mapper1Program,mapper1));
            SubmissionStage(3,ref stageClock);
            UploadVector4(_mapper0ScaleOffsetUniform, mapper0.TextureScaleOffset);
            UploadVector4(_mapper0CrossUniform, mapper0.TextureCrossVector);
            UploadInteger(_mapper0KindUniform, (int)textureRun.Mapper0Program.Kind);
            UploadInteger(_mapper0SourceUniform, (int)textureRun.Mapper0Program.CoordinateSource);
            UploadInteger(_mapper0AxisUniform, (int)textureRun.Mapper0Program.Axis);
            UploadVector4(_mapper1ScaleOffsetUniform, mapper1.TextureScaleOffset);
            UploadVector4(_mapper1CrossUniform, mapper1.TextureCrossVector);
            UploadInteger(_mapper1KindUniform, (int)textureRun.Mapper1Program.Kind);
            UploadInteger(_mapper1SourceUniform, (int)textureRun.Mapper1Program.CoordinateSource);
            UploadInteger(_mapper1AxisUniform, (int)textureRun.Mapper1Program.Axis);
            UploadVector4(
                _perturbationMatrixUniform,
                mapper0.ConfiguresBumpEnvironment ? mapper0.BumpEnvironmentMatrix : textureRun.BumpEnvironment.Matrix2X2);
            UploadVector4(_perturbationLuminanceUniform, new Vector4(textureRun.BumpEnvironment.LuminanceScale, textureRun.BumpEnvironment.LuminanceOffset, 0f, 0f));
            SubmissionStage(4,ref stageClock);
            BindSubmissionTexture(GlTexture0, borrowedStage0Texture?.Texture ?? resource.TextureIdsByRun[i]);
            if (borrowedStage0Texture is null && resource.TextureIdsByRun[i] != 0)
                ApplySampler(GlTexture0, resource.TextureIdsByRun[i], textureRun.Texture0, textureRun.Sampler, resource.TextureUploadPlans[i].Mips.Length, fixedFunction.BlendEnabled);
            BindSubmissionTexture(GlTexture1, resource.Texture1IdsByRun[i]);
            if (textureRun.Texture1 is {} stage1Texture)
            {
                var stage1Plan = resource.Texture1UploadPlans[stage1SamplerIndex++];
                if (resource.Texture1IdsByRun[i] != 0)
                    ApplySampler(GlTexture1, resource.Texture1IdsByRun[i], stage1Texture, stage1Plan.Sampler, stage1Plan.Mips.Length, fixedFunction.BlendEnabled);
            }
            SubmissionStage(5,ref stageClock);
            gl.DrawElements(GlConsts.GL_TRIANGLES,command.IndexCount,glIndexType,
                new IntPtr((resource.SharedGeometry?.IndexByteOffset??0)+command.IndexByteOffset));
            SubmissionStage(6,ref stageClock);
            drawCalls++;
            primitiveCount = checked(primitiveCount + command.PrimitiveCount); vertexCount += unchecked((int)command.SourceRun.VertexCount);
            indexCount = checked(indexCount + command.IndexCount);
        }

        // Texture objects can be shared by immutable and mutable bindings.
        // Restore each draw's sampler, including draws without a runtime owner.
        void ApplySampler(int unit, int textureId, EnbW3dRenderTextureBinding binding, EnbW3dTextureSamplerSemantics initial, int mipCount, bool blended)
        {
            var sampler = binding.CurrentRetailTextureState is null ? initial :
                EnbW3dTextureSamplerCompiler.Compile(binding, texturedLane.RuntimeSettings, texturedLane.BackendCapabilities);
            if(gl.SceneAnisotropicFiltering)
                sampler=EnbW3dTextureSamplerCompiler.ApplySceneAnisotropy(binding,sampler,mipCount,
                    currentPacket.CurrentRetailEffectMapper.HasValue || blended,gl.MaximumSupportedAnisotropy);
            var state = (EnbOpenGlStage0TextureUploadPlanner.MapAddressMode(sampler.AddressU),
                EnbOpenGlStage0TextureUploadPlanner.MapAddressMode(sampler.AddressV),
                EnbOpenGlStage0TextureUploadPlanner.MapMinFilter(sampler, mipCount),
                sampler.MagFilter == EnbRenderTextureMinMagFilter.Point ? 0x2600 : 0x2601, (float)sampler.MaximumAnisotropy);
            if (_appliedSamplers.TryGetValue(textureId, out var previous) && previous == state) return;
            ActivateSubmissionTexture(unit);
            var parameter = RequireProc(_texParameteri, "glTexParameteri");
            parameter(GlTexture2D, GlTextureWrapS, state.Item1);
            parameter(GlTexture2D, GlTextureWrapT, state.Item2);
            parameter(GlTexture2D, GlTextureMinFilter, state.Item3);
            parameter(GlTexture2D, GlTextureMagFilter, state.Item4);
            if(gl.MaximumSupportedAnisotropy>1)RequireProc(_texParameterf,"glTexParameterf")(GlTexture2D,GlTextureMaxAnisotropyExt,state.Item5);
            _appliedSamplers[textureId] = state;
        }

        if (_retainedSubmissionGl is null)
            CleanupSubmissionBindings(gl, resource.VertexArray != 0);

        int error = _retainedSubmissionGl is null ? gl.GetError() : GlConsts.GL_NO_ERROR;
        SubmissionStage(7,ref stageClock);
        return new EnbOpenGlSubmissionReceipt(drawCalls, primitiveCount, indexCount, error) { VertexCount = vertexCount };
    }

    private static bool UsesNullMaterialCoordinates(EnbW3dProgrammableTextureRunState run)
        => run.LightingRun.Run.Binding.CurrentRetailNullMaterial;

    public void DestroyResource(GlInterface? gl, EnbOpenGlStage0TextureMeshResource resource)
    {
        InterruptRetainedSubmission();
        if (gl is null) return;
        ArgumentNullException.ThrowIfNull(resource);
        foreach (int buffer in resource.NullMaterialCoordinateBuffers) gl.DeleteBuffer(buffer);
        foreach (int textureId in resource.TextureIdsByRun.Concat(resource.Texture1IdsByRun).Where(id => id != 0).Distinct())
            DeleteTexture(textureId);
        if(resource.SharedGeometry is {} shared)
        {
            ReleaseSharedGeometry(gl,shared);
            return;
        }
        if (resource.VertexArray != 0 && gl.IsDeleteVertexArraysAvailable)
            gl.DeleteVertexArray(resource.VertexArray);
        if (resource.IndexBuffer != 0)
            gl.DeleteBuffer(resource.IndexBuffer);
        if (resource.TextureCoordinate1Buffer != 0)
            gl.DeleteBuffer(resource.TextureCoordinate1Buffer);
        if (resource.VertexBuffer != 0)
            gl.DeleteBuffer(resource.VertexBuffer);
    }

    private void AdoptResourceContext(GlInterface? gl)
    {
        if(gl is not null && ReferenceEquals(_geometryContext,gl))return;
        _sharedImageTextures.Clear();_imageTextureKeys.Clear();
        _sharedRuntimeTextures.Clear();_runtimeTextureKeys.Clear();_runtimeTextureImages.Clear();
        _sharedGeometry.Clear();_geometryContainers.Clear();_geometryDeviceOwner=new();_geometryContext=gl;
    }

    public void Deinitialize(GlInterface? gl)
    {
        InterruptRetainedSubmission();
        _appliedSamplers.Clear();
        // A live-context program rebuild leaves mesh resources alive. Their
        // texture and geometry leases must keep their reference counts until
        // the final mesh releases them. Only actual context loss forgets the
        // device ownership tables without issuing resource deletion calls.
        if(gl is null) AdoptResourceContext(null);
        InvalidateUniformCache();
        // No GL calls after context loss.
        if (_program != 0)
            gl?.DeleteProgram(_program);
        _program = 0;
        _uploadedHdr = -1;
        _clipFromModelUniform = -1;
        _canonicalClipFromModelUniform = -1;
        _viewFromModelUniform = -1;
        _sceneDepthUniform = -1;
        _sceneDepthBiasUniform = -1;
        _retailGeneratedViewFromWorldUniform = -1;
        _worldFromModelUniform = -1;
        _effectProjectionUniform = -1;
        _effectProjectionEnabledUniform = -1;
        _effectProjection1Uniform = -1;
        _effectProjection1EnabledUniform = -1;
        _normalFromModelUniform = -1;
        _cameraWorldPositionUniform = -1;
        _globalAmbientUniform = -1;
        _lightingEnabledUniform = -1;
        _materialAmbientUniform = -1;
        _materialDiffuseUniform = -1;
        _materialSpecularUniform = -1;
        _materialEmissiveUniform = -1;
        _materialSourcesUniform = -1;
        _materialPowerSpecularUniform = -1;
        _texture0Uniform = -1;
        _texture0FromFramebufferUniform = -1;
        _texture1Uniform = -1;
        _stage0ProgramUniform = -1;
        _stage1ColorProgramUniform = -1;
        _stage1AlphaProgramUniform = -1;
        _mapper0ScaleOffsetUniform = -1;
        _mapper0CrossUniform = -1;
        _mapper0KindUniform = -1;
        _mapper0SourceUniform = -1;
        _mapper0AxisUniform = -1;
        _mapper1ScaleOffsetUniform = -1;
        _mapper1CrossUniform = -1;
        _mapper1KindUniform = -1;
        _mapper1SourceUniform = -1;
        _mapper1AxisUniform = -1;
        _perturbationMatrixUniform = -1;
        _perturbationLuminanceUniform = -1;
        _opaqueCoverageUniform = -1;
        _alphaTestModeUniform = -1;
        _alphaTestReferenceUniform = -1;
        _fogRangeUniform = -1;
        _fogEnabledUniform = -1;
        _fogColorUniform = -1;
        _lightUniforms = ImmutableArray<LightUniformLocations>.Empty;
        _uniform4f = null;
        _uniform1i = null;
        _cullFace = null;
        _blendFunc = null;
        _blendFuncSeparate = null;
        _frontFace = null;
        _genTextures = null;
        _deleteTextures = null;
        _activeTexture = null;
        _bindTexture = null;
        _texParameteri = null;
        _texParameterf = null;
        _pixelStorei = null;
        _texImage2D = null;
        _compressedTexImage2D = null;
        ContextIdentity = null;
    }

    public static (string VertexShader, string FragmentShader) CreateShaderSources(GlVersion version)
    {
        if (!IsSupportedShaderContext(version))
            throw new NotSupportedException($"Unsupported OpenGL shader context {version}.");

        string uniforms = CreateLightingUniformDeclarations();
        string body = CreateLightingVertexBody();
        const string stage0ProgramBody =
            "vec4 applyStage0(vec4 texel,vec4 diffuse){\n" +
            " if(uStage0Program==0) return texel;\n" +
            " if(uStage0Program==1) return texel*diffuse;\n" +
            " if(uStage0Program==2) return vec4(texel.rgb+diffuse.rgb,texel.a*diffuse.a);\n" +
            " if(uStage0Program==3) return vec4(2.0*texel.rgb*diffuse.rgb,texel.a*diffuse.a);\n" +
            " return vec4(1.0,0.0,1.0,1.0);\n" +
            "}\n";
        const string stage1ProgramBody =
            "vec3 applyStage1Color(vec4 texel,vec4 current){\n" +
            " if(uStage1ColorProgram==0||uStage1ColorProgram==1) return current.rgb;\n" +
            " if(uStage1ColorProgram==2) return texel.rgb;\n" +
            " if(uStage1ColorProgram==3) return texel.rgb*current.rgb;\n" +
            " if(uStage1ColorProgram==4) return texel.rgb+current.rgb-texel.rgb*current.rgb;\n" +
            " if(uStage1ColorProgram==5) return texel.rgb+current.rgb;\n" +
            " if(uStage1ColorProgram==6) return texel.rgb-current.rgb;\n" +
            " if(uStage1ColorProgram==7) return current.rgb-texel.rgb;\n" +
            " if(uStage1ColorProgram==8) return texel.rgb*texel.a+current.rgb*(1.0-texel.a);\n" +
            " if(uStage1ColorProgram==9) return texel.rgb*current.a+current.rgb*(1.0-current.a);\n" +
            " if(uStage1ColorProgram==10) return texel.rgb+current.rgb-vec3(0.5);\n" +
            " if(uStage1ColorProgram==11) return 2.0*(texel.rgb+current.rgb-vec3(0.5));\n" +
            " if(uStage1ColorProgram==12) return 2.0*texel.rgb*current.rgb;\n" +
            " if(uStage1ColorProgram==13) return current.rgb+current.a*texel.rgb;\n" +
            " return vec3(1.0,0.0,1.0);\n" +
            "}\n" +
            "float applyStage1Alpha(vec4 texel,vec4 current){\n" +
            " if(uStage1AlphaProgram==0||uStage1AlphaProgram==1) return current.a;\n" +
            " if(uStage1AlphaProgram==2) return texel.a;\n" +
            " if(uStage1AlphaProgram==3) return texel.a*current.a;\n" +
            " if(uStage1AlphaProgram==4) return texel.a+current.a-texel.a*current.a;\n" +
            " return 1.0;\n" +
            "}\n";
        const string alphaTestProgramModern =
            "uniform bool uHighDynamicRange;\nvec3 lightRange(vec3 c){return uHighDynamicRange?max(c,vec3(0.0)):clamp(c,0.0,1.0);}\nvec4 stageRange(vec4 c){return vec4(lightRange(c.rgb),clamp(c.a,0.0,1.0));}\nbool alphaTestPass(float alpha){ if(uAlphaTestMode<0.5) return true; if(uAlphaTestMode<1.5) return alpha<=uAlphaTestReference; return alpha>=uAlphaTestReference; }\n" +
            "vec4 applyFog(vec4 color){ if(uFogEnabled>0.5) color.rgb=mix(uFogColor.rgb,color.rgb,vFogVisibility); return color; }\n" +
            "void writeColor(vec4 color){ if(!alphaTestPass(color.a)) discard; if(uOpaqueCoverage>0.5) color.a=1.0; fragColor=applyFog(color); }\n";
        const string alphaTestProgramLegacy =
            "uniform bool uHighDynamicRange;\nvec3 lightRange(vec3 c){return uHighDynamicRange?max(c,vec3(0.0)):clamp(c,0.0,1.0);}\nvec4 stageRange(vec4 c){return vec4(lightRange(c.rgb),clamp(c.a,0.0,1.0));}\nbool alphaTestPass(float alpha){ if(uAlphaTestMode<0.5) return true; if(uAlphaTestMode<1.5) return alpha<=uAlphaTestReference; return alpha>=uAlphaTestReference; }\n" +
            "vec4 applyFog(vec4 color){ if(uFogEnabled>0.5) color.rgb=mix(uFogColor.rgb,color.rgb,vFogVisibility); return color; }\n" +
            "void writeColor(vec4 color){ if(!alphaTestPass(color.a)) discard; if(uOpaqueCoverage>0.5) color.a=1.0; gl_FragColor=applyFog(color); }\n";
        const string fragmentBodyModern =
            "void main(){ if(uStage0Program==4||uStage0Program==5){ vec4 tex0=texture(uTexture0,stage0Coordinates(vTexCoord0)); vec2 duv=clamp((tex0.rg*255.0-vec2(128.0))/127.0,vec2(-1.0),vec2(1.0)); vec2 delta=vec2(uPerturbationMatrix.x*duv.x+uPerturbationMatrix.z*duv.y,uPerturbationMatrix.y*duv.x+uPerturbationMatrix.w*duv.y); vec4 tex1=texture(uTexture1,vTexCoord1+delta); if(uStage0Program==5) tex1.rgb*=tex0.b*uPerturbationLuminance.x+uPerturbationLuminance.y; vec4 stage1=stageRange(vec4(applyStage1Color(tex1,vDiffuse),applyStage1Alpha(tex1,vDiffuse))); writeColor(vec4(lightRange(stage1.rgb+vSpecular),clamp(stage1.a,0.0,1.0))); return; } vec4 stage0; if(uStage0Program==6) stage0=vDiffuse; else stage0=stageRange(applyStage0(texture(uTexture0,stage0Coordinates(vTexCoord0)),vDiffuse)); if(uStage1ColorProgram==0&&uStage1AlphaProgram==0){ writeColor(vec4(lightRange(stage0.rgb+vSpecular),clamp(stage0.a,0.0,1.0))); return; } vec4 tex1=texture(uTexture1,vTexCoord1); vec4 stage1=stageRange(vec4(applyStage1Color(tex1,stage0),applyStage1Alpha(tex1,stage0))); writeColor(vec4(lightRange(stage1.rgb+vSpecular),clamp(stage1.a,0.0,1.0))); }\n";
        const string fragmentBodyLegacy =
            "void main(){ if(uStage0Program==4||uStage0Program==5){ vec4 tex0=texture2D(uTexture0,stage0Coordinates(vTexCoord0)); vec2 duv=clamp((tex0.rg*255.0-vec2(128.0))/127.0,vec2(-1.0),vec2(1.0)); vec2 delta=vec2(uPerturbationMatrix.x*duv.x+uPerturbationMatrix.z*duv.y,uPerturbationMatrix.y*duv.x+uPerturbationMatrix.w*duv.y); vec4 tex1=texture2D(uTexture1,vTexCoord1+delta); if(uStage0Program==5) tex1.rgb*=tex0.b*uPerturbationLuminance.x+uPerturbationLuminance.y; vec4 stage1=stageRange(vec4(applyStage1Color(tex1,vDiffuse),applyStage1Alpha(tex1,vDiffuse))); writeColor(vec4(lightRange(stage1.rgb+vSpecular),clamp(stage1.a,0.0,1.0))); return; } vec4 stage0; if(uStage0Program==6) stage0=vDiffuse; else stage0=stageRange(applyStage0(texture2D(uTexture0,stage0Coordinates(vTexCoord0)),vDiffuse)); if(uStage1ColorProgram==0&&uStage1AlphaProgram==0){ writeColor(vec4(lightRange(stage0.rgb+vSpecular),clamp(stage0.a,0.0,1.0))); return; } vec4 tex1=texture2D(uTexture1,vTexCoord1); vec4 stage1=stageRange(vec4(applyStage1Color(tex1,stage0),applyStage1Alpha(tex1,stage0))); writeColor(vec4(lightRange(stage1.rgb+vSpecular),clamp(stage1.a,0.0,1.0))); }\n";

        const string modernVertexIo = "in vec3 aPosition;\nin vec3 aNormal;\nin vec4 aColor1;\nin vec2 aTexCoord0;\nin vec2 aTexCoord1;\nout mediump vec4 vDiffuse;\nout mediump vec3 vSpecular;\nout mediump vec2 vTexCoord0;\nout mediump vec2 vTexCoord1;\nout mediump float vFogVisibility;\n";
        const string legacyVertexIo = "attribute vec3 aPosition;\nattribute vec3 aNormal;\nattribute vec4 aColor1;\nattribute vec2 aTexCoord0;\nattribute vec2 aTexCoord1;\nvarying mediump vec4 vDiffuse;\nvarying mediump vec3 vSpecular;\nvarying mediump vec2 vTexCoord0;\nvarying mediump vec2 vTexCoord1;\nvarying mediump float vFogVisibility;\n";
        const string modernFragmentIo = "in mediump vec4 vDiffuse;\nin mediump vec3 vSpecular;\nin mediump vec2 vTexCoord0;\nin mediump vec2 vTexCoord1;\nin mediump float vFogVisibility;\nuniform sampler2D uTexture0;\nuniform int uTexture0FromFramebuffer;\nvec2 stage0Coordinates(vec2 uv){ return uTexture0FromFramebuffer!=0 ? vec2(uv.x,1.0-uv.y) : uv; }\nuniform sampler2D uTexture1;\nuniform int uStage0Program;\nuniform int uStage1ColorProgram;\nuniform int uStage1AlphaProgram;\nuniform vec4 uPerturbationMatrix;\nuniform vec4 uPerturbationLuminance;\nuniform float uOpaqueCoverage;\nuniform float uAlphaTestMode;\nuniform float uAlphaTestReference;\nuniform float uFogEnabled;\nuniform vec4 uFogColor;\n";
        const string legacyFragmentIo = "varying mediump vec4 vDiffuse;\nvarying mediump vec3 vSpecular;\nvarying mediump vec2 vTexCoord0;\nvarying mediump vec2 vTexCoord1;\nvarying mediump float vFogVisibility;\nuniform sampler2D uTexture0;\nuniform int uTexture0FromFramebuffer;\nvec2 stage0Coordinates(vec2 uv){ return uTexture0FromFramebuffer!=0 ? vec2(uv.x,1.0-uv.y) : uv; }\nuniform sampler2D uTexture1;\nuniform int uStage0Program;\nuniform int uStage1ColorProgram;\nuniform int uStage1AlphaProgram;\nuniform vec4 uPerturbationMatrix;\nuniform vec4 uPerturbationLuminance;\nuniform float uOpaqueCoverage;\nuniform float uAlphaTestMode;\nuniform float uAlphaTestReference;\nuniform float uFogEnabled;\nuniform vec4 uFogColor;\n";

        if (version.Type == GlProfileType.OpenGLES)
        {
            if (version.Major >= 3)
            {
                return (
                    "#version 300 es\nprecision highp float;\n" + modernVertexIo + uniforms + body,
                    "#version 300 es\nprecision mediump float;\n" + modernFragmentIo + "out vec4 fragColor;\n" + alphaTestProgramModern + stage0ProgramBody + stage1ProgramBody + fragmentBodyModern);
            }

            return (
                "precision highp float;\n" + legacyVertexIo + uniforms + body,
                "precision mediump float;\n" + legacyFragmentIo + alphaTestProgramLegacy + stage0ProgramBody + stage1ProgramBody + fragmentBodyLegacy);
        }

        if (version.Major > 3 || (version.Major == 3 && version.Minor >= 2))
        {
            string desktopVertexIo = modernVertexIo.Replace("mediump ", string.Empty, StringComparison.Ordinal);
            string desktopFragmentIo = modernFragmentIo.Replace("mediump ", string.Empty, StringComparison.Ordinal);
            return (
                "#version 150\n" + desktopVertexIo + uniforms + body,
                "#version 150\n" + desktopFragmentIo + "out vec4 fragColor;\n" + alphaTestProgramModern + stage0ProgramBody + stage1ProgramBody + fragmentBodyModern);
        }

        string legacyVersion = version.Major == 2 && version.Minor == 0 ? "110" : "120";
        string desktopLegacyVertexIo = legacyVertexIo.Replace("mediump ", string.Empty, StringComparison.Ordinal);
        string desktopLegacyFragmentIo = legacyFragmentIo.Replace("mediump ", string.Empty, StringComparison.Ordinal);
        return (
            $"#version {legacyVersion}\n{desktopLegacyVertexIo}{uniforms}{body}",
            $"#version {legacyVersion}\n{desktopLegacyFragmentIo}{alphaTestProgramLegacy}{stage0ProgramBody}{stage1ProgramBody}{fragmentBodyLegacy}");
    }

    public static bool SupportsAnisotropicFiltering(IReadOnlySet<string> extensions)
    {
        ArgumentNullException.ThrowIfNull(extensions);
        return extensions.Contains("GL_EXT_texture_filter_anisotropic") ||
               extensions.Contains("GL_ARB_texture_filter_anisotropic");
    }

    public static bool SupportsTextureMaximumLevel(GlVersion version)
        => version.Type switch
        {
            GlProfileType.OpenGL => version.Major >= 2,
            GlProfileType.OpenGLES => version.Major >= 3,
            _ => false,
        };

    private static string CreateLightingUniformDeclarations()
    {
        var source = new StringBuilder();
        source.Append(
            "uniform bool uHighDynamicRange;\nvec3 lightRange(vec3 c){return uHighDynamicRange?max(c,vec3(0.0)):clamp(c,0.0,1.0);}\nvec4 stageRange(vec4 c){return vec4(lightRange(c.rgb),clamp(c.a,0.0,1.0));}\nuniform mat4 uInstanceMatrices[5];\n#define uClipFromModel uInstanceMatrices[0]\n" +
            "#define uCanonicalClipFromModel uInstanceMatrices[1]\n" +
            "#define uViewFromModel uInstanceMatrices[2]\n" +
            "uniform vec4 uFogRange;\n" +
            "uniform mat4 uRetailGeneratedViewFromWorld;\n" +
            "#define uWorldFromModel uInstanceMatrices[3]\n" +
            "uniform mat4 uEffectProjection;\n" +
            "uniform int uEffectProjectionEnabled;\n" +
            "uniform mat4 uEffectProjection1;\n" +
            "uniform int uEffectProjection1Enabled;\n" +
            "#define uNormalFromModel uInstanceMatrices[4]\n" +
            "uniform vec4 uCameraWorldPosition;\n" +
            "uniform vec4 uGlobalAmbient;\n" +
            "uniform int uLightingEnabled;\n" +
            "uniform vec4 uMaterialAmbient;\n" +
            "uniform vec4 uMaterialDiffuse;\n" +
            "uniform vec4 uMaterialSpecular;\n" +
            "uniform vec4 uMaterialEmissive;\n" +
            "uniform vec4 uMaterialSources;\n" +
            "uniform vec4 uMaterialPowerSpecular;\n" +
            "uniform vec4 uMapper0ScaleOffset;\n" +
            "uniform vec4 uMapper0Cross;\n" +
            "uniform int uMapper0Kind;\n" +
            "uniform int uMapper0Source;\n" +
            "uniform int uMapper0Axis;\n" +
            "uniform vec4 uMapper1ScaleOffset;\n" +
            "uniform vec4 uMapper1Cross;\n" +
            "uniform int uMapper1Kind;\n" +
            "uniform int uMapper1Source;\n" +
            "uniform int uMapper1Axis;\n");
        for (int i = 0; i < EnbW3dLightEnvironment.CurrentRetailLightSlotCount; i++)
        {
            source.Append($"uniform vec4 uLight{i}State;\n");
            source.Append($"uniform vec4 uLight{i}Diffuse;\n");
            source.Append($"uniform vec4 uLight{i}Specular;\n");
            source.Append($"uniform vec4 uLight{i}Ambient;\n");
            source.Append($"uniform vec4 uLight{i}PositionPhi;\n");
            source.Append($"uniform vec4 uLight{i}DirectionTheta;\n");
            source.Append($"uniform vec4 uLight{i}Attenuation;\n");
        }
        return source.ToString();
    }

    private static string CreateLightingVertexBody()
    {
        var calls = new StringBuilder();
        for (int i = 0; i < EnbW3dLightEnvironment.CurrentRetailLightSlotCount; i++)
        {
            calls.Append($" addLight(uLight{i}State,uLight{i}Diffuse,uLight{i}Specular,uLight{i}Ambient,uLight{i}PositionPhi,uLight{i}DirectionTheta,uLight{i}Attenuation,worldPosition,worldNormal,ambientReflectance,diffuseReflectance.rgb,specularReflectance,ambient,diffuse,specular);\n");
        }

        return
            "vec3 resolveRgb(float source, vec3 materialRgb, vec4 color1){ return source < 0.5 ? materialRgb : color1.rgb; }\n" +
            "vec4 resolveDiffuse(float source, vec4 materialRgba, vec4 color1){ return source < 0.5 ? materialRgba : color1; }\n" +
            "float fogVisibility(vec3 p){ if(uFogRange.x==0.0 && uFogRange.y==0.0) return 0.0; float d=uFogRange.z>0.5?length(p):abs(p.z); return clamp((uFogRange.y-d)/(uFogRange.y-uFogRange.x),0.0,1.0); }\n" +
            "vec3 reflectLocalViewer(vec3 eyeToPosition,vec3 normal){ float d=length(eyeToPosition); vec3 e=d>0.0?-eyeToPosition/d:vec3(0.0); return 2.0*dot(e,normal)*normal-e; }\n" +
            "vec3 cameraMapperSource(int source,vec3 worldPosition,vec3 worldNormal){ vec3 p=(uRetailGeneratedViewFromWorld*vec4(worldPosition,1.0)).xyz; vec3 n=(uRetailGeneratedViewFromWorld*vec4(worldNormal,0.0)).xyz; if(source==1) return n; if(source==2) return p; if(source==3) return reflectLocalViewer(p,n); return vec3(0.0); }\n" +
            "vec3 worldMapperSource(int source,vec3 worldPosition,vec3 worldNormal){ if(source==1) return worldNormal; if(source==3) return reflectLocalViewer(worldPosition-uCameraWorldPosition.xyz,worldNormal); return vec3(0.0); }\n" +
            "vec2 affineMapper(vec2 source,vec4 scaleOffset,vec4 cross){ return vec2(source.x*scaleOffset.x+source.y*cross.x+scaleOffset.z,source.x*cross.y+source.y*scaleOffset.y+scaleOffset.w); }\n" +
            "vec2 axisMapper(vec3 source,int axis,vec4 scaleOffset,vec4 cross){ vec2 plane=axis==0?source.yz:(axis==1?source.xz:source.xy); return affineMapper(plane,scaleOffset,cross); }\n" +
            "vec2 applyMapper(int kind,int source,int axis,vec2 passUv,vec3 modelPosition,vec3 worldPosition,vec3 worldNormal,vec4 scaleOffset,vec4 cross){ vec3 cameraSource=cameraMapperSource(source,worldPosition,worldNormal); if(kind==1) return affineMapper(cameraSource.xy,scaleOffset,cross); if(kind==2){ vec4 c=uCanonicalClipFromModel*vec4(modelPosition,1.0); return vec2((c.x*scaleOffset.x+c.w*scaleOffset.z)/c.z,(c.y*scaleOffset.y+c.w*scaleOffset.w)/c.z); } if(kind==5&&source!=0) return affineMapper(cameraSource.xy,scaleOffset,cross); if(kind==10) return axisMapper(worldMapperSource(source,worldPosition,worldNormal),axis,scaleOffset,cross); if(kind==12) return vec2(cameraSource.z*scaleOffset.x+scaleOffset.z,scaleOffset.w); if(kind==14) return axisMapper(worldMapperSource(source,worldPosition,worldNormal),axis,scaleOffset,cross); return affineMapper(passUv,scaleOffset,cross); }\n" +
            "void addLight(vec4 state,vec4 lightDiffuse,vec4 lightSpecular,vec4 lightAmbient,vec4 positionPhi,vec4 directionTheta,vec4 atten,vec3 vertexPosition,vec3 normal,vec3 ca,vec3 cd,vec3 cs,inout vec3 ambient,inout vec3 diffuse,inout vec3 specular){\n" +
            " if(state.x < 0.5) return; vec3 ldir; float attenuation=1.0; float spot=1.0;\n" +
            " if(state.y > 2.5){ float dl=length(directionTheta.xyz); if(dl==0.0) return; ldir=-directionTheta.xyz/dl; }\n" +
            " else { vec3 delta=positionPhi.xyz-vertexPosition; float d=length(delta); if(d>state.z || d==0.0) return; ldir=delta/d; float denom=atten.x+atten.y*d+atten.z*d*d; if(denom==0.0) return; attenuation=1.0/denom;\n" +
            "  if(state.y>1.5){ float sdl=length(directionTheta.xyz); if(sdl==0.0) return; vec3 negDir=-directionTheta.xyz/sdl; float rho=dot(negDir,ldir); float cosTheta=cos(directionTheta.w*0.5); float cosPhi=cos(positionPhi.w*0.5); if(rho>=cosTheta) spot=1.0; else if(rho<=cosPhi) spot=0.0; else spot=pow((rho-cosPhi)/(cosTheta-cosPhi),state.w); if(spot==0.0) return; } }\n" +
            " float scaled=attenuation*spot; ambient += ca*lightAmbient.rgb*scaled; float ndl=dot(normal,ldir); diffuse += cd*lightDiffuse.rgb*(max(ndl,0.0)*scaled);\n" +
            " if(uMaterialPowerSpecular.y>0.5 && ndl>=0.0){ vec3 viewer=normalize(uCameraWorldPosition.xyz-vertexPosition); vec3 halfway=normalize(viewer+ldir); float ndh=dot(normal,halfway); float coeff=ndh<0.0?0.0:pow(ndh,uMaterialPowerSpecular.x); specular += cs*lightSpecular.rgb*(coeff*scaled); }\n" +
            "}\n" +
            "void main(){\n" +
            " vec4 world=uWorldFromModel*vec4(aPosition,1.0); vec3 worldPosition=world.xyz; vec3 worldNormal=(uNormalFromModel*vec4(aNormal,0.0)).xyz;\n" +
            " vec3 ambientReflectance=resolveRgb(uMaterialSources.x,uMaterialAmbient.rgb,aColor1); vec4 diffuseReflectance=resolveDiffuse(uMaterialSources.y,uMaterialDiffuse,aColor1); vec3 specularReflectance=uMaterialSpecular.rgb; vec3 emissive=resolveRgb(uMaterialSources.w,uMaterialEmissive.rgb,aColor1);\n" +
            " vec3 ambient=ambientReflectance*uGlobalAmbient.rgb; vec3 diffuse=vec3(0.0); vec3 specular=vec3(0.0);\n" +
            " if(uLightingEnabled!=0){\n" + calls + " }\n" +
            " vec3 fogPosition=(uViewFromModel*vec4(aPosition,1.0)).xyz; vFogVisibility=fogVisibility(fogPosition); if(uFogRange.w>0.5){ vec4 clip=uClipFromModel*vec4(aPosition,1.0); float z=(clip.z+clip.w)*0.5; vFogVisibility=clamp((uFogRange.y-z)/(uFogRange.y-uFogRange.x),0.0,1.0); } vDiffuse=uLightingEnabled!=0?vec4(lightRange(emissive+ambient+diffuse),clamp(diffuseReflectance.a,0.0,1.0)):aColor1; vSpecular=uLightingEnabled!=0?lightRange(specular):vec3(0.0); vTexCoord0=applyMapper(uMapper0Kind,uMapper0Source,uMapper0Axis,aTexCoord0,aPosition,worldPosition,worldNormal,uMapper0ScaleOffset,uMapper0Cross); vTexCoord1=applyMapper(uMapper1Kind,uMapper1Source,uMapper1Axis,aTexCoord1,aPosition,worldPosition,worldNormal,uMapper1ScaleOffset,uMapper1Cross); gl_Position=uClipFromModel*vec4(aPosition,1.0);\n" +
            " if(uEffectProjectionEnabled!=0){ vec2 projected=(uEffectProjection*world).xy; vTexCoord0=projected; }\n" +
            // 00980E50 supplies (projected U,V,1,1) to the parent matrix.
            // Mode11 consumes Z, not XY; wrapping it therefore gives uniform U.
            " if(uEffectProjection1Enabled!=0){ vec2 projected=(uEffectProjection1*world).xy; vTexCoord1=projected; }\n" +
            "}\n";
    }

    private static uint CurrentMapperClockMilliseconds()
        => unchecked((uint)Environment.TickCount);

    private void UploadFogFrameState(EnbOpenGlFogUniformState state,bool terrainDisplacement=false)
    {
        UploadVector4(_fogRangeUniform, new Vector4(
            state.Start,
            state.End,
            state.DistanceSource == EnbW3dFogDistanceSource.CameraRange ? 1f : 0f,
            terrainDisplacement?1f:0f));
    }

    private void ApplyFogState(GlInterface gl, EnbOpenGlFogUniformState state)
    {
        UploadScalar(gl,_fogEnabledUniform, state.Enabled ? 1f : 0f);
        UploadVector4(_fogColorUniform, new Vector4(state.Color, 0f));
    }

    private void ApplyAlphaTestState(GlInterface gl, EnbW3dAlphaTestSemantics alphaTest)
    {
        EnbOpenGlAlphaTestUniformState state = EnbOpenGlFirstW3dShaderBackend.MapAlphaTest(alphaTest);
        UploadScalar(gl,_alphaTestModeUniform, state.Mode);
        UploadScalar(gl,_alphaTestReferenceUniform, state.Reference);
    }

    private void ApplyBlendState(GlInterface gl, EnbW3dFixedFunctionState state, bool compositeCoverage)
    {
        if (ReuseRetainedState(ref _retainedBlend, (state.BlendEnabled,
            (int)state.SourceBlend, (int)state.DestinationBlend, compositeCoverage))) return;
        if (!state.BlendEnabled)
        {
            gl.Disable(GlBlend);
            return;
        }

        _blendFunc ??= LoadRequiredProc<GlBlendFuncProc>(gl, "glBlendFunc");
        gl.Enable(GlBlend);
        if(compositeCoverage)
        {
            // Material RGB blending (including the eye's multiply pass) must not
            // erase coverage already written by the underlying opaque surface.
            // The procedure belongs to this GL context, just like glBlendFunc.
            // Keep the authored factors unchanged and discard it on context release.
            var blendSeparate=RetainBlendProcedure
                ? _blendFuncSeparate ??= LoadRequiredProc<GlBlendFuncSeparateProc>(gl,"glBlendFuncSeparate")
                : LoadRequiredProc<GlBlendFuncSeparateProc>(gl,"glBlendFuncSeparate");
            blendSeparate(
                EnbOpenGlFirstW3dShaderBackend.MapBlendFactor(state.SourceBlend),
                EnbOpenGlFirstW3dShaderBackend.MapBlendFactor(state.DestinationBlend),1,0x0303);
            return;
        }
        _blendFunc(
            EnbOpenGlFirstW3dShaderBackend.MapBlendFactor(state.SourceBlend),
            EnbOpenGlFirstW3dShaderBackend.MapBlendFactor(state.DestinationBlend));
    }

    private (EnbW3dMaterialUniforms Material,EnbW3dVertexLightingSemantics Lighting,bool Specular)? _uploadedMaterial;
    private void UploadRunState(EnbW3dProgrammableLightingRunState runState)
    {
        var group=(runState.Material,runState.Lighting,runState.SpecularEnabled);
        if(UniformCachingEnabled && _uploadedMaterial==group)return;
        EnbW3dMaterialUniforms material = runState.Material;
        EnbW3dVertexLightingSemantics lighting = runState.Lighting;
        UploadInteger(_lightingEnabledUniform, lighting.LightingEnabled ? 1 : 0);
        UploadVector4(_materialAmbientUniform, new Vector4(material.Ambient, 0f));
        UploadVector4(_materialDiffuseUniform, material.DiffuseRgba);
        UploadVector4(_materialSpecularUniform, new Vector4(material.Specular, 0f));
        UploadVector4(_materialEmissiveUniform, new Vector4(material.Emissive, 0f));
        UploadVector4(_materialSourcesUniform, new Vector4(
            (float)lighting.AmbientSource,
            (float)lighting.DiffuseSource,
            (float)lighting.SpecularSource,
            (float)lighting.EmissiveSource));
        UploadVector4(_materialPowerSpecularUniform, new Vector4(
            material.Power,
            runState.SpecularEnabled ? 1f : 0f,
            0f,
            0f));
        _uploadedMaterial=UniformCachingEnabled?group:null;
    }

    private ImmutableArray<EnbW3dLightSlot> _uploadedLights;
    private void UploadLightEnvironment(EnbW3dLightEnvironment environment)
    {
        // One immutable light snapshot commonly serves a whole visible group.
        // Its global ambient is uploaded separately from material application.
        // Program recreation invalidates both uniform cache levels together.
        if(UniformCachingEnabled && !_uploadedLights.IsDefault &&
            (_uploadedLights.Equals(environment.Lights) || _uploadedLights.AsSpan().SequenceEqual(environment.Lights.AsSpan())))return;
        for (int i = 0; i < EnbW3dLightEnvironment.CurrentRetailLightSlotCount; i++)
        {
            EnbW3dLightSlot slot = environment.Lights[i];
            EnbW3dCompiledLight light = slot.Light;
            LightUniformLocations locations = _lightUniforms[i];
            UploadVector4(locations.State, new Vector4(
                slot.Enabled ? 1f : 0f,
                (float)light.Type,
                light.Range,
                light.Falloff));
            UploadVector4(locations.Diffuse, new Vector4(light.Diffuse, 0f));
            UploadVector4(locations.Specular, new Vector4(light.Specular, 0f));
            UploadVector4(locations.Ambient, new Vector4(light.Ambient, 0f));
            UploadVector4(locations.PositionPhi, new Vector4(light.Position, light.Phi));
            UploadVector4(locations.DirectionTheta, new Vector4(light.Direction, light.Theta));
            UploadVector4(locations.Attenuation, new Vector4(
                light.Attenuation0,
                light.Attenuation1,
                light.Attenuation2,
                0f));
        }
        _uploadedLights=UniformCachingEnabled?environment.Lights:default;
    }

    private void ApplyRasterState(GlInterface gl, EnbW3dProgrammableLightingLanePlan lane)
        => ApplyRasterState(gl, lane.DepthCompare, lane.DepthWriteEnabled, lane.CullMode);

    private void ApplyRasterState(
        GlInterface gl,
        EnbRenderDepthCompare depthCompare,
        bool depthWriteEnabled,
        EnbRenderCullMode cullMode)
    {
        if (ReuseRetainedState(ref _retainedRaster, ((int)depthCompare, depthWriteEnabled, (int)cullMode))) return;
        // The legacy raster helper disables blending; the next blend application
        // must therefore run even if its requested material has not changed.
        _retainedBlend = null;
        gl.Enable(GlDepthTest);
        gl.DepthFunc(EnbOpenGlFirstW3dShaderBackend.MapDepthCompare(depthCompare));
        gl.DepthMask(depthWriteEnabled ? 1 : 0);
        gl.Disable(GlBlend);

        EnbOpenGlCullState cull = EnbOpenGlFirstW3dShaderBackend.MapCullState(cullMode);
        if (!cull.Enabled)
        {
            gl.Disable(GlCullFace);
            return;
        }

        _frontFace ??= LoadRequiredProc<GlFrontFaceProc>(gl, "glFrontFace");
        _cullFace ??= LoadRequiredProc<GlCullFaceProc>(gl, "glCullFace");
        gl.Enable(GlCullFace);
        RequireProc(_frontFace, "glFrontFace")(cull.FrontFace);
        RequireProc(_cullFace, "glCullFace")(cull.Face);
    }

    private static void BindVertexLayout(GlInterface gl)
    {
        gl.VertexAttribPointer(PositionAttributeIndex, 3, GlConsts.GL_FLOAT, 0, EnbOpenGlLitShaderUploadPlan.StrideBytes, new IntPtr(EnbOpenGlLitShaderUploadPlan.PositionOffsetBytes));
        gl.EnableVertexAttribArray(PositionAttributeIndex);
        gl.VertexAttribPointer(NormalAttributeIndex, 3, GlConsts.GL_FLOAT, 0, EnbOpenGlLitShaderUploadPlan.StrideBytes, new IntPtr(EnbOpenGlLitShaderUploadPlan.NormalOffsetBytes));
        gl.EnableVertexAttribArray(NormalAttributeIndex);
        gl.VertexAttribPointer(Color1AttributeIndex, 4, GlConsts.GL_FLOAT, 0, EnbOpenGlLitShaderUploadPlan.StrideBytes, new IntPtr(EnbOpenGlLitShaderUploadPlan.Color1OffsetBytes));
        gl.EnableVertexAttribArray(Color1AttributeIndex);
        gl.VertexAttribPointer(TextureCoordinate0AttributeIndex, 2, GlConsts.GL_FLOAT, 0, EnbOpenGlLitShaderUploadPlan.StrideBytes, new IntPtr(EnbOpenGlLitShaderUploadPlan.TextureCoordinate0OffsetBytes));
        gl.EnableVertexAttribArray(TextureCoordinate0AttributeIndex);
    }

    private static void BindTextureCoordinate1Layout(GlInterface gl)
    {
        gl.VertexAttribPointer(TextureCoordinate1AttributeIndex, 2, GlConsts.GL_FLOAT, 0, 2 * sizeof(float), IntPtr.Zero);
        gl.EnableVertexAttribArray(TextureCoordinate1AttributeIndex);
    }

    private static bool ValidateTextureCapabilities(
        string logicalName,
        bool requiresExplicitMaximumMipLevel,
        float maximumAnisotropy,
        bool supportsTextureMaxLevel,
        bool supportsAnisotropy,
        GlVersion version,
        out string? diagnostic)
    {
        diagnostic = null;
        if (requiresExplicitMaximumMipLevel && !supportsTextureMaxLevel)
        {
            diagnostic = $"Texture '{logicalName}' omits authored tail mips under the R4J upload plan, but OpenGL context {version} cannot clamp GL_TEXTURE_MAX_LEVEL exactly.";
            return false;
        }

        if (maximumAnisotropy > 1.0f && !supportsAnisotropy)
        {
            diagnostic = $"Texture '{logicalName}' requires anisotropic filtering, but the current OpenGL context does not expose the anisotropy extension.";
            return false;
        }

        return true;
    }

    private int CreateTextureObject(
        ImmutableArray<EnbOpenGlStage0TextureMipUpload> mips,
        int glWrapS,
        int glWrapT,
        int glMinFilter,
        int glMagFilter,
        int maximumMipLevel,
        float maximumAnisotropy,
        bool supportsTextureMaxLevel,
        string logicalName)
    {
        RequireProc(_genTextures, "glGenTextures")(1, out int textureId);
        if (textureId == 0)
            throw new InvalidOperationException($"OpenGL returned texture object 0 for '{logicalName}'.");

        try
        {
            RequireProc(_bindTexture, "glBindTexture")(GlTexture2D, textureId);
            RequireProc(_texParameteri, "glTexParameteri")(GlTexture2D, GlTextureWrapS, glWrapS);
            RequireProc(_texParameteri, "glTexParameteri")(GlTexture2D, GlTextureWrapT, glWrapT);
            RequireProc(_texParameteri, "glTexParameteri")(GlTexture2D, GlTextureMinFilter, glMinFilter);
            RequireProc(_texParameteri, "glTexParameteri")(GlTexture2D, GlTextureMagFilter, glMagFilter);
            if (supportsTextureMaxLevel)
                RequireProc(_texParameteri, "glTexParameteri")(GlTexture2D, GlTextureMaxLevel, maximumMipLevel);
            if (maximumAnisotropy > 1.0f)
                RequireProc(_texParameterf, "glTexParameterf")(GlTexture2D, GlTextureMaxAnisotropyExt, maximumAnisotropy);

            foreach (EnbOpenGlStage0TextureMipUpload mip in mips)
                UploadTextureMip(mip);

            _appliedSamplers[textureId] = (glWrapS, glWrapT, glMinFilter, glMagFilter, maximumAnisotropy);
            return textureId;
        }
        catch
        {
            DeleteTexture(textureId);
            throw;
        }
    }

    private int RequireUniform(GlInterface gl, int program, string name)
    {
        int location = gl.GetUniformLocationString(program, name);
        if (location < 0)
            throw new InvalidOperationException($"R6E OpenGL W3D program did not expose {name}.");
        if (_uniformSlotByLocation.TryGetValue(location, out int slot)) return slot;
        slot = _registeredUniformLocations.Count;
        _registeredUniformLocations.Add(location);
        _uniformSlotByLocation.Add(location, slot);
        return slot;
    }

    // Uniform values belong to this backend's program, not the bound mesh.
    // Other renderer programs can run between submissions without changing them.
    // Dense slots are assigned once per linked program; driver locations need not be dense.
    private readonly Dictionary<int, int> _uniformSlotByLocation = [];
    private readonly List<int> _registeredUniformLocations = [];
    private int[] _uniformLocations = [];
    private struct UniformSlot<T> { public bool Valid; public T Value; }
    private UniformSlot<Vector4>[] _vectorUniformValues = [];
    private UniformSlot<Matrix4x4>[] _matrixUniformValues = [];
    private UniformSlot<int>[] _integerUniformValues = [];
    private UniformSlot<float>[] _scalarUniformValues = [];
    internal bool UniformCachingEnabled { get; set; } = true;
    internal bool TextureSharingEnabled { get; set; } = true;
    private readonly record struct TexturePlanKey(EnbTextureUploadMipPlan Mips,EnbW3dTextureSamplerSemantics Sampler);
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<EnbTextureImage,
        Dictionary<TexturePlanKey,EnbOpenGlTextureStageUploadPlan>> _imageUploadPlans=new();
    private readonly Dictionary<TextureObjectKey,(int Id,int References)> _sharedImageTextures=[];
    private readonly Dictionary<int,TextureObjectKey> _imageTextureKeys=[];
    private readonly record struct TextureObjectKey(ImmutableArray<EnbOpenGlStage0TextureMipUpload> Mips,
        int WrapS, int WrapT, int Min, int Mag, int MaximumMip, float Anisotropy);

    private void InvalidateUniformCache()
    {
        _uploadedLights=default;_uploadedMaterial=null;
        Array.Clear(_vectorUniformValues);
        Array.Clear(_matrixUniformValues);
        Array.Clear(_integerUniformValues);
        Array.Clear(_scalarUniformValues);
    }

    private void UploadInteger(int location,int value)
    {
        ref var slot = ref _integerUniformValues[location];
        if (UniformCachingEnabled && slot.Valid && slot.Value == value) return;
        RequireProc(_uniform1i,"glUniform1i")(_uniformLocations[location],value);
        slot.Value = value;
        slot.Valid = UniformCachingEnabled;
    }

    private void UploadScalar(GlInterface gl,int location,float value)
    {
        ref var slot = ref _scalarUniformValues[location];
        if (UniformCachingEnabled && slot.Valid && slot.Value == value) return;
        gl.Uniform1f(_uniformLocations[location],value);
        slot.Value = value;
        slot.Valid = UniformCachingEnabled;
    }

    private void UploadVector4(int location, Vector4 value)
    {
        ref var slot = ref _vectorUniformValues[location];
        if (UniformCachingEnabled && slot.Valid && slot.Value == value)
        {
            return;
        }
        RequireProc(_uniform4f, "glUniform4f")(_uniformLocations[location], value.X, value.Y, value.Z, value.W);
        slot.Value = value;
        slot.Valid = UniformCachingEnabled;
    }

    private static Vector3 DecodeRgb24(uint rgb24)
    {
        const float inv255 = 1.0f / 255.0f;
        return new Vector3(
            ((rgb24 >> 16) & 0xFFu) * inv255,
            ((rgb24 >> 8) & 0xFFu) * inv255,
            (rgb24 & 0xFFu) * inv255);
    }

    private static T LoadRequiredProc<T>(GlInterface gl, string name)
        where T : Delegate
    {
        IntPtr address = gl.GetProcAddress(name);
        if (address == IntPtr.Zero)
            throw new NotSupportedException($"Host OpenGL context did not expose required core procedure {name}.");
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    private static T RequireProc<T>(T? proc, string name)
        where T : Delegate
        => proc ?? throw new InvalidOperationException($"R6E OpenGL procedure {name} was not initialized.");

    private unsafe void UploadInstanceMatrices(GlInterface gl, Matrix4x4 rasterClip,
        Matrix4x4 canonicalClip, Matrix4x4 view, Matrix4x4 world, Matrix4x4 normal)
    {
        // One instance-state upload, independent of retained pass/category
        // uniforms. Preserve all CPU-computed matrix bits and shader arithmetic.
        ReadOnlySpan<int> slots = [_clipFromModelUniform, _canonicalClipFromModelUniform,
            _viewFromModelUniform, _worldFromModelUniform, _normalFromModelUniform];
        ReadOnlySpan<Matrix4x4> matrices = [rasterClip, canonicalClip, view, world, normal];
        int first = -1, last = -1;
        for (int i=0;i<matrices.Length;i++)
        {
            ref var slot = ref _matrixUniformValues[slots[i]];
            if (UniformCachingEnabled && slot.Valid && slot.Value == matrices[i]) continue;
            if (first < 0) first = i;
            last = i;
        }
        if (first < 0) return;
        // GLSL matrix-array elements accept a contiguous subrange. Query each
        // real location at link time; never assume numeric location strides.
        var selected = matrices.Slice(first, last-first+1);
        var elements = MemoryMarshal.Cast<Matrix4x4,float>(selected);
        foreach (float element in elements)
            if (!float.IsFinite(element))
                throw new ArgumentOutOfRangeException(nameof(matrices), "OpenGL transform must be finite.");
        fixed (float* pointer = elements)
            gl.UniformMatrix4fv(_uniformLocations[slots[first]], selected.Length, transpose:false, pointer);
        for (int i=first;i<=last;i++)
        {
            ref var slot = ref _matrixUniformValues[slots[i]];
            slot.Value = matrices[i]; slot.Valid = UniformCachingEnabled;
        }
    }

    private unsafe void UploadSystemNumericsMatrix(GlInterface gl, int location, Matrix4x4 matrix)
    {
        ref var slot = ref _matrixUniformValues[location];
        if (UniformCachingEnabled && slot.Valid && slot.Value == matrix)
        {
            return;
        }
        // Same row-major order as CreateGlUniformMatrixElements; no heap arrays
        // for the six transforms submitted with every textured packet.
        Span<float> elements =
        [
            matrix.M11, matrix.M12, matrix.M13, matrix.M14,
            matrix.M21, matrix.M22, matrix.M23, matrix.M24,
            matrix.M31, matrix.M32, matrix.M33, matrix.M34,
            matrix.M41, matrix.M42, matrix.M43, matrix.M44,
        ];
        foreach (float element in elements)
            if (!float.IsFinite(element)) throw new ArgumentOutOfRangeException(nameof(matrix), "OpenGL transform must be finite.");
        fixed (float* pointer = elements)
            gl.UniformMatrix4fv(_uniformLocations[location], 1, transpose: false, pointer);
        slot.Value = matrix;
        slot.Valid = UniformCachingEnabled;
    }

    private static unsafe void UploadArray<T>(GlInterface gl, int target, ReadOnlySpan<T> values, int usage)
        where T : unmanaged
    {
        if (values.IsEmpty)
        {
            gl.BufferData(target, IntPtr.Zero, IntPtr.Zero, usage);
            return;
        }

        fixed (T* pointer = values)
        {
            int byteCount = checked(values.Length * sizeof(T));
            gl.BufferData(target, new IntPtr(byteCount), new IntPtr(pointer), usage);
        }
    }

    private unsafe void UploadTextureMip(EnbOpenGlStage0TextureMipUpload mip)
    {
        using var hitch=EnbHitchEvents.Measure("texture.upload.stage0");
        if(!mip.CompressedBlocks.IsEmpty)
        {
            fixed(byte* blocks=mip.CompressedBlocks.Span)
                RequireProc(_compressedTexImage2D,"glCompressedTexImage2D")(
                    GlTexture2D,mip.Level,mip.GlInternalFormat,mip.Width,mip.Height,0,mip.CompressedBlocks.Length,new IntPtr(blocks));
            return;
        }
        bool packed=!mip.PackedBytes.IsDefaultOrEmpty;
        if(packed)RequireProc(_pixelStorei,"glPixelStorei")(GlUnpackAlignment,1);
        try
        {
            fixed (byte* pointer = (packed?mip.PackedBytes.AsSpan():mip.RgbaBytes))
            {
                RequireProc(_texImage2D, "glTexImage2D")(
                    GlTexture2D,mip.Level,mip.GlInternalFormat,mip.Width,mip.Height,0,
                    mip.GlPixelFormat,mip.GlPixelType,new IntPtr(pointer));
            }
        }
        finally {if(packed)RequireProc(_pixelStorei,"glPixelStorei")(GlUnpackAlignment,4);}
    }

    private readonly Dictionary<int, EnbTextureImage> _runtimeTextureImages = new();
    // These texture objects are owned here; borrowed framebuffer textures never
    // enter this cache. Shared mutable bindings are compared on every draw.
    private readonly Dictionary<int, (int WrapS, int WrapT, int Min, int Mag, float Anisotropy)> _appliedSamplers = new();
    private readonly HashSet<string> _runtimeTextureNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string, int, int, int, int, int, float), (int Id, int References)> _sharedRuntimeTextures = new();
    private readonly Dictionary<int, (string, int, int, int, int, int, float)> _runtimeTextureKeys = new();
    public void RegisterRuntimeTexture(string name) => _runtimeTextureNames.Add(name);
    public long RuntimeTextureUploadCount { get; private set; }

    public void UpdateRuntimeTexture(EnbOpenGlStage0TextureMeshResource resource, string logicalName, EnbTextureImage image)
    {
        for (int i = 0; i < resource.TextureUploadPlans.Length; i++)
        {
            var run = resource.TextureUploadPlans[i].RunState;
            if (!string.Equals(run.Texture0.LogicalName, logicalName, StringComparison.OrdinalIgnoreCase)) continue;
            int id = resource.TextureIdsByRun[i];
            if (_runtimeTextureImages.TryGetValue(id, out var previous) && ReferenceEquals(previous, image)) continue;
            if (!EnbOpenGlStage0TextureUploadPlanner.TryPlanStage(0, run.Texture0, image,
                    run.UploadMipPlan, run.Sampler, out var plan, out var reason))
                throw new InvalidOperationException(reason);
            // A nonmatching or unchanged publication performs no GL work and
            // must not tear down the active category's retained bindings.
            InterruptRetainedSubmission(materialChanged:false);
            RequireProc(_bindTexture, "glBindTexture")(GlTexture2D, id);
            foreach (var mip in plan.Mips) UploadTextureMip(mip);
            RuntimeTextureUploadCount++;
            _runtimeTextureImages[id] = image;
        }
    }

    private void DeleteTexture(int textureId)
    {
        if (textureId == 0)
            return;
        int id = textureId;
        if(_imageTextureKeys.TryGetValue(id,out var imageKey))
        {
            var shared=_sharedImageTextures[imageKey];
            if(shared.References>1){_sharedImageTextures[imageKey]=(id,shared.References-1);return;}
            _imageTextureKeys.Remove(id);_sharedImageTextures.Remove(imageKey);
        }
        if (_runtimeTextureKeys.TryGetValue(id, out var key))
        {
            var shared = _sharedRuntimeTextures[key];
            if (shared.References > 1)
            {
                _sharedRuntimeTextures[key] = (id, shared.References - 1);
                return;
            }
            _runtimeTextureKeys.Remove(id);
            _sharedRuntimeTextures.Remove(key);
        }
        _runtimeTextureImages.Remove(id);
        _appliedSamplers.Remove(id);
        RequireProc(_deleteTextures, "glDeleteTextures")(1, ref id);
    }

    private static void DrainErrors(GlInterface gl)
    {
        for (int i = 0; i < 16 && gl.GetError() != GlConsts.GL_NO_ERROR; i++)
        {
        }
    }

    private void EnsureInitialized()
    {
        if (!IsInitialized)
            throw new InvalidOperationException("The R6E OpenGL stage-0 texture backend has not been initialized on a current host GL context.");
    }

    private static bool IsSupportedShaderContext(GlVersion version)
        => (version.Type == GlProfileType.OpenGL || version.Type == GlProfileType.OpenGLES) &&
           version.Major >= 2;
}
