using System.Numerics;
using System.Runtime.InteropServices;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Rendering.OpenGL;

public sealed partial class EnbOpenGlBackdropRenderer
{
    public int SubmittedWorldVertices { get; private set; }
    public void BeginWorldStatistics() => SubmittedWorldVertices = 0;
    private int _worldProgram;
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void GlUniform3fProc(int location, float x, float y, float z);
    private GlUniform3fProc? _uniform3f;
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void BackdropDepthRange(double near, double far);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void BackdropDepthRangeFloat(float near, float far);
    private Action<float, float>? _backdropDepthRange;
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void BackdropGetInteger(int name, out int value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void BackdropGetRange(int name, out Vector2 value);
    private BackdropGetInteger? _getBackdropInteger;
    private BackdropGetRange? _getBackdropRange;
    private int _rightUniform, _upUniform, _forwardUniform, _projectionUniform, _dualUniform, _farUniform, _texturedUniform;
    private readonly Dictionary<EnbBackdropGeometry, WorldBuffers> _worldBuffers =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, int> _worldTextures = new(ReferenceEqualityComparer.Instance);

    public void Render(GlInterface gl, int framebuffer, int width, int height,
        EnbBackdropFrame? frame, EnbResolvedRenderCamera camera) =>
        RenderCore(gl, framebuffer, width, height, frame, camera, null, null, null);

    public void RenderSpace(GlInterface gl, int framebuffer, int width, int height,
        EnbBackdropFrame? frame, EnbResolvedRenderCamera camera,
        EnbSpaceScenePass spacePass, Action? observe, Action? observeDraw, Vector2? depthRangeOverride = null) =>
        RenderCore(gl, framebuffer, width, height, frame, camera, spacePass, observe, observeDraw, depthRangeOverride);

    private void RenderCore(GlInterface gl, int framebuffer, int width, int height,
        EnbBackdropFrame? frame, EnbResolvedRenderCamera camera,
        EnbSpaceScenePass? spacePass, Action? observe, Action? observeDraw, Vector2? depthRangeOverride = null)
    {
        if (frame is null)
        {
            RetireWorldAssets(gl, null);
            return;
        }
        if (_worldProgram == 0) InitializeWorld(gl);
        RetireWorldAssets(gl, frame);
        _getBackdropInteger!(GlDepthTest, out int depthEnabled);
        _getBackdropInteger!(0x0B72, out int depthWrite);
        _getBackdropInteger!(0x0B74, out int depthFunction);
        _getBackdropRange!(0x0B70, out Vector2 depthRange);
        gl.BindFramebuffer(GlConsts.GL_FRAMEBUFFER, framebuffer);
        gl.Viewport(0, 0, width, height);
        gl.Disable(GlDepthTest);
        gl.DepthMask(0);
        gl.Disable(GlCullFace);
        gl.UseProgram(_worldProgram);
        SetVector(_rightUniform, camera.Basis.Right);
        SetVector(_upUniform, camera.Basis.Up);
        SetVector(_forwardUniform, camera.Basis.Forward);
        var clip = spacePass == EnbSpaceScenePass.Stars ? EnbRetailSpaceComposition.StarsClip : EnbRetailSpaceComposition.BackdropClip;
        _uniform3f!(_projectionUniform, camera.Projection.Matrix.M11,
            camera.Projection.Matrix.M22, spacePass.HasValue ? clip.NearPlane : frame.Environment.WriteFarDepth ? 20 : camera.Projection.ClipRange.NearPlane);
        _uniform1f!(_farUniform, spacePass.HasValue ? clip.FarPlane : frame.Environment.WriteFarDepth ? 100000 : 0);
        // Current ordinary mode-0 shared sky uses viewport MinZ=MaxZ=1.
        if (spacePass.HasValue || frame.Environment.WriteFarDepth) _backdropDepthRange!(depthRangeOverride?.X ?? 1, depthRangeOverride?.Y ?? 1);
        try
        {
            observe?.Invoke();
            if (spacePass != EnbSpaceScenePass.Stars)
            {
                DrawWorldGeometry(gl, frame.Environment, additive: false, observeDraw);
                foreach (var layer in frame.Environment.AdditiveLayers) DrawWorldGeometry(gl, layer, additive: true, observeDraw);
            }
            if (!spacePass.HasValue || spacePass == EnbSpaceScenePass.Stars)
                foreach (var layer in frame.Stars) DrawWorldGeometry(gl, layer, additive: true, observeDraw);
        }
        finally
        {
            gl.DepthMask(depthWrite);
            gl.DepthFunc(depthFunction);
            if (depthEnabled != 0) gl.Enable(GlDepthTest); else gl.Disable(GlDepthTest);
            _backdropDepthRange!(depthRange.X, depthRange.Y);
            gl.Disable(GlBlend);
            if (_vertexArray != 0) gl.BindVertexArray(0);
            gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, 0);
            RequireProc(_activeTexture, "glActiveTexture")(GlTexture0);
            gl.UseProgram(0);
        }

        void SetVector(int location, Vector3 value) => _uniform3f!(location, value.X, value.Y, value.Z);
    }

    private void DrawWorldGeometry(GlInterface gl, EnbBackdropGeometry geometry, bool additive, Action? observeDraw)
    {
        // A movie without CREATE_SKYBOX has no sky vertices or texture owner.
        if (geometry.Vertices.IsDefaultOrEmpty) return;
        if (geometry.WriteFarDepth)
        {
            gl.Enable(GlDepthTest);
            gl.DepthFunc(0x0207); // ALWAYS; depth must be enabled for writes.
            gl.DepthMask(1);
        }
        else { gl.Disable(GlDepthTest); gl.DepthMask(0); }
        if (!_worldBuffers.TryGetValue(geometry, out var buffers))
        {
            float[] data = new float[geometry.Vertices.Length * 10];
            int i = 0;
            foreach (var v in geometry.Vertices)
            {
                data[i++] = v.Position.X; data[i++] = v.Position.Y; data[i++] = v.Position.Z;
                data[i++] = v.ViewOffset.X; data[i++] = v.ViewOffset.Y;
                data[i++] = v.Uv0.X; data[i++] = v.Uv0.Y;
                data[i++] = v.Uv1.X; data[i++] = v.Uv1.Y; data[i++] = v.Intensity;
            }
            int vao = _vertexArray != 0 ? gl.GenVertexArray() : 0;
            int vbo = gl.GenBuffer();
            if (vao != 0) gl.BindVertexArray(vao);
            gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, vbo);
            UploadArray(gl, data);
            BindWorldLayout(gl);
            buffers = new(vbo, vao);
            _worldBuffers.Add(geometry, buffers);
        }
        if (buffers.VertexArray != 0) gl.BindVertexArray(buffers.VertexArray);
        else
        {
            gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, buffers.VertexBuffer);
            BindWorldLayout(gl);
        }
        if (geometry.StarTexture is {} starTexture)
        {
            if (!BindStarTexture(gl, starTexture)) return;
        }
        else if (!geometry.IsUntextured)
        {
            BindWorldTexture(gl, (object?)geometry.ResolvedStage0 ?? geometry.Stage0!, 0);
            BindWorldTexture(gl, (object?)geometry.Stage1 ?? geometry.ResolvedStage0 ?? (object)geometry.Stage0!, 1);
        }
        gl.Uniform1i(_texturedUniform, geometry.IsUntextured ? 0 : 1);
        gl.Uniform1i(_dualUniform, geometry.Stage1 is null ? 0 : 1);
        if (additive)
        {
            gl.Enable(GlBlend);
            RequireProc(_blendFunc, "glBlendFunc")(GlOne, GlOne);
        }
        else gl.Disable(GlBlend);
        observeDraw?.Invoke();
        gl.DrawArrays(GlConsts.GL_TRIANGLES, 0, geometry.Vertices.Length);
        SubmittedWorldVertices += geometry.Vertices.Length;
    }

    private void BindWorldTexture(GlInterface gl, object source, int unit)
    {
        RequireProc(_activeTexture, "glActiveTexture")(GlTexture0 + unit);
        if (_worldTextures.TryGetValue(source, out int texture))
        {
            RequireProc(_bindTexture, "glBindTexture")(GlTexture2D, texture);
            return;
        }
        RequireProc(_genTextures, "glGenTextures")(1, out texture);
        RequireProc(_bindTexture, "glBindTexture")(GlTexture2D, texture);
        RequireProc(_texParameteri, "glTexParameteri")(GlTexture2D, GlTextureMinFilter, GlLinear);
        RequireProc(_texParameteri, "glTexParameteri")(GlTexture2D, GlTextureMagFilter, GlLinear);
        RequireProc(_texParameteri, "glTexParameteri")(GlTexture2D, GlTextureWrapS, 0x2901); // REPEAT
        RequireProc(_texParameteri, "glTexParameteri")(GlTexture2D, GlTextureWrapT, 0x2901);
        RequireProc(_pixelStorei, "glPixelStorei")(GlUnpackAlignment, 1);
        // Source rows and runtime UVs already share the retail (U, 1-V) import.
        // No bitmap-origin flip or BGRA swizzle belongs on this mesh path.
        var (width, height, mip) = source switch
        {
            EnbDdsTexture dds => (dds.Width, dds.Height, dds.MipLevels[0]),
            EnbTextureImage image => (image.Width, image.Height, image.MipLevels[0]),
            _ => throw new ArgumentException("Decoded backdrop texture required.", nameof(source)),
        };
        var pixels = mip.Pixels.Span;
        if(!mip.CompressedBlocks.IsEmpty)
        {
            UploadCompressedTexture(mip);
            _worldTextures.Add(source,texture);
            return;
        }
        byte[] rgba = new byte[pixels.Length * 4];
        for (int i = 0; i < pixels.Length; i++)
        {
            rgba[i * 4] = pixels[i].R; rgba[i * 4 + 1] = pixels[i].G;
            rgba[i * 4 + 2] = pixels[i].B; rgba[i * 4 + 3] = pixels[i].A;
        }
        unsafe
        {
            fixed (byte* pointer = rgba)
                RequireProc(_texImage2D, "glTexImage2D")(GlTexture2D, 0, GlRgba,
                    width, height, 0, GlRgba, GlUnsignedByte, new IntPtr(pointer));
        }
        _worldTextures.Add(source, texture);
    }

    private unsafe void UploadCompressedTexture(EnbTextureMipLevel mip, int level = 0)
    {
        int format=mip.CompressedFourCc switch
        {
            0x31545844=>0x83F1,
            0x32545844 or 0x33545844=>0x83F2,
            0x34545844 or 0x35545844=>0x83F3,
            _=>throw new InvalidDataException("Unsupported backdrop compressed format."),
        };
        fixed(byte* blocks=mip.CompressedBlocks.Span)
            RequireProc(_compressedTexImage2D,"glCompressedTexImage2D")(
                GlTexture2D,level,format,mip.Width,mip.Height,0,mip.CompressedBlocks.Length,new IntPtr(blocks));
    }

    private static void BindWorldLayout(GlInterface gl)
    {
        Attribute(0, 3, 0);
        Attribute(1, 2, 3);
        Attribute(2, 2, 5);
        Attribute(3, 2, 7);
        Attribute(4, 1, 9);
        void Attribute(int index, int count, int offset)
        {
            gl.VertexAttribPointer(index, count, GlConsts.GL_FLOAT, 0, 10 * sizeof(float),
                new IntPtr(offset * sizeof(float)));
            gl.EnableVertexAttribArray(index);
        }
    }

    private void InitializeWorld(GlInterface gl)
    {
        _getBackdropInteger = LoadRequiredProc<BackdropGetInteger>(gl, "glGetIntegerv");
        _getBackdropRange = LoadRequiredProc<BackdropGetRange>(gl, "glGetFloatv");
        IntPtr depthRange = gl.GetProcAddress("glDepthRangef");
        if (depthRange != IntPtr.Zero)
        {
            var set = Marshal.GetDelegateForFunctionPointer<BackdropDepthRangeFloat>(depthRange);
            _backdropDepthRange = (near, far) => set(near, far);
        }
        else
        {
            var set = LoadRequiredProc<BackdropDepthRange>(gl, "glDepthRange");
            _backdropDepthRange = (near, far) => set(near, far);
        }
        _uniform3f = LoadRequiredProc<GlUniform3fProc>(gl, "glUniform3f");
        GlVersion version = gl.ContextInfo.Version;
        bool es = version.Type == GlProfileType.OpenGLES;
        bool modern = es ? version.Major >= 3 : version.Major > 3 || version.Major == 3 && version.Minor >= 2;
        string header = es
            ? (modern ? "#version 300 es\n" : "") + "precision highp float;\n"
            : modern ? "#version 150\n" : version.Major == 2 && version.Minor == 0 ? "#version 110\n" : "#version 120\n";
        string input = modern ? "in" : "attribute";
        string output = modern ? "out" : "varying";
        string fragmentInput = modern ? "in" : "varying";
        string sample = modern ? "texture" : "texture2D";
        string vertex = header + $$"""
            {{input}} vec3 aPosition;
            {{input}} vec2 aOffset;
            {{input}} vec2 aUv0;
            {{input}} vec2 aUv1;
            {{input}} float aIntensity;
            uniform vec3 uRight, uUp, uForward, uProjection;
            uniform float uFar;
            {{output}} vec2 vUv0, vUv1;
            {{output}} float vIntensity;
            void main() {
                float depth = dot(aPosition, uForward);
                vec2 view = vec2(dot(aPosition,uRight),dot(aPosition,uUp)) + aOffset;
                // Canonical near clip at depth=near, no far cutoff for the backdrop.
                float z = uFar > 0.0 ? ((uFar+uProjection.z)*depth-2.0*uFar*uProjection.z)/(uFar-uProjection.z) : depth-2.0*uProjection.z;
                gl_Position = vec4(view*uProjection.xy,z,depth);
                vUv0=aUv0; vUv1=aUv1; vIntensity=aIntensity;
            }
            """;
        string fragment = header + (modern ? "out vec4 fragColor;\n" : "") + $$"""
            {{fragmentInput}} vec2 vUv0, vUv1;
            {{fragmentInput}} float vIntensity;
            uniform sampler2D uStage0, uStage1;
            uniform int uDual, uTextured;
            void main() {
                vec3 color = uTextured != 0 ? {{sample}}(uStage0,vUv0).rgb : vec3(1.0);
                if(uDual != 0) color *= {{sample}}(uStage1,vUv1).rgb;
                {{(modern ? "fragColor" : "gl_FragColor")}} = vec4(color*clamp(vIntensity,0.0,1.0),1.0);
            }
            """;
        int vs = gl.CreateShader(GlConsts.GL_VERTEX_SHADER), fs = gl.CreateShader(GlConsts.GL_FRAGMENT_SHADER);
        int program = 0;
        try
        {
            string? error = gl.CompileShaderAndGetError(vs, vertex);
            if (!string.IsNullOrWhiteSpace(error)) throw new InvalidOperationException("World backdrop vertex: " + error);
            error = gl.CompileShaderAndGetError(fs, fragment);
            if (!string.IsNullOrWhiteSpace(error)) throw new InvalidOperationException("World backdrop fragment: " + error);
            program = gl.CreateProgram();
            gl.AttachShader(program, vs); gl.AttachShader(program, fs);
            string[] attributes = ["aPosition", "aOffset", "aUv0", "aUv1", "aIntensity"];
            for (int i = 0; i < attributes.Length; i++) gl.BindAttribLocationString(program, i, attributes[i]);
            error = gl.LinkProgramAndGetError(program);
            if (!string.IsNullOrWhiteSpace(error)) throw new InvalidOperationException("World backdrop link: " + error);
            _rightUniform = Uniform("uRight"); _upUniform = Uniform("uUp");
            _forwardUniform = Uniform("uForward"); _projectionUniform = Uniform("uProjection");
            _dualUniform = Uniform("uDual"); _texturedUniform = Uniform("uTextured");
            _farUniform = Uniform("uFar");
            gl.UseProgram(program);
            gl.Uniform1i(Uniform("uStage0"), 0); gl.Uniform1i(Uniform("uStage1"), 1);
            gl.UseProgram(0);
            _worldProgram = program;
            program = 0;
        }
        finally
        {
            gl.DeleteShader(vs); gl.DeleteShader(fs);
            if (program != 0) gl.DeleteProgram(program);
        }
        int Uniform(string name)
        {
            int location = gl.GetUniformLocationString(program, name);
            return location >= 0 ? location : throw new InvalidOperationException("Missing backdrop uniform " + name);
        }
    }

    private void RetireWorldAssets(GlInterface? gl, EnbBackdropFrame? frame)
    {
        RetireStarTextures(gl, frame);
        // Tiny asset-owner inventory, independent of viewport and camera changes.
        foreach (var (geometry, buffers) in _worldBuffers.ToArray())
        {
            if (frame is not null && (ReferenceEquals(frame.Environment, geometry) ||
                frame.Environment.AdditiveLayers.Any(layer => ReferenceEquals(layer, geometry)) ||
                frame.Stars.Any(star => ReferenceEquals(star, geometry)))) continue;
            if (buffers.VertexArray != 0) gl?.DeleteVertexArray(buffers.VertexArray);
            gl?.DeleteBuffer(buffers.VertexBuffer);
            _worldBuffers.Remove(geometry);
        }
        foreach (var (source, id) in _worldTextures.ToArray())
        {
            if (_worldBuffers.Keys.Any(g => ReferenceEquals(g.Stage0, source) || ReferenceEquals(g.Stage1, source) || ReferenceEquals(g.ResolvedStage0, source))) continue;
            int texture = id;
            if (gl is not null) RequireProc(_deleteTextures, "glDeleteTextures")(1, ref texture);
            _worldTextures.Remove(source);
        }
    }

    private void DeinitializeWorld(GlInterface? gl)
    {
        RetireWorldAssets(gl, null);
        if (_worldProgram != 0) gl?.DeleteProgram(_worldProgram);
        _worldProgram = 0;
    }

    private sealed record WorldBuffers(int VertexBuffer, int VertexArray);
}
