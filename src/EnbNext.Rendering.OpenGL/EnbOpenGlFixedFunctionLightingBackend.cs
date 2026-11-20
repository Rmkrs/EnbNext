using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using EnbNext.Rendering;

namespace EnbNext.Rendering.OpenGL;

/// <summary>
/// Interleaved upload: position.xyz, normal.xyz, COLO.rgba and stage-0
/// uv.xy. Normals stay in object/submission space; the shader applies the exact
/// Inverse-transpose world normal transform without automatic normalization.
/// </summary>
public sealed record EnbOpenGlLitShaderUploadPlan
{
    public EnbW3dProgrammableLightingLanePlan LanePlan { get; init; }
    internal EnbW3dNormalStream ValidatedNormals { get; init; }
    public EnbOpenGlIndexedUploadPlan IndexedPlan { get; init; }
    private ImmutableArray<float> _vertexElements;
    private readonly EnbW3dProgrammableLightingLanePlan _vertexLane;
    private readonly EnbOpenGlIndexedUploadPlan _vertexIndexed;
    public EnbOpenGlLitShaderUploadPlan(EnbW3dProgrammableLightingLanePlan lanePlan,
        EnbOpenGlIndexedUploadPlan indexedPlan, ImmutableArray<float> vertexElements)
    { LanePlan=_vertexLane=lanePlan; IndexedPlan=_vertexIndexed=indexedPlan; _vertexElements=vertexElements; }

    // Streaming uploads need this representation only until BufferData returns.
    // Keep the immutable component snapshots; materialize a retained interleaved
    // snapshot only for a diagnostic consumer that explicitly requests it.
    public ImmutableArray<float> VertexElements
    {
        get
        {
            if (_vertexElements.IsDefault)
            {
                var elements=new float[VertexCount*FloatElementsPerVertex];
                CopyVertexElementsTo(elements);
                ImmutableInterlocked.InterlockedInitialize(ref _vertexElements,
                    ImmutableCollectionsMarshal.AsImmutableArray(elements));
            }
            return _vertexElements;
        }
    }

    public void CopyVertexElementsTo(Span<float> destination)
    {
        if(destination.Length!=VertexCount*FloatElementsPerVertex)
            throw new ArgumentException("Incorrect interleaved vertex buffer size.",nameof(destination));
        if(!_vertexElements.IsDefault) { _vertexElements.AsSpan().CopyTo(destination); return; }
        // The frame owns these ranges throughout this synchronous upload.
        // Check lifetime once; no borrowed span escapes this method.
        var positions=_vertexIndexed.PositionSpan;
        var packedColors=_vertexLane.Color1.PackedSpan;
        var normals=_vertexLane.Normals.ReadSpan;
        for(int i=0;i<VertexCount;i++)
        {
            int n=i*FloatElementsPerVertex;
            var position=!positions.IsEmpty?positions[i]:_vertexIndexed.PositionAt(i);
            destination[n]=position.X;destination[n+1]=position.Y;destination[n+2]=position.Z;
            var normal=normals.IsEmpty?Vector3.Zero:normals[i];
            Vector4 color;
            if(!packedColors.IsEmpty)
            {var packed=packedColors[i];const float scale=1f/255f;color=new(packed.R*scale,packed.G*scale,packed.B*scale,packed.A*scale);}
            else color=_vertexLane.Color1[i];
            var uv=_vertexLane.TextureCoordinates0[i];
            destination[n+3]=normal.X; destination[n+4]=normal.Y; destination[n+5]=normal.Z;
            destination[n+6]=color.X; destination[n+7]=color.Y; destination[n+8]=color.Z; destination[n+9]=color.W;
            destination[n+10]=uv.X; destination[n+11]=uv.Y;
        }
    }
    internal bool CanRetainStaticVertexSource => _vertexElements.IsDefault;
    internal EnbW3dColorStream VertexColors => _vertexLane.Color1;
    internal EnbW3dUvStream VertexUv => _vertexLane.TextureCoordinates0;
    internal void CopyPoseElementsTo(Span<float> destination)
    {
        if(destination.Length!=VertexCount*FloatElementsPerVertex)throw new ArgumentException("Incorrect vertex size",nameof(destination));
        // A materialized snapshot may contain arbitrary diagnostic input; keep
        // its exact data rather than reconstructing a different source.
        if(!_vertexElements.IsDefault){_vertexElements.AsSpan().CopyTo(destination);return;}
        var positions=_vertexIndexed.PositionSpan;var normals=_vertexLane.Normals.ReadSpan;
        for(int i=0;i<VertexCount;i++)
        {
            int n=i*FloatElementsPerVertex;var p=positions.IsEmpty?_vertexIndexed.PositionAt(i):positions[i];
            var normal=normals.IsEmpty?Vector3.Zero:normals[i];
            destination[n]=p.X;destination[n+1]=p.Y;destination[n+2]=p.Z;
            destination[n+3]=normal.X;destination[n+4]=normal.Y;destination[n+5]=normal.Z;
        }
    }
    public const int FloatElementsPerVertex = 12;
    public const int StrideBytes = FloatElementsPerVertex * sizeof(float);
    public const int PositionOffsetBytes = 0;
    public const int NormalOffsetBytes = 3 * sizeof(float);
    public const int Color1OffsetBytes = 6 * sizeof(float);
    public const int TextureCoordinate0OffsetBytes = 10 * sizeof(float);

    public int VertexCount => IndexedPlan.VertexCount;
}

public sealed record EnbOpenGlLitShaderMeshResource(
    int VertexBuffer,
    int IndexBuffer,
    int VertexArray,
    EnbOpenGlLitShaderUploadPlan UploadPlan);

public static class EnbOpenGlLitShaderUploadPlanner
{
    public static bool TryPlan(
        EnbW3dProgrammableLightingLanePlan lanePlan,
        bool supportsUnsignedIntIndices,
        out EnbOpenGlLitShaderUploadPlan uploadPlan,
        out string? diagnostic,
        EnbOpenGlLitShaderUploadPlan? retained = null)
    {
        ArgumentNullException.ThrowIfNull(lanePlan);
        uploadPlan = default!;
        diagnostic = null;

        if (!EnbOpenGlIndexedUploadPlanner.TryPlan(
                lanePlan.Submission,
                supportsUnsignedIntIndices,
                out EnbOpenGlIndexedUploadPlan indexed,
                out diagnostic, retained?.IndexedPlan))
        {
            return false;
        }

        int vertexCount = indexed.VertexCount;
        if (lanePlan.Normals.Length != vertexCount ||
            lanePlan.Color1.Length != vertexCount ||
            lanePlan.TextureCoordinates0.Length != vertexCount)
        {
            diagnostic = $"R6D lane has {vertexCount} positions, {lanePlan.Normals.Length} normals, {lanePlan.Color1.Length} COLOR1 values and {lanePlan.TextureCoordinates0.Length} stage-0 UV values.";
            return false;
        }

        bool validateNormals=!lanePlan.Normals.IsKnownFinite && (retained is null || !lanePlan.Normals.Equals(retained.ValidatedNormals));
        var normalSpan=validateNormals?lanePlan.Normals.ReadSpan:default;
        // Packed bytes and implicit white cannot produce NaN or infinity.
        // Expanded interpolation colors still require ordinary validation.
        bool validateColors=!lanePlan.Color1.IsKnownFinite;
        for (int i = 0; (validateNormals || validateColors) && i < vertexCount; i++)
        {
            if ((validateNormals && !IsFinite(normalSpan[i])) ||
                (validateColors && !IsFinite(lanePlan.Color1[i])))
            {
                diagnostic = $"R6D vertex {i} contains a non-finite position, normal or COLOR1 value.";
                return false;
            }
        }
        uploadPlan = new EnbOpenGlLitShaderUploadPlan(lanePlan, indexed, default) {ValidatedNormals=lanePlan.Normals};
        return true;
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFinite(Vector4 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) &&
           float.IsFinite(value.Z) && float.IsFinite(value.W);
}

/// <summary>
/// Programmable realization of the current-retail vertex
/// lighting contract. It consumes the immutable four-light draw snapshot and
/// per--run material source/uniform state. Textures, blending, alpha test,
/// fog and mapper programs remain outside this admitted lane and fail closed in
/// the backend-neutral compiler.
/// </summary>
public sealed class EnbOpenGlFixedFunctionLightingBackend
{
    private const int GlUnsignedInt = 0x1405;
    private const int GlStreamDraw = 0x88E0;
    private const int GlDepthBufferBit = 0x00000100;
    private const int GlDepthTest = 0x0B71;
    private const int GlCullFace = 0x0B44;
    private const int GlBlend = 0x0BE2;

    private const int PositionAttributeIndex = 0;
    private const int NormalAttributeIndex = 1;
    private const int Color1AttributeIndex = 2;
    private const int TextureCoordinate0AttributeIndex = 3;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void GlUniform4fProc(int location, float x, float y, float z, float w);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void GlCullFaceProc(int mode);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void GlBlendFuncProc(int sourceFactor, int destinationFactor);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void GlFrontFaceProc(int mode);

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
    private int _viewFromModelUniform = -1;
    private int _sceneDepthUniform = -1;
    private int _worldFromModelUniform = -1;
    private int _normalFromModelUniform = -1;
    private int _cameraWorldPositionUniform = -1;
    private int _globalAmbientUniform = -1;
    private int _materialAmbientUniform = -1;
    private int _materialDiffuseUniform = -1;
    private int _materialSpecularUniform = -1;
    private int _materialEmissiveUniform = -1;
    private int _materialSourcesUniform = -1;
    private int _materialPowerSpecularUniform = -1;
    private int _uvSemanticKeepAliveUniform = -1;
    private int _alphaTestModeUniform = -1;
    private int _alphaTestReferenceUniform = -1;
    private int _fogRangeUniform = -1;
    private int _fogEnabledUniform = -1;
    private int _fogColorUniform = -1;
    private ImmutableArray<LightUniformLocations> _lightUniforms = ImmutableArray<LightUniformLocations>.Empty;
    private GlUniform4fProc? _uniform4f;
    private GlCullFaceProc? _cullFace;
    private GlBlendFuncProc? _blendFunc;
    private GlFrontFaceProc? _frontFace;

    public bool IsInitialized => _program != 0;
    public EnbOpenGlContextIdentity? ContextIdentity { get; private set; }

    public void Initialize(GlInterface gl)
    {
        ArgumentNullException.ThrowIfNull(gl);
        if (IsInitialized)
            throw new InvalidOperationException("The R6D OpenGL fixed-function-lighting backend is already initialized.");

        GlVersion version = gl.ContextInfo.Version;
        if (!IsSupportedShaderContext(version))
            throw new NotSupportedException($"R6D requires desktop OpenGL 2.0+ or OpenGL ES 2.0+; the host supplied {version}.");

        (string vertexSource, string fragmentSource) = CreateShaderSources(version);
        fragmentSource=EnbOpenGlSceneDepth.FragmentShader(fragmentSource,version);
        int vertexShader = gl.CreateShader(GlConsts.GL_VERTEX_SHADER);
        int fragmentShader = gl.CreateShader(GlConsts.GL_FRAGMENT_SHADER);
        int program = 0;
        try
        {
            string? vertexError = gl.CompileShaderAndGetError(vertexShader, vertexSource);
            if (!string.IsNullOrWhiteSpace(vertexError))
                throw new InvalidOperationException($"R6D OpenGL vertex shader compilation failed: {vertexError}");

            string? fragmentError = gl.CompileShaderAndGetError(fragmentShader, fragmentSource);
            if (!string.IsNullOrWhiteSpace(fragmentError))
                throw new InvalidOperationException($"R6D OpenGL fragment shader compilation failed: {fragmentError}");

            program = gl.CreateProgram();
            gl.AttachShader(program, vertexShader);
            gl.AttachShader(program, fragmentShader);
            gl.BindAttribLocationString(program, PositionAttributeIndex, "aPosition");
            gl.BindAttribLocationString(program, NormalAttributeIndex, "aNormal");
            gl.BindAttribLocationString(program, Color1AttributeIndex, "aColor1");
            gl.BindAttribLocationString(program, TextureCoordinate0AttributeIndex, "aTexCoord0");
            string? linkError = gl.LinkProgramAndGetError(program);
            if (!string.IsNullOrWhiteSpace(linkError))
                throw new InvalidOperationException($"R6D OpenGL W3D lighting program link failed: {linkError}");

            _clipFromModelUniform = RequireUniform(gl, program, "uClipFromModel");
            _viewFromModelUniform = RequireUniform(gl, program, "uViewFromModel");
            _sceneDepthUniform = RequireUniform(gl, program, "uSceneDepth");
            _worldFromModelUniform = RequireUniform(gl, program, "uWorldFromModel");
            _normalFromModelUniform = RequireUniform(gl, program, "uNormalFromModel");
            _cameraWorldPositionUniform = RequireUniform(gl, program, "uCameraWorldPosition");
            _globalAmbientUniform = RequireUniform(gl, program, "uGlobalAmbient");
            _materialAmbientUniform = RequireUniform(gl, program, "uMaterialAmbient");
            _materialDiffuseUniform = RequireUniform(gl, program, "uMaterialDiffuse");
            _materialSpecularUniform = RequireUniform(gl, program, "uMaterialSpecular");
            _materialEmissiveUniform = RequireUniform(gl, program, "uMaterialEmissive");
            _materialSourcesUniform = RequireUniform(gl, program, "uMaterialSources");
            _materialPowerSpecularUniform = RequireUniform(gl, program, "uMaterialPowerSpecular");
            _uvSemanticKeepAliveUniform = RequireUniform(gl, program, "uUvSemanticKeepAlive");
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

            _uniform4f = LoadRequiredProc<GlUniform4fProc>(gl, "glUniform4f");
            _lightUniforms = lightUniforms.ToImmutable();
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
        EnbW3dProgrammableLightingLanePlan lanePlan,
        out EnbOpenGlLitShaderMeshResource resource,
        out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(gl);
        ArgumentNullException.ThrowIfNull(lanePlan);
        EnsureInitialized();
        resource = default!;
        diagnostic = null;

        bool supportsUnsignedIntIndices = EnbOpenGlIndexedSubmissionBackend.SupportsUnsignedIntIndices(
            gl.ContextInfo.Version,
            gl.ContextInfo.Extensions);
        if (!EnbOpenGlLitShaderUploadPlanner.TryPlan(
                lanePlan,
                supportsUnsignedIntIndices,
                out EnbOpenGlLitShaderUploadPlan uploadPlan,
                out diagnostic))
        {
            return false;
        }

        DrainErrors(gl);
        int vertexBuffer = 0;
        int indexBuffer = 0;
        int vertexArray = 0;
        try
        {
            bool useVertexArray = gl.IsGenVertexArraysAvailable &&
                                  gl.IsBindVertexArrayAvailable &&
                                  gl.IsDeleteVertexArraysAvailable;
            if (EnbOpenGlIndexedSubmissionBackend.RequiresVertexArray(gl.ContextInfo.Version) && !useVertexArray)
            {
                diagnostic = $"OpenGL context {gl.ContextInfo.Version} requires a vertex array object for the R6D core-profile draw path, but the host did not expose VAO support.";
                return false;
            }

            if (useVertexArray)
            {
                vertexArray = gl.GenVertexArray();
                gl.BindVertexArray(vertexArray);
            }

            vertexBuffer = gl.GenBuffer();
            gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, vertexBuffer);
            UploadArray(
                gl,
                GlConsts.GL_ARRAY_BUFFER,
                uploadPlan.VertexElements.AsSpan(),
                uploadPlan.IndexedPlan.VertexSource == EnbW3dSubmissionVertexSource.CpuDeformed ? GlStreamDraw : GlConsts.GL_STATIC_DRAW);
            BindVertexLayout(gl);

            indexBuffer = gl.GenBuffer();
            gl.BindBuffer(GlConsts.GL_ELEMENT_ARRAY_BUFFER, indexBuffer);
            if (uploadPlan.IndexedPlan.IndexElementType == EnbOpenGlIndexElementType.UnsignedShort)
                UploadArray(gl, GlConsts.GL_ELEMENT_ARRAY_BUFFER, uploadPlan.IndexedPlan.UnsignedShortIndices.AsSpan(), GlConsts.GL_STATIC_DRAW);
            else
                UploadArray(gl, GlConsts.GL_ELEMENT_ARRAY_BUFFER, uploadPlan.IndexedPlan.UnsignedIntIndices.AsSpan(), GlConsts.GL_STATIC_DRAW);

            if (useVertexArray)
                gl.BindVertexArray(0);
            gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, 0);
            if (!useVertexArray)
                gl.BindBuffer(GlConsts.GL_ELEMENT_ARRAY_BUFFER, 0);

            int error = gl.GetError();
            if (error != GlConsts.GL_NO_ERROR)
            {
                diagnostic = $"R6D OpenGL resource creation ended with GL error 0x{error:X4}.";
                return false;
            }

            resource = new EnbOpenGlLitShaderMeshResource(vertexBuffer, indexBuffer, vertexArray, uploadPlan);
            vertexBuffer = 0;
            indexBuffer = 0;
            vertexArray = 0;
            return true;
        }
        finally
        {
            if (vertexArray != 0 && gl.IsDeleteVertexArraysAvailable)
                gl.DeleteVertexArray(vertexArray);
            if (indexBuffer != 0)
                gl.DeleteBuffer(indexBuffer);
            if (vertexBuffer != 0)
                gl.DeleteBuffer(vertexBuffer);
        }
    }

    public EnbOpenGlSubmissionReceipt Submit(
        GlInterface gl,
        int framebuffer,
        int pixelWidth,
        int pixelHeight,
        EnbOpenGlLitShaderMeshResource resource,
        bool clearColorAndDepth = true)
        => Submit(
            gl,
            framebuffer,
            pixelWidth,
            pixelHeight,
            resource,
            new EnbW3dSceneFogState(false, 0u, 0f, 1f),
            EnbW3dFogPipelineSemantics.FromCurrentRetailRangeCapability(false),
            rendererSupportsFog: false,
            clearColorAndDepth);

    public EnbOpenGlSubmissionReceipt Submit(
        GlInterface gl,
        int framebuffer,
        int pixelWidth,
        int pixelHeight,
        EnbOpenGlLitShaderMeshResource resource,
        EnbW3dSceneFogState sceneFog,
        EnbW3dFogPipelineSemantics fogPipeline,
        bool rendererSupportsFog = true,
        bool clearColorAndDepth = true)
    {
        ArgumentNullException.ThrowIfNull(gl);
        ArgumentNullException.ThrowIfNull(resource);
        EnsureInitialized();
        if (pixelWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(pixelWidth));
        if (pixelHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(pixelHeight));

        EnbW3dProgrammableLightingLanePlan lane = resource.UploadPlan.LanePlan;
        if (lane.RunStates.Length != resource.UploadPlan.IndexedPlan.DrawCommands.Length)
            throw new InvalidOperationException("R6D run-state count no longer matches the R6A/OpenGL draw-command count.");

        DrainErrors(gl);
        gl.BindFramebuffer(GlConsts.GL_FRAMEBUFFER, framebuffer);
        gl.Viewport(0, 0, pixelWidth, pixelHeight);
        if (clearColorAndDepth)
        {
            gl.DepthMask(1);
            gl.ClearColor(0.012f, 0.028f, 0.050f, 1f);
            gl.ClearDepth(1.0f);
            gl.Clear(GlConsts.GL_COLOR_BUFFER_BIT | GlDepthBufferBit);
        }

        ApplyRasterState(gl, lane);
        gl.UseProgram(_program);
        int hdr=gl.HighDynamicRange?1:0;
        if(_uploadedHdr!=hdr){gl.Uniform1i(_hdrUniform,hdr);_uploadedHdr=hdr;}
        UploadSystemNumericsMatrix(gl, _clipFromModelUniform, EnbOpenGlFirstW3dShaderBackend.CreateOpenGlClipFromCanonical(lane.CanonicalClipFromModel));
        UploadSystemNumericsMatrix(gl, _viewFromModelUniform, lane.CanonicalViewFromModel);
        var depth=EnbOpenGlSceneDepth.Uniforms(lane.ClipRange,lane.CanonicalClipFromModel);
        RequireProc(_uniform4f,"glUniform4f")(_sceneDepthUniform,depth.X,depth.Y,depth.Z,depth.W);
        UploadSystemNumericsMatrix(gl, _worldFromModelUniform, lane.ModelToWorld);
        UploadSystemNumericsMatrix(gl, _normalFromModelUniform, lane.NormalFromModel);
        UploadVector4(_cameraWorldPositionUniform, new Vector4(lane.CameraWorldPosition, 0f));
        UploadVector4(_globalAmbientUniform, new Vector4(DecodeRgb24(lane.LightEnvironment.GlobalAmbientRgb24), 0f));
        UploadLightEnvironment(lane.LightEnvironment);
        bool anyMaterialFog = lane.RunStates.Any(run => run.Run.Binding.Semantics.FixedFunction.FogMode != EnbW3dFogMode.Disabled);
        EnbOpenGlFogUniformState frameFog = EnbOpenGlFirstW3dShaderBackend.MapFog(
            anyMaterialFog ? EnbW3dFogMode.SceneColor : EnbW3dFogMode.Disabled,
            sceneFog,
            rendererSupportsFog,
            fogPipeline);
        UploadFogFrameState(gl, frameFog);
        gl.Uniform1f(_uvSemanticKeepAliveUniform, 0.0f);

        if (resource.VertexArray != 0)
        {
            gl.BindVertexArray(resource.VertexArray);
        }
        else
        {
            gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, resource.VertexBuffer);
            gl.BindBuffer(GlConsts.GL_ELEMENT_ARRAY_BUFFER, resource.IndexBuffer);
            BindVertexLayout(gl);
        }

        int glIndexType = resource.UploadPlan.IndexedPlan.IndexElementType == EnbOpenGlIndexElementType.UnsignedShort
            ? GlConsts.GL_UNSIGNED_SHORT
            : GlUnsignedInt;
        int drawCalls = 0;
        int primitiveCount = 0; int vertexCount = 0;
        int indexCount = 0;
        for (int i = 0; i < resource.UploadPlan.IndexedPlan.DrawCommands.Length; i++)
        {
            EnbOpenGlIndexedDrawCommand command = resource.UploadPlan.IndexedPlan.DrawCommands[i];
            EnbW3dProgrammableLightingRunState runState = lane.RunStates[i];
            if (!ReferenceEquals(command.SourceRun, runState.Run) && command.SourceRun != runState.Run)
                throw new InvalidOperationException($"R6D run state {i} no longer corresponds to its R6A draw command.");

            UploadRunState(runState);
            EnbW3dFixedFunctionState fixedFunction = runState.Run.Binding.Semantics.FixedFunction;
            ApplyBlendState(gl, fixedFunction);
            ApplyAlphaTestState(gl, fixedFunction.AlphaTest);
            ApplyFogState(gl, EnbOpenGlFirstW3dShaderBackend.MapFog(fixedFunction.FogMode, sceneFog, rendererSupportsFog, fogPipeline));
            gl.DrawElements(
                GlConsts.GL_TRIANGLES,
                command.IndexCount,
                glIndexType,
                new IntPtr(command.IndexByteOffset));
            drawCalls++;
            primitiveCount = checked(primitiveCount + command.PrimitiveCount); vertexCount += unchecked((int)command.SourceRun.VertexCount);
            indexCount = checked(indexCount + command.IndexCount);
        }

        if (resource.VertexArray != 0)
            gl.BindVertexArray(0);
        else
        {
            gl.BindBuffer(GlConsts.GL_ELEMENT_ARRAY_BUFFER, 0);
            gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, 0);
        }
        gl.UseProgram(0);

        int error = gl.GetError();
        return new EnbOpenGlSubmissionReceipt(drawCalls, primitiveCount, indexCount, error) { VertexCount = vertexCount };
    }

    public void DestroyResource(GlInterface? gl, EnbOpenGlLitShaderMeshResource resource)
    {
        if (gl is null) return;
        ArgumentNullException.ThrowIfNull(resource);
        if (resource.VertexArray != 0 && gl.IsDeleteVertexArraysAvailable)
            gl.DeleteVertexArray(resource.VertexArray);
        if (resource.IndexBuffer != 0)
            gl.DeleteBuffer(resource.IndexBuffer);
        if (resource.VertexBuffer != 0)
            gl.DeleteBuffer(resource.VertexBuffer);
    }

    public void Deinitialize(GlInterface? gl)
    {
        // No GL calls after context loss.
        if (_program != 0)
            gl?.DeleteProgram(_program);
        _program = 0;
        _uploadedHdr = -1;
        _clipFromModelUniform = -1;
        _viewFromModelUniform = -1;
        _sceneDepthUniform = -1;
        _worldFromModelUniform = -1;
        _normalFromModelUniform = -1;
        _cameraWorldPositionUniform = -1;
        _globalAmbientUniform = -1;
        _materialAmbientUniform = -1;
        _materialDiffuseUniform = -1;
        _materialSpecularUniform = -1;
        _materialEmissiveUniform = -1;
        _materialSourcesUniform = -1;
        _materialPowerSpecularUniform = -1;
        _uvSemanticKeepAliveUniform = -1;
        _alphaTestModeUniform = -1;
        _alphaTestReferenceUniform = -1;
        _fogRangeUniform = -1;
        _fogEnabledUniform = -1;
        _fogColorUniform = -1;
        _lightUniforms = ImmutableArray<LightUniformLocations>.Empty;
        _uniform4f = null;
        _cullFace = null;
        _blendFunc = null;
        _frontFace = null;
        ContextIdentity = null;
    }

    public static (string VertexShader, string FragmentShader) CreateShaderSources(GlVersion version)
    {
        if (!IsSupportedShaderContext(version))
            throw new NotSupportedException($"Unsupported OpenGL shader context {version}.");

        string uniforms = CreateLightingUniformDeclarations();
        string body = CreateLightingVertexBody();
        const string fragmentExpression =
            "vec4(lightRange(vDiffuse.rgb + vSpecular + vec3((vTexCoord0.x + vTexCoord0.y) * uUvSemanticKeepAlive)), vDiffuse.a)";
        const string alphaTestProgram =
            "uniform bool uHighDynamicRange;\nvec3 lightRange(vec3 c){return uHighDynamicRange?max(c,vec3(0.0)):clamp(c,0.0,1.0);}\nvec4 stageRange(vec4 c){return vec4(lightRange(c.rgb),clamp(c.a,0.0,1.0));}\nuniform float uAlphaTestMode;\nuniform float uAlphaTestReference;\nuniform float uFogEnabled;\nuniform vec4 uFogColor;\n" +
            "bool alphaTestPass(float alpha){ if(uAlphaTestMode<0.5) return true; if(uAlphaTestMode<1.5) return alpha<=uAlphaTestReference; return alpha>=uAlphaTestReference; }\n" +
            "vec4 applyFog(vec4 color){ if(uFogEnabled>0.5) color.rgb=mix(uFogColor.rgb,color.rgb,vFogVisibility); return color; }\n";

        if (version.Type == GlProfileType.OpenGLES)
        {
            if (version.Major >= 3)
            {
                return (
                    "#version 300 es\nprecision highp float;\nin vec3 aPosition;\nin vec3 aNormal;\nin vec4 aColor1;\nin vec2 aTexCoord0;\nout mediump vec4 vDiffuse;\nout mediump vec3 vSpecular;\nout mediump vec2 vTexCoord0;\nout mediump float vFogVisibility;\n" + uniforms + body,
                    $"#version 300 es\nprecision mediump float;\nin mediump vec4 vDiffuse;\nin mediump vec3 vSpecular;\nin mediump vec2 vTexCoord0;\nin mediump float vFogVisibility;\nuniform float uUvSemanticKeepAlive;\nout vec4 fragColor;\n{alphaTestProgram}void main(){{ vec4 color={fragmentExpression}; if(!alphaTestPass(color.a)) discard; fragColor=applyFog(color); }}\n");
            }

            return (
                "precision highp float;\nattribute vec3 aPosition;\nattribute vec3 aNormal;\nattribute vec4 aColor1;\nattribute vec2 aTexCoord0;\nvarying mediump vec4 vDiffuse;\nvarying mediump vec3 vSpecular;\nvarying mediump vec2 vTexCoord0;\nvarying mediump float vFogVisibility;\n" + uniforms + body,
                $"precision mediump float;\nvarying mediump vec4 vDiffuse;\nvarying mediump vec3 vSpecular;\nvarying mediump vec2 vTexCoord0;\nvarying mediump float vFogVisibility;\nuniform float uUvSemanticKeepAlive;\n{alphaTestProgram}void main(){{ vec4 color={fragmentExpression}; if(!alphaTestPass(color.a)) discard; gl_FragColor=applyFog(color); }}\n");
        }

        if (version.Major > 3 || (version.Major == 3 && version.Minor >= 2))
        {
            return (
                "#version 150\nin vec3 aPosition;\nin vec3 aNormal;\nin vec4 aColor1;\nin vec2 aTexCoord0;\nout vec4 vDiffuse;\nout vec3 vSpecular;\nout vec2 vTexCoord0;\nout float vFogVisibility;\n" + uniforms + body,
                $"#version 150\nin vec4 vDiffuse;\nin vec3 vSpecular;\nin vec2 vTexCoord0;\nin float vFogVisibility;\nuniform float uUvSemanticKeepAlive;\nout vec4 fragColor;\n{alphaTestProgram}void main(){{ vec4 color={fragmentExpression}; if(!alphaTestPass(color.a)) discard; fragColor=applyFog(color); }}\n");
        }

        string legacyVersion = version.Major == 2 && version.Minor == 0 ? "110" : "120";
        return (
            $"#version {legacyVersion}\nattribute vec3 aPosition;\nattribute vec3 aNormal;\nattribute vec4 aColor1;\nattribute vec2 aTexCoord0;\nvarying vec4 vDiffuse;\nvarying vec3 vSpecular;\nvarying vec2 vTexCoord0;\nvarying float vFogVisibility;\n{uniforms}{body}",
            $"#version {legacyVersion}\nvarying vec4 vDiffuse;\nvarying vec3 vSpecular;\nvarying vec2 vTexCoord0;\nvarying float vFogVisibility;\nuniform float uUvSemanticKeepAlive;\n{alphaTestProgram}void main(){{ vec4 color={fragmentExpression}; if(!alphaTestPass(color.a)) discard; gl_FragColor=applyFog(color); }}\n");
    }

    internal static string CreateLightingUniformDeclarations()
    {
        var source = new StringBuilder();
        source.Append(
            "uniform bool uHighDynamicRange;\nvec3 lightRange(vec3 c){return uHighDynamicRange?max(c,vec3(0.0)):clamp(c,0.0,1.0);}\nvec4 stageRange(vec4 c){return vec4(lightRange(c.rgb),clamp(c.a,0.0,1.0));}\nuniform mat4 uClipFromModel;\n" +
            "uniform mat4 uViewFromModel;\n" +
            "uniform vec4 uFogRange;\n" +
            "uniform mat4 uWorldFromModel;\n" +
            "uniform mat4 uNormalFromModel;\n" +
            "uniform vec4 uCameraWorldPosition;\n" +
            "uniform vec4 uGlobalAmbient;\n" +
            "uniform vec4 uMaterialAmbient;\n" +
            "uniform vec4 uMaterialDiffuse;\n" +
            "uniform vec4 uMaterialSpecular;\n" +
            "uniform vec4 uMaterialEmissive;\n" +
            "uniform vec4 uMaterialSources;\n" +
            "uniform vec4 uMaterialPowerSpecular;\n");
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

    internal static string CreateLightingVertexBody()
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
            calls +
            " vec3 fogPosition=(uViewFromModel*vec4(aPosition,1.0)).xyz; vFogVisibility=fogVisibility(fogPosition); vDiffuse=vec4(lightRange(emissive+ambient+diffuse),clamp(diffuseReflectance.a,0.0,1.0)); vSpecular=lightRange(specular); vTexCoord0=aTexCoord0; gl_Position=uClipFromModel*vec4(aPosition,1.0);\n" +
            "}\n";
    }

    private void UploadFogFrameState(GlInterface gl, EnbOpenGlFogUniformState state)
    {
        UploadVector4(_fogRangeUniform, new Vector4(
            state.Start,
            state.End,
            state.DistanceSource == EnbW3dFogDistanceSource.CameraRange ? 1f : 0f,
            0f));
    }

    private void ApplyFogState(GlInterface gl, EnbOpenGlFogUniformState state)
    {
        gl.Uniform1f(_fogEnabledUniform, state.Enabled ? 1f : 0f);
        UploadVector4(_fogColorUniform, new Vector4(state.Color, 0f));
    }

    private void ApplyAlphaTestState(GlInterface gl, EnbW3dAlphaTestSemantics alphaTest)
    {
        EnbOpenGlAlphaTestUniformState state = EnbOpenGlFirstW3dShaderBackend.MapAlphaTest(alphaTest);
        gl.Uniform1f(_alphaTestModeUniform, state.Mode);
        gl.Uniform1f(_alphaTestReferenceUniform, state.Reference);
    }

    private void ApplyBlendState(GlInterface gl, EnbW3dFixedFunctionState state)
    {
        if (!state.BlendEnabled)
        {
            gl.Disable(GlBlend);
            return;
        }

        _blendFunc ??= LoadRequiredProc<GlBlendFuncProc>(gl, "glBlendFunc");
        gl.Enable(GlBlend);
        _blendFunc(
            EnbOpenGlFirstW3dShaderBackend.MapBlendFactor(state.SourceBlend),
            EnbOpenGlFirstW3dShaderBackend.MapBlendFactor(state.DestinationBlend));
    }

    private void UploadRunState(EnbW3dProgrammableLightingRunState runState)
    {
        EnbW3dMaterialUniforms material = runState.Material;
        EnbW3dVertexLightingSemantics lighting = runState.Lighting;
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
    }

    private void UploadLightEnvironment(EnbW3dLightEnvironment environment)
    {
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
    }

    private void ApplyRasterState(GlInterface gl, EnbW3dProgrammableLightingLanePlan lane)
    {
        gl.Enable(GlDepthTest);
        gl.DepthFunc(EnbOpenGlFirstW3dShaderBackend.MapDepthCompare(lane.DepthCompare));
        gl.DepthMask(lane.DepthWriteEnabled ? 1 : 0);
        gl.Disable(GlBlend);

        EnbOpenGlCullState cull = EnbOpenGlFirstW3dShaderBackend.MapCullState(lane.CullMode);
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

    private static int RequireUniform(GlInterface gl, int program, string name)
    {
        int location = gl.GetUniformLocationString(program, name);
        if (location < 0)
            throw new InvalidOperationException($"R6D OpenGL W3D program did not expose {name}.");
        return location;
    }

    private void UploadVector4(int location, Vector4 value)
        => RequireProc(_uniform4f, "glUniform4f")(location, value.X, value.Y, value.Z, value.W);

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
        => proc ?? throw new InvalidOperationException($"R6D OpenGL procedure {name} was not initialized.");

    private static unsafe void UploadSystemNumericsMatrix(GlInterface gl, int location, Matrix4x4 matrix)
    {
        float[] elements = EnbOpenGlIndexedSubmissionBackend.CreateGlUniformMatrixElements(matrix).ToArray();
        fixed (float* pointer = elements)
            gl.UniformMatrix4fv(location, 1, transpose: false, pointer);
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

    private static void DrainErrors(GlInterface gl)
    {
        for (int i = 0; i < 16 && gl.GetError() != GlConsts.GL_NO_ERROR; i++)
        {
        }
    }

    private void EnsureInitialized()
    {
        if (!IsInitialized)
            throw new InvalidOperationException("The R6D OpenGL fixed-function-lighting backend has not been initialized on a current host GL context.");
    }

    private static bool IsSupportedShaderContext(GlVersion version)
        => (version.Type == GlProfileType.OpenGL || version.Type == GlProfileType.OpenGLES) &&
           version.Major >= 2;
}
