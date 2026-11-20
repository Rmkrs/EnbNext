using System.Runtime.InteropServices;


namespace EnbNext.Rendering.OpenGL;

/// <summary>Retained GPU image of a map planet. Capture, material blending and
/// composition stay on the GPU; map motion never changes its camera or time.</summary>
public sealed class EnbOpenGlMapPlanetImage
{
    // Backing resolution is a presentation-quality choice, independent of map zoom.
    public const int Size=512;
    public int Texture { get; private set; }
    public int WhiteTexture => _white;
    private int _white,_framebuffer,_depth;
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Gen(int n,out int value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Delete(int n,ref int value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Bind(int target,int value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Parameter(int target,int name,int value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Image(int target,int level,int format,int width,int height,int border,int inputFormat,int type,IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void AttachTexture(int target,int attachment,int textureTarget,int texture,int level);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Storage(int target,int format,int width,int height);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void AttachDepth(int target,int attachment,int renderbufferTarget,int renderbuffer);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Check(int target);

    public int Begin(EnbGlApi gl,bool white=false)
    {
        if(_framebuffer==0)
        {
            Load<Gen>(gl,"glGenFramebuffers")(1,out _framebuffer);
            _white=CreateTexture(gl,0x2601); Texture=CreateTexture(gl,0x2601);
            Load<Gen>(gl,"glGenRenderbuffers")(1,out _depth);
            Load<Bind>(gl,"glBindRenderbuffer")(0x8D41,_depth);
            Load<Storage>(gl,"glRenderbufferStorage")(0x8D41,0x81A6,Size,Size);
            gl.BindFramebuffer(EnbGlConstants.GL_FRAMEBUFFER,_framebuffer);
            Load<AttachDepth>(gl,"glFramebufferRenderbuffer")(0x8D40,0x8D00,0x8D41,_depth);
        }
        gl.BindFramebuffer(EnbGlConstants.GL_FRAMEBUFFER,_framebuffer);
        Load<AttachTexture>(gl,"glFramebufferTexture2D")(0x8D40,0x8CE0,0x0DE1,white ? _white : Texture,0);
        if(Load<Check>(gl,"glCheckFramebufferStatus")(0x8D40)!=0x8CD5) throw new InvalidOperationException("Map planet framebuffer is incomplete.");
        gl.Disable(0x0C11); gl.Viewport(0,0,Size,Size); gl.DepthMask(1);
        float background=white ? 1 : 0;
        gl.ClearColor(background,background,background,1); gl.ClearDepth(1); gl.Clear(0x4100);
        return _framebuffer;
    }

    public void Deinitialize(EnbGlApi? gl)
    {
        if (gl is not null && _white!=0) Load<Delete>(gl,"glDeleteTextures")(1,ref _white);
        int texture=Texture;
        if (gl is not null && texture!=0) Load<Delete>(gl,"glDeleteTextures")(1,ref texture);
        if (gl is not null && _depth!=0) Load<Delete>(gl,"glDeleteRenderbuffers")(1,ref _depth);
        if (gl is not null && _framebuffer!=0) Load<Delete>(gl,"glDeleteFramebuffers")(1,ref _framebuffer);
        _white=_depth=_framebuffer=0; Texture=0;
    }

    private static int CreateTexture(EnbGlApi gl,int filter)
    {
        Load<Gen>(gl,"glGenTextures")(1,out int texture);
        Load<Bind>(gl,"glBindTexture")(0x0DE1,texture);
        var parameter=Load<Parameter>(gl,"glTexParameteri");
        parameter(0x0DE1,0x2801,filter); parameter(0x0DE1,0x2800,filter);
        parameter(0x0DE1,0x2802,0x812F); parameter(0x0DE1,0x2803,0x812F);
        // Floating attachments preserve additive terms when capturing against white.
        Load<Image>(gl,"glTexImage2D")(0x0DE1,0,0x881A,Size,Size,0,0x1908,0x1406,IntPtr.Zero);
        return texture;
    }
    private static T Load<T>(EnbGlApi gl,string name) where T:Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(gl.GetProcAddress(name));
}
