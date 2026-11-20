using System.Runtime.InteropServices;


namespace EnbNext.Rendering.OpenGL;

/// <summary>
/// A viewport-local GPU scratch surface, seeded from the real destination and
/// copied back 1:1. It is not a keyed preview texture: destination-dependent
/// material blends see the world/chrome pixels and gamma runs once afterwards.
/// Local coordinates let the existing submission backends remain unchanged.
/// </summary>
public sealed class EnbOpenGlScannerFramebuffer
{
    private const int Framebuffer = 0x8D40, ReadFramebuffer = 0x8CA8, DrawFramebuffer = 0x8CA9;
    private const int Renderbuffer = 0x8D41, ColorAttachment = 0x8CE0, DepthAttachment = 0x8D00;
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Gen(int n, out int id);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Delete(int n, ref int id);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Bind(int target, int id);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Storage(int target, int format, int width, int height);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Attach(int target, int attachment, int rbTarget, int id);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Check(int target);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Scissor(int x,int y,int width,int height);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Blit(int x0, int y0, int x1, int y1, int dx0, int dy0, int dx1, int dy1, int mask, int filter);
    private int _framebuffer, _color, _depth, _width, _height;
    private int _capacityWidth, _capacityHeight, _format;
    private Blit? _blit;
    private Scissor? _scissor;
    public int StorageAllocationCount {get;private set;}

    public int Begin(EnbGlApi gl, int destination, int x, int y, int width, int height)
        => BeginCore(gl,destination,x,y,width,height,true);

    // A full scene clears its color itself; do not copy pixels that it replaces.
    public int BeginScene(EnbGlApi gl,int destination,int width,int height,bool highDynamicRange=false)
        => BeginCore(gl,destination,0,0,width,height,false,highDynamicRange);

    private int BeginCore(EnbGlApi gl, int destination, int x, int y, int width, int height,bool preserveColor,bool highDynamicRange=false)
    {
        _blit ??= Load<Blit>(gl, "glBlitFramebuffer");
        if (_framebuffer == 0)
        {
            Load<Gen>(gl, "glGenFramebuffers")(1, out _framebuffer);
            Load<Gen>(gl, "glGenRenderbuffers")(1, out _color);
            Load<Gen>(gl, "glGenRenderbuffers")(1, out _depth);
        }
        gl.BindFramebuffer(Framebuffer, _framebuffer);
        // Many UI controls share this scratch surface within one frame. Keep its
        // storage at a high-water size instead of reallocating it for each control.
        // The viewport and both blits still use the exact active rectangle.
        int format=highDynamicRange?0x881A:0x8058;
        if (_capacityWidth < width || _capacityHeight < height || _format!=format)
        {
            StorageAllocationCount++;
            _capacityWidth=Math.Max(_capacityWidth,width);
            _capacityHeight=Math.Max(_capacityHeight,height);
            var bind = Load<Bind>(gl, "glBindRenderbuffer");
            var storage = Load<Storage>(gl, "glRenderbufferStorage");
            var attach = Load<Attach>(gl, "glFramebufferRenderbuffer");
            bind(Renderbuffer, _color);
            storage(Renderbuffer, format, _capacityWidth, _capacityHeight); // RGBA16F or RGBA8
            attach(Framebuffer, ColorAttachment, Renderbuffer, _color);
            bind(Renderbuffer, _depth);
            storage(Renderbuffer, 0x81A6, _capacityWidth, _capacityHeight); // DEPTH_COMPONENT24
            attach(Framebuffer, DepthAttachment, Renderbuffer, _depth);
            bind(Renderbuffer, 0);
            if (Load<Check>(gl, "glCheckFramebufferStatus")(Framebuffer) != 0x8CD5)
                throw new InvalidOperationException("Scanner GPU framebuffer is incomplete.");
        }
        _format=format;
        _width = width; _height = height;
        gl.Disable(0x0C11); // scissor
        gl.BindFramebuffer(ReadFramebuffer, destination);
        gl.BindFramebuffer(DrawFramebuffer, _framebuffer);
        if(preserveColor)_blit(x, y, x + width, y + height, 0, 0, width, height, 0x4000, 0x2600);
        gl.BindFramebuffer(Framebuffer, _framebuffer);
        gl.Viewport(0, 0, width, height);
        gl.DepthMask(1);
        gl.ClearDepth(1);
        _scissor ??= Load<Scissor>(gl,"glScissor");
        gl.Enable(0x0C11);
        _scissor(0,0,width,height);
        gl.Clear(0x0100); // depth only; never clear destination color
        gl.Disable(0x0C11);
        return _framebuffer;
    }

    public void End(EnbGlApi gl, int destination, int x, int y, int bottomCrop=0)
    {
        gl.Disable(0x0C11);
        gl.BindFramebuffer(ReadFramebuffer, _framebuffer);
        gl.BindFramebuffer(DrawFramebuffer, destination);
        _blit!(0, bottomCrop, _width, _height, x, y+bottomCrop, x + _width, y + _height, 0x4000, 0x2600);
        gl.BindFramebuffer(Framebuffer, destination);
    }

    public int BeginPlaced(EnbGlApi gl,int destination,int x,int y,int width,int height,int viewportWidth,int viewportHeight,EnbNext.Presentation.EnbUiPlacement placement)
    {
        if(placement.IsIdentity)return Begin(gl,destination,x,y,width,height);
        int target=BeginCore(gl,destination,x,y,width,height,false);
        var r=placement.Apply(new EnbNext.Presentation.EnbUiRect(x,viewportHeight-y-height,width,height),new(viewportWidth,viewportHeight));
        gl.BindFramebuffer(ReadFramebuffer,destination);gl.BindFramebuffer(DrawFramebuffer,target);
        _blit!((int)Math.Round(r.Left),viewportHeight-(int)Math.Round(r.Bottom),(int)Math.Round(r.Right),viewportHeight-(int)Math.Round(r.Top),0,0,width,height,0x4000,0x2600);
        gl.BindFramebuffer(Framebuffer,target);return target;
    }

    // Composite an existing model surface through its enclosing UI placement.
    public void EndPlaced(EnbGlApi gl,int destination,int x,int y,int viewportWidth,int viewportHeight,EnbNext.Presentation.EnbUiPlacement placement)
    {
        if(placement.IsIdentity){End(gl,destination,x,y);return;}
        var r=placement.Apply(new EnbNext.Presentation.EnbUiRect(x,viewportHeight-y-_height,_width,_height),new(viewportWidth,viewportHeight));
        gl.Disable(0x0C11);gl.BindFramebuffer(ReadFramebuffer,_framebuffer);gl.BindFramebuffer(DrawFramebuffer,destination);
        _blit!(0,0,_width,_height,(int)Math.Round(r.Left),viewportHeight-(int)Math.Round(r.Bottom),(int)Math.Round(r.Right),viewportHeight-(int)Math.Round(r.Top),0x4000,0x2600);
        gl.BindFramebuffer(Framebuffer,destination);
    }

    public void Deinitialize(EnbGlApi? gl)
    {
        if (gl is not null && _color != 0) Load<Delete>(gl, "glDeleteRenderbuffers")(1, ref _color);
        if (gl is not null && _depth != 0) Load<Delete>(gl, "glDeleteRenderbuffers")(1, ref _depth);
        if (gl is not null && _framebuffer != 0) Load<Delete>(gl, "glDeleteFramebuffers")(1, ref _framebuffer);
        _framebuffer = _color = _depth = _width = _height = 0;
        _capacityWidth = _capacityHeight = _format = 0;
        _blit = null;
        _scissor = null;
    }

    private static T Load<T>(EnbGlApi gl, string name) where T : Delegate
    {
        IntPtr p = gl.GetProcAddress(name);
        if (p == IntPtr.Zero) throw new NotSupportedException($"Scanner GPU composition requires {name}.");
        return Marshal.GetDelegateForFunctionPointer<T>(p);
    }
}
