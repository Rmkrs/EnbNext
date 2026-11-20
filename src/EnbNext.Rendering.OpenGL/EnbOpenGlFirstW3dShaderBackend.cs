using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;
using EnbNext.Rendering;

namespace EnbNext.Rendering.OpenGL;

/// <summary>
/// Interleaved vertex upload: position.xyz, COLO.rgba and stage-0 uv.xy.
/// All components are floats so the first programmable lane has one simple,
/// portable GLES2+/desktop GL layout while exact authored COLO bytes are
/// normalized once by the backend-neutral compiler.
/// </summary>
public sealed record EnbOpenGlFirstShaderUploadPlan(
    EnbW3dProgrammableShaderLanePlan LanePlan,
    EnbOpenGlIndexedUploadPlan IndexedPlan,
    ImmutableArray<float> VertexElements)
{
    public const int FloatElementsPerVertex = 9;
    public const int StrideBytes = FloatElementsPerVertex * sizeof(float);
    public const int PositionOffsetBytes = 0;
    public const int Color1OffsetBytes = 3 * sizeof(float);
    public const int TextureCoordinate0OffsetBytes = 7 * sizeof(float);

    public int VertexCount => VertexElements.Length / FloatElementsPerVertex;
}

public sealed record EnbOpenGlFirstShaderMeshResource(
    int VertexBuffer,
    int IndexBuffer,
    int VertexArray,
    EnbOpenGlFirstShaderUploadPlan UploadPlan);

public readonly record struct EnbOpenGlCullState(
    bool Enabled,
    int FrontFace,
    int Face);

public readonly record struct EnbOpenGlAlphaTestUniformState(
    float Mode,
    float Reference);

public readonly record struct EnbOpenGlFogUniformState(
    bool Enabled,
    Vector3 Color,
    float Start,
    float End,
    EnbW3dFogDistanceSource DistanceSource);

/// <summary>
/// Pure-to-GL upload planning. Material/camera semantics are already frozen
/// in the lane plan; this stage only selects exact index representation and
/// flattens the three vertex attributes without changing vertex/index order.
/// </summary>
public static class EnbOpenGlFirstShaderUploadPlanner
{
    public static bool TryPlan(
        EnbW3dProgrammableShaderLanePlan lanePlan,
        bool supportsUnsignedIntIndices,
        out EnbOpenGlFirstShaderUploadPlan uploadPlan,
        out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(lanePlan);
        uploadPlan = default!;
        diagnostic = null;

        if (!EnbOpenGlIndexedUploadPlanner.TryPlan(
                lanePlan.Submission,
                supportsUnsignedIntIndices,
                out EnbOpenGlIndexedUploadPlan indexed,
                out diagnostic))
        {
            return false;
        }

        int vertexCount = indexed.VertexCount;
        if (lanePlan.Color1.Length != vertexCount || lanePlan.TextureCoordinates0.Length != vertexCount)
        {
            diagnostic = $"R6C lane has {vertexCount} positions, {lanePlan.Color1.Length} COLOR1 values and {lanePlan.TextureCoordinates0.Length} stage-0 UV values.";
            return false;
        }

        var elements = ImmutableArray.CreateBuilder<float>(checked(vertexCount * EnbOpenGlFirstShaderUploadPlan.FloatElementsPerVertex));
        IReadOnlyList<Vector3> positions = lanePlan.Submission.Positions;
        for (int i = 0; i < vertexCount; i++)
        {
            Vector3 position = positions[i];
            Vector4 color = lanePlan.Color1[i];
            Vector2 uv = lanePlan.TextureCoordinates0[i];
            if (!IsFinite(color))
            {
                diagnostic = $"R6C vertex {i} contains a non-finite COLOR1 value.";
                return false;
            }

            elements.Add(position.X);
            elements.Add(position.Y);
            elements.Add(position.Z);
            elements.Add(color.X);
            elements.Add(color.Y);
            elements.Add(color.Z);
            elements.Add(color.W);
            elements.Add(uv.X);
            elements.Add(uv.Y);
        }

        uploadPlan = new EnbOpenGlFirstShaderUploadPlan(lanePlan, indexed, elements.ToImmutable());
        return true;
    }

    private static bool IsFinite(Vector4 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) &&
           float.IsFinite(value.Z) && float.IsFinite(value.W);

}

/// <summary>
/// First scene-correct programmable W3D lane. It realizes only the exact
/// unlit/COLO/opaque state accepted by EnbW3dProgrammableShaderLaneCompiler.
/// Unsupported state never reaches this class. This also owns the one API
/// conversion from the canonical 0..1 NDC depth to OpenGL's -1..1 NDC depth.
/// </summary>
public sealed class EnbOpenGlFirstW3dShaderBackend
{
    private const int GlUnsignedInt = 0x1405;
    private const int GlStreamDraw = 0x88E0;
    private const int GlDepthBufferBit = 0x00000100;
    private const int GlDepthTest = 0x0B71;
    private const int GlCullFace = 0x0B44;
    private const int GlBlend = 0x0BE2;
    private const int GlZero = 0;
    private const int GlOne = 1;
    private const int GlSourceColor = 0x0300;
    private const int GlOneMinusSourceColor = 0x0301;
    private const int GlSourceAlpha = 0x0302;
    private const int GlOneMinusSourceAlpha = 0x0303;
    private const int GlBack = 0x0405;
    private const int GlClockwise = 0x0900;
    private const int GlCounterClockwise = 0x0901;

    private const int PositionAttributeIndex = 0;
    private const int Color1AttributeIndex = 1;
    private const int TextureCoordinate0AttributeIndex = 2;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void GlUniform4fProc(int location, float x, float y, float z, float w);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void GlCullFaceProc(int mode);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void GlBlendFuncProc(int sourceFactor, int destinationFactor);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void GlFrontFaceProc(int mode);

    private int _program;
    private int _clipFromModelUniform = -1;
    private int _viewFromModelUniform = -1;
    private int _sceneDepthUniform = -1;
    private int _uvSemanticKeepAliveUniform = -1;
    private int _alphaTestModeUniform = -1;
    private int _alphaTestReferenceUniform = -1;
    private int _fogRangeUniform = -1;
    private int _fogEnabledUniform = -1;
    private int _fogColorUniform = -1;
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
            throw new InvalidOperationException("The R6C OpenGL W3D backend is already initialized.");

        GlVersion version = gl.ContextInfo.Version;
        if (!IsSupportedShaderContext(version))
            throw new NotSupportedException($"R6C requires desktop OpenGL 2.0+ or OpenGL ES 2.0+; the host supplied {version}.");

        (string vertexSource, string fragmentSource) = CreateFirstLaneShaderSources(version);
        fragmentSource=EnbOpenGlSceneDepth.FragmentShader(fragmentSource,version);
        int vertexShader = gl.CreateShader(GlConsts.GL_VERTEX_SHADER);
        int fragmentShader = gl.CreateShader(GlConsts.GL_FRAGMENT_SHADER);
        int program = 0;
        try
        {
            string? vertexError = gl.CompileShaderAndGetError(vertexShader, vertexSource);
            if (!string.IsNullOrWhiteSpace(vertexError))
                throw new InvalidOperationException($"R6C OpenGL vertex shader compilation failed: {vertexError}");

            string? fragmentError = gl.CompileShaderAndGetError(fragmentShader, fragmentSource);
            if (!string.IsNullOrWhiteSpace(fragmentError))
                throw new InvalidOperationException($"R6C OpenGL fragment shader compilation failed: {fragmentError}");

            program = gl.CreateProgram();
            gl.AttachShader(program, vertexShader);
            gl.AttachShader(program, fragmentShader);
            gl.BindAttribLocationString(program, PositionAttributeIndex, "aPosition");
            gl.BindAttribLocationString(program, Color1AttributeIndex, "aColor1");
            gl.BindAttribLocationString(program, TextureCoordinate0AttributeIndex, "aTexCoord0");
            string? linkError = gl.LinkProgramAndGetError(program);
            if (!string.IsNullOrWhiteSpace(linkError))
                throw new InvalidOperationException($"R6C OpenGL W3D program link failed: {linkError}");

            int clipFromModelUniform = gl.GetUniformLocationString(program, "uClipFromModel");
            if (clipFromModelUniform < 0)
                throw new InvalidOperationException("R6C OpenGL W3D program did not expose uClipFromModel.");
            int viewFromModelUniform = gl.GetUniformLocationString(program, "uViewFromModel");
            if (viewFromModelUniform < 0)
                throw new InvalidOperationException("R6O OpenGL W3D program did not expose the canonical view transform.");
            int uvSemanticKeepAliveUniform = gl.GetUniformLocationString(program, "uUvSemanticKeepAlive");
            if (uvSemanticKeepAliveUniform < 0)
                throw new InvalidOperationException("R6C OpenGL W3D program optimized away the stage-0 UV semantic path.");
            int alphaTestModeUniform = gl.GetUniformLocationString(program, "uAlphaTestMode");
            int alphaTestReferenceUniform = gl.GetUniformLocationString(program, "uAlphaTestReference");
            if (alphaTestModeUniform < 0 || alphaTestReferenceUniform < 0)
                throw new InvalidOperationException("R6N OpenGL W3D program did not expose alpha-test uniforms.");
            int fogRangeUniform = gl.GetUniformLocationString(program, "uFogRange");
            int fogEnabledUniform = gl.GetUniformLocationString(program, "uFogEnabled");
            int fogColorUniform = gl.GetUniformLocationString(program, "uFogColor");
            if (fogRangeUniform < 0 || fogEnabledUniform < 0 || fogColorUniform < 0)
                throw new InvalidOperationException("R6O OpenGL W3D program did not expose fog uniforms.");

            _program = program;
            _clipFromModelUniform = clipFromModelUniform;
            _viewFromModelUniform = viewFromModelUniform;
            _sceneDepthUniform=gl.GetUniformLocationString(program,"uSceneDepth");
            if(_sceneDepthUniform<0)throw new InvalidOperationException("Scene depth uniform unavailable.");
            _uvSemanticKeepAliveUniform = uvSemanticKeepAliveUniform;
            _alphaTestModeUniform = alphaTestModeUniform;
            _alphaTestReferenceUniform = alphaTestReferenceUniform;
            _fogRangeUniform = fogRangeUniform;
            _fogEnabledUniform = fogEnabledUniform;
            _fogColorUniform = fogColorUniform;
            _uniform4f = LoadRequiredProc<GlUniform4fProc>(gl, "glUniform4f");
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
        EnbW3dProgrammableShaderLanePlan lanePlan,
        out EnbOpenGlFirstShaderMeshResource resource,
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
        if (!EnbOpenGlFirstShaderUploadPlanner.TryPlan(
                lanePlan,
                supportsUnsignedIntIndices,
                out EnbOpenGlFirstShaderUploadPlan uploadPlan,
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
                diagnostic = $"OpenGL context {gl.ContextInfo.Version} requires a vertex array object for the R6C core-profile draw path, but the host did not expose VAO support.";
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
                uploadPlan.IndexedPlan.VertexSource == EnbW3dSubmissionVertexSource.CpuDeformed
                    ? GlStreamDraw
                    : GlConsts.GL_STATIC_DRAW);
            BindVertexLayout(gl);

            indexBuffer = gl.GenBuffer();
            gl.BindBuffer(GlConsts.GL_ELEMENT_ARRAY_BUFFER, indexBuffer);
            if (uploadPlan.IndexedPlan.IndexElementType == EnbOpenGlIndexElementType.UnsignedShort)
            {
                UploadArray(gl, GlConsts.GL_ELEMENT_ARRAY_BUFFER, uploadPlan.IndexedPlan.UnsignedShortIndices.AsSpan(), GlConsts.GL_STATIC_DRAW);
            }
            else
            {
                UploadArray(gl, GlConsts.GL_ELEMENT_ARRAY_BUFFER, uploadPlan.IndexedPlan.UnsignedIntIndices.AsSpan(), GlConsts.GL_STATIC_DRAW);
            }

            if (useVertexArray)
                gl.BindVertexArray(0);
            gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, 0);
            if (!useVertexArray)
                gl.BindBuffer(GlConsts.GL_ELEMENT_ARRAY_BUFFER, 0);

            int error = gl.GetError();
            if (error != GlConsts.GL_NO_ERROR)
            {
                diagnostic = $"R6C OpenGL resource creation ended with GL error 0x{error:X4}.";
                return false;
            }

            resource = new EnbOpenGlFirstShaderMeshResource(vertexBuffer, indexBuffer, vertexArray, uploadPlan);
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
        EnbOpenGlFirstShaderMeshResource resource,
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
        EnbOpenGlFirstShaderMeshResource resource,
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

        DrainErrors(gl);
        gl.BindFramebuffer(GlConsts.GL_FRAMEBUFFER, framebuffer);
        gl.Viewport(0, 0, pixelWidth, pixelHeight);
        if (clearColorAndDepth)
        {
            // OpenGL depth clears obey the depth write mask. Force writes on for
            // the clear, then ApplyRasterState restores the authored mask.
            gl.DepthMask(1);
            gl.ClearColor(0.012f, 0.028f, 0.050f, 1f);
            gl.ClearDepth(1.0f);
            gl.Clear(GlConsts.GL_COLOR_BUFFER_BIT | GlDepthBufferBit);
        }

        ApplyRasterState(gl, resource.UploadPlan.LanePlan);
        gl.UseProgram(_program);
        Matrix4x4 openGlClipFromModel = CreateOpenGlClipFromCanonical(
            resource.UploadPlan.LanePlan.CanonicalClipFromModel);
        UploadSystemNumericsMatrix(gl, _clipFromModelUniform, openGlClipFromModel);
        UploadSystemNumericsMatrix(gl, _viewFromModelUniform, resource.UploadPlan.LanePlan.CanonicalViewFromModel);
        var depth=EnbOpenGlSceneDepth.Uniforms(resource.UploadPlan.LanePlan.ClipRange,resource.UploadPlan.LanePlan.CanonicalClipFromModel);
        RequireProc(_uniform4f,"glUniform4f")(_sceneDepthUniform,depth.X,depth.Y,depth.Z,depth.W);
        bool anyMaterialFog = resource.UploadPlan.IndexedPlan.DrawCommands.Any(
            command => command.SourceRun.Binding.Semantics.FixedFunction.FogMode != EnbW3dFogMode.Disabled);
        EnbOpenGlFogUniformState frameFog = MapFog(
            anyMaterialFog ? EnbW3dFogMode.SceneColor : EnbW3dFogMode.Disabled,
            sceneFog,
            rendererSupportsFog,
            fogPipeline);
        UploadFogFrameState(gl, frameFog);
        // Runtime zero keeps the authored UV attribute/varying active in the
        // linked shader without changing the untextured COLO output.
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
        foreach (EnbOpenGlIndexedDrawCommand command in resource.UploadPlan.IndexedPlan.DrawCommands)
        {
            EnbW3dFixedFunctionState fixedFunction = command.SourceRun.Binding.Semantics.FixedFunction;
            ApplyBlendState(gl, fixedFunction);
            ApplyAlphaTestState(gl, fixedFunction.AlphaTest);
            ApplyFogState(gl, MapFog(fixedFunction.FogMode, sceneFog, rendererSupportsFog, fogPipeline));
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

    public void DestroyResource(GlInterface? gl, EnbOpenGlFirstShaderMeshResource resource)
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
        _clipFromModelUniform = -1;
        _viewFromModelUniform = -1;
        _sceneDepthUniform = -1;
        _uvSemanticKeepAliveUniform = -1;
        _alphaTestModeUniform = -1;
        _alphaTestReferenceUniform = -1;
        _fogRangeUniform = -1;
        _fogEnabledUniform = -1;
        _fogColorUniform = -1;
        _uniform4f = null;
        _cullFace = null;
        _blendFunc = null;
        _frontFace = null;
        ContextIdentity = null;
    }

    /// <summary>
    /// Converts only the depth convention: canonical NDC z 0..1 becomes
    /// OpenGL NDC z -1..1. X/Y and W are unchanged.
    /// </summary>
    public static Matrix4x4 CreateOpenGlClipFromCanonical(Matrix4x4 canonicalClipFromModel)
    {
        if (!IsFinite(canonicalClipFromModel))
            throw new ArgumentOutOfRangeException(nameof(canonicalClipFromModel), "Canonical clip transform must be finite.");

        var depthRemap = new Matrix4x4(
            1f, 0f, 0f, 0f,
            0f, 1f, 0f, 0f,
            0f, 0f, 2f, 0f,
            0f, 0f, -1f, 1f);
        return canonicalClipFromModel * depthRemap;
    }

    public static (string VertexShader, string FragmentShader) CreateFirstLaneShaderSources(GlVersion version)
    {
        if (!IsSupportedShaderContext(version))
            throw new NotSupportedException($"Unsupported OpenGL shader context {version}.");

        const string vertexBody =
            "uniform mat4 uClipFromModel;\n" +
            "uniform mat4 uViewFromModel;\n" +
            "uniform vec4 uFogRange;\n" +
            "float fogVisibility(vec3 p){ if(uFogRange.x==0.0 && uFogRange.y==0.0) return 0.0; float d=uFogRange.z>0.5?length(p):abs(p.z); return clamp((uFogRange.y-d)/(uFogRange.y-uFogRange.x),0.0,1.0); }\n" +
            "void main(){ vec3 viewPosition=(uViewFromModel*vec4(aPosition,1.0)).xyz; vFogVisibility=fogVisibility(viewPosition); gl_Position = uClipFromModel * vec4(aPosition, 1.0); vColor1 = aColor1; vTexCoord0 = aTexCoord0; }\n";
        const string fragmentExpression =
            "vec4(vColor1.rgb + vec3((vTexCoord0.x + vTexCoord0.y) * uUvSemanticKeepAlive), vColor1.a)";
        const string alphaTestProgram =
            "uniform float uAlphaTestMode;\nuniform float uAlphaTestReference;\nuniform float uFogEnabled;\nuniform vec4 uFogColor;\n" +
            "bool alphaTestPass(float alpha){ if(uAlphaTestMode<0.5) return true; if(uAlphaTestMode<1.5) return alpha<=uAlphaTestReference; return alpha>=uAlphaTestReference; }\n" +
            "vec4 applyFog(vec4 color){ if(uFogEnabled>0.5) color.rgb=mix(uFogColor.rgb,color.rgb,vFogVisibility); return color; }\n";

        if (version.Type == GlProfileType.OpenGLES)
        {
            if (version.Major >= 3)
            {
                return (
                    "#version 300 es\nprecision highp float;\nin vec3 aPosition;\nin vec4 aColor1;\nin vec2 aTexCoord0;\nout mediump vec4 vColor1;\nout mediump vec2 vTexCoord0;\nout mediump float vFogVisibility;\n" + vertexBody,
                    $"#version 300 es\nprecision mediump float;\nin mediump vec4 vColor1;\nin mediump vec2 vTexCoord0;\nin mediump float vFogVisibility;\nuniform float uUvSemanticKeepAlive;\nout vec4 fragColor;\n{alphaTestProgram}void main(){{ vec4 color={fragmentExpression}; if(!alphaTestPass(color.a)) discard; fragColor=applyFog(color); }}\n");
            }

            return (
                "precision highp float;\nattribute vec3 aPosition;\nattribute vec4 aColor1;\nattribute vec2 aTexCoord0;\nvarying mediump vec4 vColor1;\nvarying mediump vec2 vTexCoord0;\nvarying mediump float vFogVisibility;\n" + vertexBody,
                $"precision mediump float;\nvarying mediump vec4 vColor1;\nvarying mediump vec2 vTexCoord0;\nvarying mediump float vFogVisibility;\nuniform float uUvSemanticKeepAlive;\n{alphaTestProgram}void main(){{ vec4 color={fragmentExpression}; if(!alphaTestPass(color.a)) discard; gl_FragColor=applyFog(color); }}\n");
        }

        if (version.Major > 3 || (version.Major == 3 && version.Minor >= 2))
        {
            return (
                "#version 150\nin vec3 aPosition;\nin vec4 aColor1;\nin vec2 aTexCoord0;\nout vec4 vColor1;\nout vec2 vTexCoord0;\nout float vFogVisibility;\n" + vertexBody,
                $"#version 150\nin vec4 vColor1;\nin vec2 vTexCoord0;\nin float vFogVisibility;\nuniform float uUvSemanticKeepAlive;\nout vec4 fragColor;\n{alphaTestProgram}void main(){{ vec4 color={fragmentExpression}; if(!alphaTestPass(color.a)) discard; fragColor=applyFog(color); }}\n");
        }

        string legacyVersion = version.Major == 2 && version.Minor == 0 ? "110" : "120";
        return (
            $"#version {legacyVersion}\nattribute vec3 aPosition;\nattribute vec4 aColor1;\nattribute vec2 aTexCoord0;\nvarying vec4 vColor1;\nvarying vec2 vTexCoord0;\nvarying float vFogVisibility;\n{vertexBody}",
            $"#version {legacyVersion}\nvarying vec4 vColor1;\nvarying vec2 vTexCoord0;\nvarying float vFogVisibility;\nuniform float uUvSemanticKeepAlive;\n{alphaTestProgram}void main(){{ vec4 color={fragmentExpression}; if(!alphaTestPass(color.a)) discard; gl_FragColor=applyFog(color); }}\n");
    }

    private void ApplyRasterState(GlInterface gl, EnbW3dProgrammableShaderLanePlan lanePlan)
    {
        gl.Enable(GlDepthTest);
        gl.DepthFunc(MapDepthCompare(lanePlan.DepthCompare));
        gl.DepthMask(lanePlan.DepthWriteEnabled ? 1 : 0);
        gl.Disable(GlBlend);

        EnbOpenGlCullState cull = MapCullState(lanePlan.CullMode);
        if (!cull.Enabled)
        {
            gl.Disable(GlCullFace);
            return;
        }

        EnsureCullProcedures(gl);
        gl.Enable(GlCullFace);
        RequireProc(_frontFace, "glFrontFace")(cull.FrontFace);
        RequireProc(_cullFace, "glCullFace")(cull.Face);
    }

    private void EnsureCullProcedures(GlInterface gl)
    {
        _cullFace ??= LoadRequiredProc<GlCullFaceProc>(gl, "glCullFace");
        _frontFace ??= LoadRequiredProc<GlFrontFaceProc>(gl, "glFrontFace");
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
        _blendFunc(MapBlendFactor(state.SourceBlend), MapBlendFactor(state.DestinationBlend));
    }

    private void ApplyAlphaTestState(GlInterface gl, EnbW3dAlphaTestSemantics alphaTest)
    {
        EnbOpenGlAlphaTestUniformState state = MapAlphaTest(alphaTest);
        gl.Uniform1f(_alphaTestModeUniform, state.Mode);
        gl.Uniform1f(_alphaTestReferenceUniform, state.Reference);
    }

    private void UploadFogFrameState(GlInterface gl, EnbOpenGlFogUniformState state)
    {
        RequireProc(_uniform4f, "glUniform4f")(_fogRangeUniform, state.Start, state.End, state.DistanceSource == EnbW3dFogDistanceSource.CameraRange ? 1f : 0f, 0f);
    }

    private void ApplyFogState(GlInterface gl, EnbOpenGlFogUniformState state)
    {
        gl.Uniform1f(_fogEnabledUniform, state.Enabled ? 1f : 0f);
        RequireProc(_uniform4f, "glUniform4f")(_fogColorUniform, state.Color.X, state.Color.Y, state.Color.Z, 0f);
    }

    public static EnbOpenGlFogUniformState MapFog(
        EnbW3dFogMode mode,
        EnbW3dSceneFogState scene,
        bool rendererSupportsFog,
        EnbW3dFogPipelineSemantics pipeline)
    {
        if (!Enum.IsDefined(mode) || !Enum.IsDefined(pipeline.DistanceSource))
            throw new NotSupportedException($"R6O received an undefined fog mode or distance source: mode={mode} distance={pipeline.DistanceSource}.");

        EnbW3dResolvedFogState resolved = EnbW3dResolvedFogState.ResolveCurrentRetail(
            mode,
            scene,
            rendererSupportsFog,
            pipeline);
        if (!resolved.Enabled)
            return new EnbOpenGlFogUniformState(false, Vector3.Zero, 0f, 1f, pipeline.DistanceSource);

        // Planet shared sky legitimately supplies 0/0. Visible vertices have
        // positive distance: the saturated linear factor is zero. Confirmed
        // with the local fixed-function driver probe; do not divide by zero.
        bool zeroInterval = resolved.Start == 0f && resolved.End == 0f;
        if (!float.IsFinite(resolved.Start) || !float.IsFinite(resolved.End) || (!zeroInterval && !(resolved.End > resolved.Start)))
            throw new NotSupportedException($"R6O cannot realize enabled current-retail fog with invalid Start/End ({resolved.Start}, {resolved.End}).");

        const float inv255 = 1f / 255f;
        Vector3 color = new(
            ((resolved.ColorRgb24 >> 16) & 0xFFu) * inv255,
            ((resolved.ColorRgb24 >> 8) & 0xFFu) * inv255,
            (resolved.ColorRgb24 & 0xFFu) * inv255);
        return new EnbOpenGlFogUniformState(true, color, resolved.Start, resolved.End, resolved.DistanceSource);
    }

    public static EnbOpenGlAlphaTestUniformState MapAlphaTest(EnbW3dAlphaTestSemantics alphaTest)
    {
        const float inv255 = 1.0f / 255.0f;
        if (!alphaTest.Enabled && alphaTest.Compare == EnbRenderDepthCompare.Always && alphaTest.Reference == 0)
            return new EnbOpenGlAlphaTestUniformState(0f, 0f);
        if (alphaTest.Enabled && alphaTest.Compare == EnbRenderDepthCompare.LessOrEqual && alphaTest.Reference == 0x9F)
            return new EnbOpenGlAlphaTestUniformState(1f, alphaTest.Reference * inv255);
        if (alphaTest.Enabled && alphaTest.Compare == EnbRenderDepthCompare.GreaterOrEqual && alphaTest.Reference == 0x60)
            return new EnbOpenGlAlphaTestUniformState(2f, alphaTest.Reference * inv255);

        throw new NotSupportedException($"R6N received a non-retail alpha-test tuple enabled={alphaTest.Enabled} compare={alphaTest.Compare} reference=0x{alphaTest.Reference:X2}.");
    }

    public static int MapBlendFactor(EnbRenderBlendFactor factor)
        => factor switch
        {
            EnbRenderBlendFactor.Zero => GlZero,
            EnbRenderBlendFactor.One => GlOne,
            EnbRenderBlendFactor.SourceColor => GlSourceColor,
            EnbRenderBlendFactor.InverseSourceColor => GlOneMinusSourceColor,
            EnbRenderBlendFactor.SourceAlpha => GlSourceAlpha,
            EnbRenderBlendFactor.InverseSourceAlpha => GlOneMinusSourceAlpha,
            _ => throw new NotSupportedException($"Unsupported R6M blend factor {factor}."),
        };

    public static int MapDepthCompare(EnbRenderDepthCompare compare)
        => compare switch
        {
            EnbRenderDepthCompare.Never => 0x0200,
            EnbRenderDepthCompare.Less => 0x0201,
            EnbRenderDepthCompare.Equal => 0x0202,
            EnbRenderDepthCompare.LessOrEqual => 0x0203,
            EnbRenderDepthCompare.Greater => 0x0204,
            EnbRenderDepthCompare.NotEqual => 0x0205,
            EnbRenderDepthCompare.GreaterOrEqual => 0x0206,
            EnbRenderDepthCompare.Always => 0x0207,
            _ => throw new NotSupportedException($"Unsupported R6C depth comparison {compare}."),
        };

    public static EnbOpenGlCullState MapCullState(EnbRenderCullMode mode)
        => mode switch
        {
            EnbRenderCullMode.None => new EnbOpenGlCullState(false, 0, 0),
            // EnbRenderCullMode preserves the current-retail D3DCULL selector:
            // Clockwise means discard clockwise triangles, CounterClockwise means
            // discard counter-clockwise triangles. With GL_BACK culling, select
            // the opposite winding as GL's front face so the named winding is
            // the one discarded. No additional viewport-Y reversal belongs here.
            EnbRenderCullMode.Clockwise => new EnbOpenGlCullState(true, GlCounterClockwise, GlBack),
            EnbRenderCullMode.CounterClockwise => new EnbOpenGlCullState(true, GlClockwise, GlBack),
            _ => throw new NotSupportedException($"Unsupported R6C cull mode {mode}."),
        };

    private static void BindVertexLayout(GlInterface gl)
    {
        gl.VertexAttribPointer(
            PositionAttributeIndex,
            3,
            GlConsts.GL_FLOAT,
            normalized: 0,
            stride: EnbOpenGlFirstShaderUploadPlan.StrideBytes,
            new IntPtr(EnbOpenGlFirstShaderUploadPlan.PositionOffsetBytes));
        gl.EnableVertexAttribArray(PositionAttributeIndex);

        gl.VertexAttribPointer(
            Color1AttributeIndex,
            4,
            GlConsts.GL_FLOAT,
            normalized: 0,
            stride: EnbOpenGlFirstShaderUploadPlan.StrideBytes,
            new IntPtr(EnbOpenGlFirstShaderUploadPlan.Color1OffsetBytes));
        gl.EnableVertexAttribArray(Color1AttributeIndex);

        gl.VertexAttribPointer(
            TextureCoordinate0AttributeIndex,
            2,
            GlConsts.GL_FLOAT,
            normalized: 0,
            stride: EnbOpenGlFirstShaderUploadPlan.StrideBytes,
            new IntPtr(EnbOpenGlFirstShaderUploadPlan.TextureCoordinate0OffsetBytes));
        gl.EnableVertexAttribArray(TextureCoordinate0AttributeIndex);
    }

    private static bool IsSupportedShaderContext(GlVersion version)
        => (version.Type == GlProfileType.OpenGL || version.Type == GlProfileType.OpenGLES) &&
           version.Major >= 2;

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
        => proc ?? throw new InvalidOperationException($"R6C OpenGL procedure {name} was not initialized.");

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
            throw new InvalidOperationException("The R6C OpenGL W3D backend has not been initialized on a current host GL context.");
    }

    private static bool IsFinite(Matrix4x4 value)
        => float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13) && float.IsFinite(value.M14) &&
           float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M23) && float.IsFinite(value.M24) &&
           float.IsFinite(value.M31) && float.IsFinite(value.M32) && float.IsFinite(value.M33) && float.IsFinite(value.M34) &&
           float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43) && float.IsFinite(value.M44);
}
