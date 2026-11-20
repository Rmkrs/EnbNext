using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;
using EnbNext.Rendering;

namespace EnbNext.Rendering.OpenGL;

/// <summary>
/// OpenGL index representation selected for one indexed submission plan.
/// Current retail W3D indices are backend-neutral u32 values; chooses the
/// smallest exact GL element representation without truncation.
/// </summary>
public enum EnbOpenGlIndexElementType : byte
{
    UnsignedShort = 0,
    UnsignedInt = 1,
}

/// <summary>
/// One run translated only as far as OpenGL DrawElements needs. The source
/// run remains attached so later material/state slices can consume the already-
/// resolved binding without reconstructing triangle ownership in the backend.
/// </summary>
public readonly record struct EnbOpenGlIndexedDrawCommand(
    EnbW3dIndexedSubmissionRun SourceRun,
    int IndexByteOffset)
{
    public int IndexCount => SourceRun.IndexCount;
    public int PrimitiveCount => SourceRun.PrimitiveCount;
}

/// <summary>
/// Pure CPU-side upload plan. It is intentionally testable without a GL context;
/// all API calls remain in EnbOpenGlIndexedSubmissionBackend and therefore can
/// only occur while the host owns a current OpenGL context.
/// </summary>
public sealed record EnbOpenGlIndexedUploadPlan(
    ImmutableArray<float> PositionElements,
    ImmutableArray<ushort> UnsignedShortIndices,
    ImmutableArray<uint> UnsignedIntIndices,
    EnbOpenGlIndexElementType IndexElementType,
    int IndexElementSizeBytes,
    EnbW3dSubmissionVertexSource VertexSource,
    ImmutableArray<EnbOpenGlIndexedDrawCommand> DrawCommands)
{
    private ImmutableArray<float> _positionElements=PositionElements;
    private ImmutableArray<Vector3> _positions;
    internal EnbW3dFrameStream<Vector3>? FramePositions {get;init;}
    internal ImmutableArray<uint> SourceImmutableIndices { get; init; }
    internal ImmutableArray<EnbW3dIndexedSubmissionRun> SourceRuns { get; init; }
    internal ImmutableArray<EnbOpenGlIndexedDrawCommand> ValidatedCommands { get; init; }
    internal ImmutableArray<ushort> ValidatedIndices16 { get; init; }
    internal ImmutableArray<uint> ValidatedIndices32 { get; init; }
    internal bool HasImmutablePositions(ImmutableArray<Vector3> positions)=>
        !_positions.IsDefault && _positions.Equals(positions);
    public ImmutableArray<float> PositionElements
    {
        get
        {
            if(_positionElements.IsDefault)
            {
                var elements=new float[VertexCount*3];
                for(int i=0;i<VertexCount;i++)
                {var p=PositionAt(i);elements[i*3]=p.X;elements[i*3+1]=p.Y;elements[i*3+2]=p.Z;}
                ImmutableInterlocked.InterlockedInitialize(ref _positionElements,ImmutableCollectionsMarshal.AsImmutableArray(elements));
            }
            return _positionElements;
        }
        init { _positionElements=value;_positions=default;FramePositions=null; }
    }
    internal EnbOpenGlIndexedUploadPlan(ImmutableArray<Vector3> positions,
        ImmutableArray<ushort> indices16,ImmutableArray<uint> indices32,
        EnbOpenGlIndexElementType type,int size,EnbW3dSubmissionVertexSource source,
        ImmutableArray<EnbOpenGlIndexedDrawCommand> commands)
        :this(default(ImmutableArray<float>),indices16,indices32,type,size,source,commands)
        =>_positions=positions;
    // The lit path interleaves directly from the immutable Vector3 snapshot.
    // Only the position-only backend needs a second flattened representation.
    internal ReadOnlySpan<Vector3> PositionSpan => FramePositions is {} frame?frame.ReadSpan:_positions.AsSpan();
    internal Vector3 PositionAt(int index)=>FramePositions is {} frame?frame[index]:!_positions.IsDefault?_positions[index]:
        new(_positionElements[index*3],_positionElements[index*3+1],_positionElements[index*3+2]);
    public int VertexCount => FramePositions?.Count??(_positions.IsDefault?_positionElements.Length/3:_positions.Length);
    public int IndexCount => IndexElementType == EnbOpenGlIndexElementType.UnsignedShort
        ? UnsignedShortIndices.Length
        : UnsignedIntIndices.Length;
}

/// <summary>
/// Exact GL object ownership for one uploaded plan. The owning backend must
/// destroy this resource while the same host-owned GL context is current.
/// </summary>
public sealed record EnbOpenGlIndexedMeshResource(
    int VertexBuffer,
    int IndexBuffer,
    int VertexArray,
    EnbOpenGlIndexedUploadPlan UploadPlan);

public readonly record struct EnbOpenGlContextIdentity(
    GlProfileType Profile,
    int MajorVersion,
    int MinorVersion,
    string Vendor,
    string Renderer,
    string VersionString);

public readonly record struct EnbOpenGlSubmissionReceipt(
    int DrawCallCount,
    int PrimitiveCount,
    int IndexCount,
    int GlErrorCode) { public int VertexCount { get; init; } }

/// <summary>
/// Pure-to-OpenGL upload planning. It knows no W3D file format, material
/// parser, camera, LOD or gameplay state. Those are already resolved upstream.
/// </summary>
public static class EnbOpenGlIndexedUploadPlanner
{
    public static bool TryPlan(
        EnbW3dIndexedSubmissionPlan submission,
        bool supportsUnsignedIntIndices,
        out EnbOpenGlIndexedUploadPlan uploadPlan,
        out string? diagnostic,
        EnbOpenGlIndexedUploadPlan? retained = null)
    {
        ArgumentNullException.ThrowIfNull(submission.Packet);
        uploadPlan = default!;
        diagnostic = null;

        if (submission.PrimitiveTopology != EnbW3dIndexedPrimitiveTopology.TriangleList)
        {
            diagnostic = $"OpenGL R6B only accepts the canonical R6A triangle-list topology, not {submission.PrimitiveTopology}.";
            return false;
        }

        IReadOnlyList<Vector3> positions = submission.Positions;
        bool retainedPositions=positions is ImmutableArray<Vector3> immutablePositions &&
            retained?.HasImmutablePositions(immutablePositions)==true;
        var framePositions=positions is EnbW3dFrameStream<Vector3> borrowed?borrowed.ReadSpan:default;
        for (int i = 0; !retainedPositions && i < positions.Count; i++)
        {
            Vector3 position = !framePositions.IsEmpty?framePositions[i]:positions[i];
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
            {
                diagnostic = $"Submission vertex {i} contains a non-finite position.";
                return false;
            }

        }

        IReadOnlyList<uint> indices = submission.Indices;
        EnbOpenGlIndexElementType indexElementType;
        int indexElementSizeBytes;
        ImmutableArray<ushort> indices16;
        ImmutableArray<uint> indices32;
        if (retained is not null && SameIndices(retained,indices) &&
            (retained.IndexElementType != EnbOpenGlIndexElementType.UnsignedInt || supportsUnsignedIntIndices))
        {
            indexElementType=retained.IndexElementType;indexElementSizeBytes=retained.IndexElementSizeBytes;
            indices16=retained.UnsignedShortIndices;indices32=retained.UnsignedIntIndices;
        }
        else
        {
        uint maximumIndex = 0;
        for (int i = 0; i < indices.Count; i++)
            maximumIndex = Math.Max(maximumIndex, indices[i]);

        if (maximumIndex <= ushort.MaxValue)
        {
            indexElementType = EnbOpenGlIndexElementType.UnsignedShort;
            indexElementSizeBytes = sizeof(ushort);
            var builder = ImmutableArray.CreateBuilder<ushort>(indices.Count);
            for (int i = 0; i < indices.Count; i++)
                builder.Add((ushort)indices[i]);
            indices16 = builder.MoveToImmutable();
            indices32 = ImmutableArray<uint>.Empty;
        }
        else if (supportsUnsignedIntIndices)
        {
            indexElementType = EnbOpenGlIndexElementType.UnsignedInt;
            indexElementSizeBytes = sizeof(uint);
            indices16 = ImmutableArray<ushort>.Empty;
            indices32 = ImmutableArray.CreateRange(indices);
        }
        else
        {
            diagnostic = $"Submission requires vertex index {maximumIndex}, but this OpenGL context does not support unsigned-int element indices.";
            return false;
        }

        }

        ImmutableArray<EnbOpenGlIndexedDrawCommand> commands;
        if(retained is not null && retained.IndexElementType==indexElementType &&
            retained.IndexElementSizeBytes==indexElementSizeBytes && retained.IndexCount==indices.Count &&
            retained.SourceRuns.Equals(submission.Runs) &&
            retained.DrawCommands.Equals(retained.ValidatedCommands))
        {
            // The prepared submission owns these immutable ranges. A changed
            // vertex pose does not change draw offsets or material run identity.
            commands=retained.DrawCommands;
        }
        else
        {
        var drawCommands = ImmutableArray.CreateBuilder<EnbOpenGlIndexedDrawCommand>(submission.Runs.Length);
        foreach (EnbW3dIndexedSubmissionRun run in submission.Runs)
        {
            if (run.StartIndex < 0 || run.IndexCount < 0 || run.StartIndex > indices.Count - run.IndexCount)
            {
                diagnostic = $"R6A run starting at {run.StartIndex} with {run.IndexCount} indices is outside the {indices.Count}-index stream.";
                return false;
            }

            int byteOffset = checked(run.StartIndex * indexElementSizeBytes);
            drawCommands.Add(new EnbOpenGlIndexedDrawCommand(run, byteOffset));
        }

        commands=drawCommands.MoveToImmutable();
        }

        uploadPlan = new EnbOpenGlIndexedUploadPlan(
            positions is EnbW3dFrameStream<Vector3>?default:positions.ToImmutableArray(),
            indices16,
            indices32,
            indexElementType,
            indexElementSizeBytes,
            submission.VertexSource,
            commands)
        { FramePositions=positions as EnbW3dFrameStream<Vector3>,SourceRuns=submission.Runs,ValidatedCommands=commands,
          SourceImmutableIndices=indices is ImmutableArray<uint> immutableIndices?immutableIndices:default,
          ValidatedIndices16=indices16,ValidatedIndices32=indices32 };
        return true;
    }
    private static bool SameIndices(EnbOpenGlIndexedUploadPlan retained,IReadOnlyList<uint> requested)
    {
        // Compare with the owned encoded snapshot, never with an alias to a
        // caller's array. Even an in-place source replacement must invalidate.
        if(retained.IndexCount!=requested.Count)return false;
        if(!retained.SourceImmutableIndices.IsDefault && requested is ImmutableArray<uint> immutable &&
            retained.SourceImmutableIndices.Equals(immutable) &&
            retained.ValidatedIndices16.Equals(retained.UnsignedShortIndices) &&
            retained.ValidatedIndices32.Equals(retained.UnsignedIntIndices))return true;
        if(retained.IndexElementType==EnbOpenGlIndexElementType.UnsignedShort)
        {
            var values=retained.UnsignedShortIndices.AsSpan();
            for(int i=0;i<values.Length;i++)if(values[i]!=requested[i])return false;
        }
        else
        {
            var values=retained.UnsignedIntIndices.AsSpan();
            if(requested is uint[] array)return values.SequenceEqual(array);
            for(int i=0;i<values.Length;i++)if(values[i]!=requested[i])return false;
        }
        return true;
    }

}

/// <summary>
/// First concrete EnbNext GPU backend. the host owns creation/destruction of the
/// native window surface and GL context; this class owns only EnbNext-created GL
/// programs, vertex/index buffers and optional VAOs. Every method taking a
/// GlInterface must be called from OpenGlControlBase's init/render/deinit
/// callbacks while the host guarantees that context is current.
///
/// Deliberately uses a diagnostic single-color shader. Camera composition,
/// Material/texture/light/fog realization and production resource caching are
/// later slices. The indexed geometry and draw-range path itself is real GPU work.
/// </summary>
public sealed class EnbOpenGlIndexedSubmissionBackend
{
    private const int GlUnsignedInt = 0x1405;
    private const int GlStreamDraw = 0x88E0;
    private const int PositionAttributeIndex = 0;

    private int _program;
    private int _clipFromModelUniform = -1;

    public bool IsInitialized => _program != 0;
    public EnbOpenGlContextIdentity? ContextIdentity { get; private set; }

    public void Initialize(GlInterface gl)
    {
        ArgumentNullException.ThrowIfNull(gl);
        if (IsInitialized)
            throw new InvalidOperationException("The OpenGL backend is already initialized.");

        GlVersion version = gl.ContextInfo.Version;
        if (!IsSupportedShaderContext(version))
            throw new NotSupportedException($"R6B requires desktop OpenGL 2.0+ or OpenGL ES 2.0+; the host supplied {version}.");

        (string vertexSource, string fragmentSource) = CreateDiagnosticShaderSources(version);
        int vertexShader = gl.CreateShader(GlConsts.GL_VERTEX_SHADER);
        int fragmentShader = gl.CreateShader(GlConsts.GL_FRAGMENT_SHADER);
        int program = 0;
        try
        {
            string? vertexError = gl.CompileShaderAndGetError(vertexShader, vertexSource);
            if (!string.IsNullOrWhiteSpace(vertexError))
                throw new InvalidOperationException($"R6B OpenGL vertex shader compilation failed: {vertexError}");

            string? fragmentError = gl.CompileShaderAndGetError(fragmentShader, fragmentSource);
            if (!string.IsNullOrWhiteSpace(fragmentError))
                throw new InvalidOperationException($"R6B OpenGL fragment shader compilation failed: {fragmentError}");

            program = gl.CreateProgram();
            gl.AttachShader(program, vertexShader);
            gl.AttachShader(program, fragmentShader);
            gl.BindAttribLocationString(program, PositionAttributeIndex, "aPosition");
            string? linkError = gl.LinkProgramAndGetError(program);
            if (!string.IsNullOrWhiteSpace(linkError))
                throw new InvalidOperationException($"R6B OpenGL diagnostic program link failed: {linkError}");

            int clipFromModelUniform = gl.GetUniformLocationString(program, "uClipFromModel");
            if (clipFromModelUniform < 0)
                throw new InvalidOperationException("R6B OpenGL diagnostic program did not expose uClipFromModel.");

            _program = program;
            _clipFromModelUniform = clipFromModelUniform;
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
        EnbW3dIndexedSubmissionPlan submission,
        out EnbOpenGlIndexedMeshResource resource,
        out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(gl);
        ArgumentNullException.ThrowIfNull(submission.Packet);
        EnsureInitialized();
        resource = default!;
        diagnostic = null;

        bool supportsUnsignedIntIndices = SupportsUnsignedIntIndices(gl.ContextInfo.Version, gl.ContextInfo.Extensions);
        if (!EnbOpenGlIndexedUploadPlanner.TryPlan(
                submission,
                supportsUnsignedIntIndices,
                out EnbOpenGlIndexedUploadPlan uploadPlan,
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
            if (RequiresVertexArray(gl.ContextInfo.Version) && !useVertexArray)
            {
                diagnostic = $"OpenGL context {gl.ContextInfo.Version} requires a vertex array object for the R6B core-profile draw path, but the host did not expose VAO support.";
                return false;
            }

            if (useVertexArray)
            {
                vertexArray = gl.GenVertexArray();
                gl.BindVertexArray(vertexArray);
            }

            vertexBuffer = gl.GenBuffer();
            gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, vertexBuffer);
            UploadArray(gl, GlConsts.GL_ARRAY_BUFFER, uploadPlan.PositionElements.AsSpan(), uploadPlan.VertexSource == EnbW3dSubmissionVertexSource.CpuDeformed ? GlStreamDraw : GlConsts.GL_STATIC_DRAW);
            gl.VertexAttribPointer(
                PositionAttributeIndex,
                3,
                GlConsts.GL_FLOAT,
                normalized: 0,
                stride: sizeof(float) * 3,
                IntPtr.Zero);
            gl.EnableVertexAttribArray(PositionAttributeIndex);

            indexBuffer = gl.GenBuffer();
            gl.BindBuffer(GlConsts.GL_ELEMENT_ARRAY_BUFFER, indexBuffer);
            if (uploadPlan.IndexElementType == EnbOpenGlIndexElementType.UnsignedShort)
            {
                UploadArray(gl, GlConsts.GL_ELEMENT_ARRAY_BUFFER, uploadPlan.UnsignedShortIndices.AsSpan(), GlConsts.GL_STATIC_DRAW);
            }
            else
            {
                UploadArray(gl, GlConsts.GL_ELEMENT_ARRAY_BUFFER, uploadPlan.UnsignedIntIndices.AsSpan(), GlConsts.GL_STATIC_DRAW);
            }

            if (useVertexArray)
                gl.BindVertexArray(0);
            gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, 0);
            if (!useVertexArray)
                gl.BindBuffer(GlConsts.GL_ELEMENT_ARRAY_BUFFER, 0);

            int error = gl.GetError();
            if (error != GlConsts.GL_NO_ERROR)
            {
                diagnostic = $"OpenGL resource creation ended with GL error 0x{error:X4}.";
                return false;
            }

            resource = new EnbOpenGlIndexedMeshResource(
                vertexBuffer,
                indexBuffer,
                vertexArray,
                uploadPlan);
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
        EnbOpenGlIndexedMeshResource resource,
        Matrix4x4 clipFromModel,
        bool clearColorBuffer = true)
    {
        ArgumentNullException.ThrowIfNull(gl);
        ArgumentNullException.ThrowIfNull(resource);
        EnsureInitialized();

        if (pixelWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(pixelWidth));
        if (pixelHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(pixelHeight));
        if (!IsFinite(clipFromModel))
            throw new ArgumentOutOfRangeException(nameof(clipFromModel), "Clip-from-model transform must be finite.");

        DrainErrors(gl);
        gl.BindFramebuffer(GlConsts.GL_FRAMEBUFFER, framebuffer);
        gl.Viewport(0, 0, pixelWidth, pixelHeight);
        if (clearColorBuffer)
        {
            gl.ClearColor(0.012f, 0.028f, 0.050f, 1f);
            gl.Clear(GlConsts.GL_COLOR_BUFFER_BIT);
        }

        gl.UseProgram(_program);
        UploadSystemNumericsMatrix(gl, _clipFromModelUniform, clipFromModel);

        if (resource.VertexArray != 0)
        {
            gl.BindVertexArray(resource.VertexArray);
        }
        else
        {
            gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, resource.VertexBuffer);
            gl.BindBuffer(GlConsts.GL_ELEMENT_ARRAY_BUFFER, resource.IndexBuffer);
            gl.VertexAttribPointer(
                PositionAttributeIndex,
                3,
                GlConsts.GL_FLOAT,
                normalized: 0,
                stride: sizeof(float) * 3,
                IntPtr.Zero);
            gl.EnableVertexAttribArray(PositionAttributeIndex);
        }

        int glIndexType = resource.UploadPlan.IndexElementType == EnbOpenGlIndexElementType.UnsignedShort
            ? GlConsts.GL_UNSIGNED_SHORT
            : GlUnsignedInt;
        int drawCalls = 0;
        int primitiveCount = 0; int vertexCount = 0;
        int indexCount = 0;
        foreach (EnbOpenGlIndexedDrawCommand command in resource.UploadPlan.DrawCommands)
        {
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

    public void DestroyResource(GlInterface gl, EnbOpenGlIndexedMeshResource resource)
    {
        ArgumentNullException.ThrowIfNull(gl);
        ArgumentNullException.ThrowIfNull(resource);
        if (resource.VertexArray != 0 && gl.IsDeleteVertexArraysAvailable)
            gl.DeleteVertexArray(resource.VertexArray);
        if (resource.IndexBuffer != 0)
            gl.DeleteBuffer(resource.IndexBuffer);
        if (resource.VertexBuffer != 0)
            gl.DeleteBuffer(resource.VertexBuffer);
    }

    public void Deinitialize(GlInterface gl)
    {
        ArgumentNullException.ThrowIfNull(gl);
        if (_program != 0)
            gl.DeleteProgram(_program);
        _program = 0;
        _clipFromModelUniform = -1;
        ContextIdentity = null;
    }

    public static bool SupportsUnsignedIntIndices(GlVersion version, IReadOnlySet<string> extensions)
    {
        ArgumentNullException.ThrowIfNull(extensions);
        if (version.Type == GlProfileType.OpenGL)
            return version.Major >= 2;
        if (version.Type == GlProfileType.OpenGLES)
            return version.Major >= 3 || extensions.Contains("GL_OES_element_index_uint");
        return false;
    }

    public static bool RequiresVertexArray(GlVersion version)
        => version.Type == GlProfileType.OpenGL &&
           !version.IsCompatibilityProfile &&
           version.Major >= 3;

    public static (string VertexShader, string FragmentShader) CreateDiagnosticShaderSources(GlVersion version)
    {
        if (!IsSupportedShaderContext(version))
            throw new NotSupportedException($"Unsupported OpenGL shader context {version}.");

        if (version.Type == GlProfileType.OpenGLES)
        {
            if (version.Major >= 3)
            {
                return (
                    "#version 300 es\nprecision highp float;\nin vec3 aPosition;\nuniform mat4 uClipFromModel;\nvoid main(){ gl_Position = uClipFromModel * vec4(aPosition, 1.0); }\n",
                    "#version 300 es\nprecision mediump float;\nout vec4 fragColor;\nvoid main(){ fragColor = vec4(0.25, 0.82, 1.0, 1.0); }\n");
            }

            return (
                "precision highp float;\nattribute vec3 aPosition;\nuniform mat4 uClipFromModel;\nvoid main(){ gl_Position = uClipFromModel * vec4(aPosition, 1.0); }\n",
                "precision mediump float;\nvoid main(){ gl_FragColor = vec4(0.25, 0.82, 1.0, 1.0); }\n");
        }

        if (version.Major > 3 || (version.Major == 3 && version.Minor >= 2))
        {
            return (
                "#version 150\nin vec3 aPosition;\nuniform mat4 uClipFromModel;\nvoid main(){ gl_Position = uClipFromModel * vec4(aPosition, 1.0); }\n",
                "#version 150\nout vec4 fragColor;\nvoid main(){ fragColor = vec4(0.25, 0.82, 1.0, 1.0); }\n");
        }

        string legacyVersion = version.Major == 2 && version.Minor == 0 ? "110" : "120";
        return (
            $"#version {legacyVersion}\nattribute vec3 aPosition;\nuniform mat4 uClipFromModel;\nvoid main(){{ gl_Position = uClipFromModel * vec4(aPosition, 1.0); }}\n",
            $"#version {legacyVersion}\nvoid main(){{ gl_FragColor = vec4(0.25, 0.82, 1.0, 1.0); }}\n");
    }

    private static bool IsSupportedShaderContext(GlVersion version)
        => (version.Type == GlProfileType.OpenGL || version.Type == GlProfileType.OpenGLES) &&
           version.Major >= 2;

    public static ImmutableArray<float> CreateGlUniformMatrixElements(Matrix4x4 matrix)
    {
        if (!IsFinite(matrix))
            throw new ArgumentOutOfRangeException(nameof(matrix), "OpenGL transform must be finite.");

        // System.Numerics evaluates row vectors (v * M). GLSL evaluates column
        // vectors (M * v). Supplying the System.Numerics row-major field order
        // as an OpenGL column-major array therefore presents transpose(M), which
        // is the exact equivalent transform, without using GL's unsupported-on-
        // GLES transpose flag.
        return ImmutableArray.Create(
            matrix.M11, matrix.M12, matrix.M13, matrix.M14,
            matrix.M21, matrix.M22, matrix.M23, matrix.M24,
            matrix.M31, matrix.M32, matrix.M33, matrix.M34,
            matrix.M41, matrix.M42, matrix.M43, matrix.M44);
    }

    private static unsafe void UploadSystemNumericsMatrix(GlInterface gl, int location, Matrix4x4 matrix)
    {
        ImmutableArray<float> immutableElements = CreateGlUniformMatrixElements(matrix);
        float[] elements = immutableElements.ToArray();
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
            throw new InvalidOperationException("The OpenGL backend has not been initialized on a current host GL context.");
    }

    private static bool IsFinite(Matrix4x4 value)
        => float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13) && float.IsFinite(value.M14) &&
           float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M23) && float.IsFinite(value.M24) &&
           float.IsFinite(value.M31) && float.IsFinite(value.M32) && float.IsFinite(value.M33) && float.IsFinite(value.M34) &&
           float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43) && float.IsFinite(value.M44);
}
