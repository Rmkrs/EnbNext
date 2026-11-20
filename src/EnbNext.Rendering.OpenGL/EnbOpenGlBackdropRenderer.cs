using EnbNext.Presentation;
using System.Runtime.InteropServices;
using EnbNext.Rendering.OpenGL;

namespace EnbNext.Rendering.OpenGL;

/// <summary>
/// Shared-framebuffer GPU backdrop and authored bitmap UI compositor.
/// World geometry is drawn by the Geometry partial; rasters are UI only.
/// </summary>
public sealed partial class EnbOpenGlBackdropRenderer
{
    private const int PositionAttributeIndex = 0;
    private const int TextureCoordinateAttributeIndex = 1;
    private const int GlTexture2D = 0x0DE1;
    private const int GlTexture0 = 0x84C0;
    private const int GlRgba = 0x1908;
    private const int GlUnsignedByte = 0x1401;
    private const int GlTextureMagFilter = 0x2800;
    private const int GlTextureMinFilter = 0x2801;
    private const int GlTextureWrapS = 0x2802;
    private const int GlTextureWrapT = 0x2803;
    private const int GlLinear = 0x2601;
    private const int GlClampToEdge = 0x812F;
    private const int GlUnpackAlignment = 0x0CF5;
    private const int GlTriangleStrip = 0x0005;
    private const int GlBlend = 0x0BE2;
    private const int GlDepthTest = 0x0B71;
    private const int GlCullFace = 0x0B44;
    private const int GlOne = 1;

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

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void GlBlendFuncProc(int sourceFactor, int destinationFactor);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void GlBlendFuncSeparateProc(int source, int destination, int sourceAlpha, int destinationAlpha);

    private int _program;
    private int _vertexBuffer;
    private int _vertexArray;
    private int _textureUniform = -1;
    private int _dazzleDepthUniform=-1, _dazzleTint0Uniform=-1, _dazzleTint1Uniform=-1, _dazzleTint2Uniform=-1;
    private GlBlendFuncSeparateProc? _blendFuncSeparate;
    private int _opacityUniform = -1;
    private int _brightnessUniform = -1;
    private int _alphaAdditiveUniform = -1;
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void GlUniform1fProc(int location, float value);
    private GlUniform1fProc? _uniform1f;
    private int _environmentTexture;
    private EnbUiRaster? _uploadedEnvironment;
    private GlGenTexturesProc? _genTextures;
    private GlDeleteTexturesProc? _deleteTextures;
    private GlActiveTextureProc? _activeTexture;
    private GlBindTextureProc? _bindTexture;
    private GlTexParameteriProc? _texParameteri;
    private GlPixelStoreiProc? _pixelStorei;
    private GlTexImage2DProc? _texImage2D;
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void GlCompressedTexImage2DProc(int target,int level,int internalFormat,int width,int height,int border,int imageSize,IntPtr data);
    private GlCompressedTexImage2DProc? _compressedTexImage2D;
    private int _nativeRgbaUniform=-1;
    private GlBlendFuncProc? _blendFunc;
    private (int Height,double Offset,float Scale) _overlayGeometry;

    public bool IsInitialized => _program != 0;
    public bool MapQuadMode { get; init; }

    public void Initialize(GlInterface gl)
    {
        ArgumentNullException.ThrowIfNull(gl);
        if (IsInitialized)
            throw new InvalidOperationException("The shared-backdrop OpenGL renderer is already initialized.");

        (string vertexSource, string fragmentSource) = CreateShaderSources(gl.ContextInfo.Version);
        if (MapQuadMode) vertexSource = vertexSource.Replace("vec2 aPosition", "vec4 aPosition")
            .Replace("vec4(aPosition,0.0,1.0)", "aPosition");
        if (_dazzleMode) vertexSource=vertexSource.Replace("void main()", "uniform float uDepth; void main()").Replace("vec4(aPosition,0.0,1.0)","vec4(aPosition,uDepth,1.0)");
        if (_dazzleMode) fragmentSource=fragmentSource.Replace("uniform sampler2D uTexture;", "uniform sampler2D uTexture; uniform float uTint0; uniform float uTint1; uniform float uTint2;").Replace(".bgra;", ".bgra * vec4(uTint0,uTint1,uTint2,1.0);");
        if (!_mapTextureMode && !_dazzleMode)
            fragmentSource = fragmentSource.Replace("uniform sampler2D uTexture;", "uniform sampler2D uTexture; uniform float uOpacity;")
                .Replace(".bgra;", MapQuadMode ? ".bgra * vec4(vec3(uAlphaAdditive!=0?1.0:uOpacity),uOpacity);" : ".bgra * vec4(1.0,1.0,1.0,uOpacity);");
        if (MapQuadMode) fragmentSource=fragmentSource.Replace("uniform float uOpacity;","uniform float uOpacity; uniform int uAlphaAdditive;");
        if (!_mapTextureMode && !_dazzleMode && !MapQuadMode)
            fragmentSource=fragmentSource.Replace("uniform float uOpacity;","uniform float uOpacity; uniform float uBrightness;")
                .Replace("vec4(1.0,1.0,1.0,uOpacity)","vec4(vec3(uBrightness),uOpacity)");
        if (_mapTextureMode) fragmentSource=CreateMapTextureShader(fragmentSource);
        string textureSample=fragmentSource.Contains("texture2D(",StringComparison.Ordinal)?"texture2D":"texture";
        fragmentSource=fragmentSource.Replace(textureSample+"(uTexture,vTexCoord).bgra","readTexture(vTexCoord)")
            .Replace("void main()","uniform int uNativeRgba; vec4 readTexture(vec2 uv){vec4 value="+textureSample+
                "(uTexture,uv);return uNativeRgba!=0?value:value.bgra;} void main()");
        // Map quads (hundreds on the galaxy map) share one program per context and source: the same shader, compiled once.
        var shared = SharesProgram ? FindSharedProgram(gl, vertexSource, fragmentSource) : null;
        int vertexShader = shared is null ? gl.CreateShader(GlConsts.GL_VERTEX_SHADER) : 0;
        int fragmentShader = shared is null ? gl.CreateShader(GlConsts.GL_FRAGMENT_SHADER) : 0;
        int program = 0;
        try
        {
            if (shared is not null) program = shared.Program;
            else
            {
            string? vertexError = gl.CompileShaderAndGetError(vertexShader, vertexSource);
            if (!string.IsNullOrWhiteSpace(vertexError))
                throw new InvalidOperationException($"Backdrop vertex shader compilation failed: {vertexError}");

            string? fragmentError = gl.CompileShaderAndGetError(fragmentShader, fragmentSource);
            if (!string.IsNullOrWhiteSpace(fragmentError))
                throw new InvalidOperationException($"Backdrop fragment shader compilation failed: {fragmentError}");

            program = gl.CreateProgram();
            gl.AttachShader(program, vertexShader);
            gl.AttachShader(program, fragmentShader);
            gl.BindAttribLocationString(program, PositionAttributeIndex, "aPosition");
            gl.BindAttribLocationString(program, TextureCoordinateAttributeIndex, "aTexCoord");
            string? linkError = gl.LinkProgramAndGetError(program);
            if (!string.IsNullOrWhiteSpace(linkError))
                throw new InvalidOperationException($"Backdrop program link failed: {linkError}");
            }

            _textureUniform = gl.GetUniformLocationString(program, "uTexture");
            _nativeRgbaUniform = gl.GetUniformLocationString(program, "uNativeRgba");
            _opacityUniform = gl.GetUniformLocationString(program, "uOpacity");
            _brightnessUniform = gl.GetUniformLocationString(program, "uBrightness");
            _alphaAdditiveUniform = gl.GetUniformLocationString(program, "uAlphaAdditive");
            _uniform1f = LoadRequiredProc<GlUniform1fProc>(gl, "glUniform1f");
            _blendFuncSeparate = LoadRequiredProc<GlBlendFuncSeparateProc>(gl, "glBlendFuncSeparate");
            if (_dazzleMode)
            {
                _dazzleDepthUniform=gl.GetUniformLocationString(program,"uDepth");
                _dazzleTint0Uniform=gl.GetUniformLocationString(program,"uTint0");
                _dazzleTint1Uniform=gl.GetUniformLocationString(program,"uTint1");
                _dazzleTint2Uniform=gl.GetUniformLocationString(program,"uTint2");
            }
            if (_textureUniform < 0)
                throw new InvalidOperationException("Backdrop program did not expose uTexture.");

            _genTextures = LoadRequiredProc<GlGenTexturesProc>(gl, "glGenTextures");
            _deleteTextures = LoadRequiredProc<GlDeleteTexturesProc>(gl, "glDeleteTextures");
            _activeTexture = LoadRequiredProc<GlActiveTextureProc>(gl, "glActiveTexture");
            _bindTexture = LoadRequiredProc<GlBindTextureProc>(gl, "glBindTexture");
            _texParameteri = LoadRequiredProc<GlTexParameteriProc>(gl, "glTexParameteri");
            _pixelStorei = LoadRequiredProc<GlPixelStoreiProc>(gl, "glPixelStorei");
            _texImage2D = LoadRequiredProc<GlTexImage2DProc>(gl, "glTexImage2D");
            _compressedTexImage2D = LoadRequiredProc<GlCompressedTexImage2DProc>(gl, "glCompressedTexImage2D");
            _blendFunc = LoadRequiredProc<GlBlendFuncProc>(gl, "glBlendFunc");

            bool useVertexArray = gl.IsGenVertexArraysAvailable &&
                                  gl.IsBindVertexArrayAvailable &&
                                  gl.IsDeleteVertexArraysAvailable;
            if (EnbOpenGlIndexedSubmissionBackend.RequiresVertexArray(gl.ContextInfo.Version) && !useVertexArray)
                throw new NotSupportedException("The shared-backdrop OpenGL path requires a VAO on this core profile, but the GL context did not expose VAO support.");

            if (useVertexArray)
            {
                _vertexArray = gl.GenVertexArray();
                gl.BindVertexArray(_vertexArray);
            }

            // Top-left source origin, OpenGL bottom-left framebuffer origin.
            // Each vertex is x, y, u, v and forms a full-screen triangle strip.
            float[] vertices =
            [
                -1f, -1f, 0f, 1f,
                 1f, -1f, 1f, 1f,
                -1f,  1f, 0f, 0f,
                 1f,  1f, 1f, 0f,
            ];
            _vertexBuffer = gl.GenBuffer();
            gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, _vertexBuffer);
            UploadArray(gl, vertices);
            BindVertexLayout(gl);
            if (useVertexArray)
                gl.BindVertexArray(0);
            gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, 0);

            if (SharesProgram) _sharedProgram = shared ?? AddSharedProgram(gl, vertexSource, fragmentSource, program);
            if (_sharedProgram is not null) _sharedProgram.Users++;
            _program = program;
            program = 0;
        }
        finally
        {
            if (vertexShader != 0) gl.DeleteShader(vertexShader);
            if (fragmentShader != 0) gl.DeleteShader(fragmentShader);
            if (program != 0 && shared is null)
                gl.DeleteProgram(program);
        }
    }

    public void RenderOverlay(GlInterface gl, int framebuffer, int x, int y,
        int width, int height, EnbUiRaster raster, bool additive = false,double verticalOffset=0,float opacity=1,float verticalScale=1,bool sourceAlphaAdditive=false,float brightness=1)
    {
        gl.BindFramebuffer(GlConsts.GL_FRAMEBUFFER, framebuffer);
        gl.Viewport(x, verticalOffset==0?y:y-1, width, verticalOffset==0?height:height+2);
        gl.Disable(GlDepthTest);
        gl.DepthMask(0);
        gl.Disable(GlCullFace);
        gl.UseProgram(_program);
        gl.Uniform1i(_textureUniform, 0);
        if (_opacityUniform >= 0) _uniform1f!(_opacityUniform, Math.Clamp(opacity,0,1));
        if (_brightnessUniform >= 0) _uniform1f!(_brightnessUniform,brightness);
        if (_alphaAdditiveUniform >= 0) gl.Uniform1i(_alphaAdditiveUniform,sourceAlphaAdditive?1:0);
        if (_nativeRgbaUniform >= 0) gl.Uniform1i(_nativeRgbaUniform, 0); // set each draw: the program can be shared
        BindGeometry(gl);
        var geometry=verticalOffset==0 && verticalScale==1 ? default : (height,verticalOffset,verticalScale);
        if(_overlayGeometry!=geometry)
        {
            gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER,_vertexBuffer);
            // Keep scrolling between pixels; rounding the viewport alone turns
            // slow text movement into visible whole-pixel jumps.
            float bottom=verticalOffset==0?-1:(float)(-1+2*(1+verticalOffset)/(height+2));
            float top=verticalOffset==0?1:(float)(1-2*(1-verticalOffset)/(height+2));
            float center=(bottom+top)*.5f, half=(top-bottom)*.5f*verticalScale;
            bottom=center-half; top=center+half;
            UploadArray(gl,[-1,bottom,0,1,1,bottom,1,1,-1,top,0,0,1,top,1,0]);
            _overlayGeometry=geometry;
        }
        _environmentTexture = EnsureTexture(gl, _environmentTexture, raster, ref _uploadedEnvironment);
        RequireProc(_activeTexture, "glActiveTexture")(GlTexture0);
        RequireProc(_bindTexture, "glBindTexture")(GlTexture2D, _environmentTexture);
        gl.Enable(GlBlend);
        _blendFuncSeparate!(
            additive && !sourceAlphaAdditive ? 1 : 0x0302, additive ? 1 : 0x0303,
            additive ? 0 : 1, additive ? 1 : 0x0303); // Highlight clone adds RGB without altering destination coverage.
        gl.DrawArrays(GlTriangleStrip, 0, 4);
        gl.Disable(GlBlend);
        if (_vertexArray != 0) gl.BindVertexArray(0);
        gl.UseProgram(0);
        gl.DepthMask(1);
    }

    public void Deinitialize(GlInterface? gl)
    {
        InvalidateStarDeviceState();
        // A lost context has already retired its native objects.
        DeinitializeWorld(gl);
        DeleteTexture(ref _environmentTexture);
        if (_vertexArray != 0 && gl?.IsDeleteVertexArraysAvailable == true)
            gl?.DeleteVertexArray(_vertexArray);
        if (_vertexBuffer != 0)
            gl?.DeleteBuffer(_vertexBuffer);
        if (_program != 0 && ReleaseProgram(gl))
            gl?.DeleteProgram(_program);
        _program = 0;
        _vertexBuffer = 0;
        _vertexArray = 0;
        _textureUniform = -1;
        _uploadedEnvironment = null;
        _overlayGeometry=default;
        _genTextures = null;
        _deleteTextures = null;
        _activeTexture = null;
        _bindTexture = null;
        _texParameteri = null;
        _pixelStorei = null;
        _texImage2D = null;
        _compressedTexImage2D = null;
        _blendFunc = null;
        _blendFuncSeparate=null;
        _dazzleDepthUniform=_dazzleTint0Uniform=_dazzleTint1Uniform=_dazzleTint2Uniform=-1;

        void DeleteTexture(ref int texture)
        {
            if (texture == 0 || _deleteTextures is null)
                return;
            if (gl is not null) _deleteTextures(1, ref texture);
            texture = 0;
        }
    }

    private int EnsureTexture(
        GlInterface gl,
        int texture,
        EnbUiRaster raster,
        ref EnbUiRaster? uploaded)
    {
        var compressed=raster.CompressedSource?.MipLevels[0];
        bool native=compressed is not null && !compressed.CompressedBlocks.IsEmpty;
        if(_nativeRgbaUniform>=0)gl.Uniform1i(_nativeRgbaUniform,native?1:0);
        if (ReferenceEquals(uploaded, raster) && texture != 0)
            return texture;

        if (texture == 0)
            RequireProc(_genTextures, "glGenTextures")(1, out texture);

        RequireProc(_activeTexture, "glActiveTexture")(GlTexture0);
        RequireProc(_bindTexture, "glBindTexture")(GlTexture2D, texture);
        RequireProc(_texParameteri, "glTexParameteri")(GlTexture2D, GlTextureMinFilter, GlLinear);
        RequireProc(_texParameteri, "glTexParameteri")(GlTexture2D, GlTextureMagFilter, GlLinear);
        RequireProc(_texParameteri, "glTexParameteri")(GlTexture2D, GlTextureWrapS, GlClampToEdge);
        RequireProc(_texParameteri, "glTexParameteri")(GlTexture2D, GlTextureWrapT, GlClampToEdge);
        RequireProc(_pixelStorei, "glPixelStorei")(GlUnpackAlignment, 1);

        if(native)
        {
            UploadCompressedTexture(compressed!);
            uploaded=raster;
            return texture;
        }

        byte[] pixels = raster.BgraPixels.ToArray();
        unsafe
        {
            fixed (byte* pointer = pixels)
            {
                RequireProc(_texImage2D, "glTexImage2D")(
                    GlTexture2D,
                    0,
                    GlRgba,
                    raster.Width,
                    raster.Height,
                    0,
                    GlRgba,
                    GlUnsignedByte,
                    new IntPtr(pointer));
            }
        }

        uploaded = raster;
        return texture;
    }

    private void BindGeometry(GlInterface gl)
    {
        if (_vertexArray != 0)
        {
            gl.BindVertexArray(_vertexArray);
            return;
        }

        gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, _vertexBuffer);
        BindVertexLayout(gl);
    }

    private void BindVertexLayout(GlInterface gl)
    {
        int components = MapQuadMode ? 4 : 2;
        int stride = sizeof(float) * (components+2);
        gl.VertexAttribPointer(PositionAttributeIndex, components, GlConsts.GL_FLOAT, 0, stride, IntPtr.Zero);
        gl.EnableVertexAttribArray(PositionAttributeIndex);
        gl.VertexAttribPointer(TextureCoordinateAttributeIndex, 2, GlConsts.GL_FLOAT, 0, stride, new IntPtr(sizeof(float) * components));
        gl.EnableVertexAttribArray(TextureCoordinateAttributeIndex);
    }

    private static unsafe void UploadArray(GlInterface gl, ReadOnlySpan<float> values)
    {
        fixed (float* pointer = values)
        {
            gl.BufferData(
                GlConsts.GL_ARRAY_BUFFER,
                new IntPtr(checked(values.Length * sizeof(float))),
                new IntPtr(pointer),
                GlConsts.GL_STATIC_DRAW);
        }
    }

    private static (string VertexShader, string FragmentShader) CreateShaderSources(GlVersion version)
    {
        if ((version.Type != GlProfileType.OpenGL && version.Type != GlProfileType.OpenGLES) || version.Major < 2)
            throw new NotSupportedException($"Shared-backdrop presentation requires OpenGL/OpenGL ES 2.0+, not {version}.");

        if (version.Type == GlProfileType.OpenGLES)
        {
            if (version.Major >= 3)
            {
                return (
                    "#version 300 es\nprecision highp float;\nin vec2 aPosition;\nin vec2 aTexCoord;\nout vec2 vTexCoord;\nvoid main(){ vTexCoord=aTexCoord; gl_Position=vec4(aPosition,0.0,1.0); }\n",
                    "#version 300 es\nprecision mediump float;\nuniform sampler2D uTexture;\nin vec2 vTexCoord;\nout vec4 fragColor;\nvoid main(){ fragColor=texture(uTexture,vTexCoord).bgra; }\n");
            }

            return (
                "precision highp float;\nattribute vec2 aPosition;\nattribute vec2 aTexCoord;\nvarying vec2 vTexCoord;\nvoid main(){ vTexCoord=aTexCoord; gl_Position=vec4(aPosition,0.0,1.0); }\n",
                "precision mediump float;\nuniform sampler2D uTexture;\nvarying vec2 vTexCoord;\nvoid main(){ gl_FragColor=texture2D(uTexture,vTexCoord).bgra; }\n");
        }

        if (version.Major > 3 || (version.Major == 3 && version.Minor >= 2))
        {
            return (
                "#version 150\nin vec2 aPosition;\nin vec2 aTexCoord;\nout vec2 vTexCoord;\nvoid main(){ vTexCoord=aTexCoord; gl_Position=vec4(aPosition,0.0,1.0); }\n",
                "#version 150\nuniform sampler2D uTexture;\nin vec2 vTexCoord;\nout vec4 fragColor;\nvoid main(){ fragColor=texture(uTexture,vTexCoord).bgra; }\n");
        }

        string legacyVersion = version.Major == 2 && version.Minor == 0 ? "110" : "120";
        return (
            $"#version {legacyVersion}\nattribute vec2 aPosition;\nattribute vec2 aTexCoord;\nvarying vec2 vTexCoord;\nvoid main(){{ vTexCoord=aTexCoord; gl_Position=vec4(aPosition,0.0,1.0); }}\n",
            $"#version {legacyVersion}\nuniform sampler2D uTexture;\nvarying vec2 vTexCoord;\nvoid main(){{ gl_FragColor=texture2D(uTexture,vTexCoord).bgra; }}\n");
    }

    private static T LoadRequiredProc<T>(GlInterface gl, string name)
        where T : Delegate
    {
        IntPtr address = gl.GetProcAddress(name);
        if (address == IntPtr.Zero)
            throw new NotSupportedException($"OpenGL context did not expose required core procedure {name}.");
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    private static T RequireProc<T>(T? proc, string name)
        where T : Delegate
        => proc ?? throw new InvalidOperationException($"Shared-backdrop OpenGL procedure {name} was not initialized.");
}
