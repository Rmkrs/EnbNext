using System.Runtime.InteropServices;
using System.Numerics;

namespace EnbNext.Rendering.OpenGL;

/// <summary>A color-only GPU capture target. Sampling borrows this texture;
/// destroying a mesh never destroys the target. All calls use its owning GL context.</summary>
public sealed class EnbOpenGlRenderTexture
{
    /// <summary>Convert D3D8 integer pixel centers to GL half-integer centers.
    /// Applied only to capture rasterization, never the authored texture mapper.
    /// Retail 00945440 applies the inset viewport without an XY projection offset.</summary>
    public static Matrix4x4 RetailCaptureRasterTranslation(int viewportWidth, int viewportHeight)
    {
        if (viewportWidth <= 0) throw new ArgumentOutOfRangeException(nameof(viewportWidth));
        if (viewportHeight <= 0) throw new ArgumentOutOfRangeException(nameof(viewportHeight));
        // +.5 screen X and -.5 bottom-up screen Y equal +.5 in both
        // top-down D3D coordinates. Multiplication also handles clip W.
        return Matrix4x4.CreateTranslation(1f / viewportWidth, -1f / viewportHeight, 0);
    }

    public int Texture { get; private set; }
    public int Framebuffer { get; private set; }
    public int Size { get; }
    private readonly GlInterface _gl;
    private EnbOpenGlRenderTexture(GlInterface gl, int size) { _gl = gl; Size = size; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Gen(int n, out int value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Delete(int n, ref int value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Bind(int target, int value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Parameter(int target, int name, int value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Image(int target, int level, int format, int width, int height, int border, int inputFormat, int type, IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Attach(int target, int attachment, int textureTarget, int texture, int level);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Check(int target);

    public static bool TryCreate(GlInterface gl, int size, int internalFormat, int pixelFormat, int filter,
        out EnbOpenGlRenderTexture? target, out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(gl);
        if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size));
        var result = new EnbOpenGlRenderTexture(gl, size);
        target = null;
        diagnostic = null;
        try
        {
            Load<Gen>(gl, "glGenTextures")(1, out int texture);
            result.Texture = texture;
            Load<Bind>(gl, "glBindTexture")(0x0DE1, texture);
            var parameter = Load<Parameter>(gl, "glTexParameteri");
            parameter(0x0DE1, 0x2801, filter); parameter(0x0DE1, 0x2800, filter);
            parameter(0x0DE1, 0x2802, 0x812F); parameter(0x0DE1, 0x2803, 0x812F);
            Load<Image>(gl, "glTexImage2D")(0x0DE1, 0, internalFormat, size, size, 0, pixelFormat, 0x1401, IntPtr.Zero);
            Load<Gen>(gl, "glGenFramebuffers")(1, out int framebuffer);
            result.Framebuffer = framebuffer;
            gl.BindFramebuffer(0x8D40, framebuffer);
            Load<Attach>(gl, "glFramebufferTexture2D")(0x8D40, 0x8CE0, 0x0DE1, texture, 0);
            int status = Load<Check>(gl, "glCheckFramebufferStatus")(0x8D40);
            int error = gl.GetError();
            if (status != 0x8CD5 || error != 0)
            {
                diagnostic = $"Framebuffer 0x{status:X}, GL error 0x{error:X}, internal format 0x{internalFormat:X}.";
                return false;
            }
            target = result;
            return true;
        }
        finally
        {
            if (target is null) result.Destroy(gl);
        }
    }

    public void Clear(GlInterface gl, float red, float green, float blue, float alpha)
    {
        Validate(gl);
        gl.BindFramebuffer(0x8D40, Framebuffer);
        gl.Disable(0x0C11);
        gl.Viewport(0, 0, Size, Size);
        gl.ClearColor(red, green, blue, alpha);
        gl.Clear(GlConsts.GL_COLOR_BUFFER_BIT);
    }

    public void Validate(GlInterface gl)
    {
        if (!ReferenceEquals(gl, _gl)) throw new InvalidOperationException("Render texture belongs to another GL context.");
        if (Texture == 0 || Framebuffer == 0) throw new ObjectDisposedException(nameof(EnbOpenGlRenderTexture));
    }

    public void Destroy(GlInterface? gl)
    {
        if (gl is not null && !ReferenceEquals(gl, _gl)) throw new InvalidOperationException("Render texture belongs to another GL context.");
        int texture = Texture, framebuffer = Framebuffer;
        if (gl is not null && framebuffer != 0) Load<Delete>(gl, "glDeleteFramebuffers")(1, ref framebuffer);
        if (gl is not null && texture != 0) Load<Delete>(gl, "glDeleteTextures")(1, ref texture);
        Texture = Framebuffer = 0;
    }

    private static T Load<T>(GlInterface gl, string name) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(gl.GetProcAddress(name));
}
