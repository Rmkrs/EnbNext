namespace EnbNext.Rendering.OpenGL;

/// <summary>Owns a doubled scene raster target; cameras and post-processing stay at output resolution.</summary>
public sealed unsafe class EnbOpenGlSceneSupersampling
{
    private readonly EnbOpenGlScannerFramebuffer target=new();
    private int destination,width,height,framebuffer;
    private (int Width,int Height,bool Hdr) request;
    private bool active;
    private delegate* unmanaged<int,int,int,int,int,int,int,int,int,int,void> blit;
    public string? Failure {get;private set;}
    public bool IsActive => active;
    public int Begin(EnbGlApi gl,int nativeFramebuffer,int w,int h,bool enabled,out string? failure)
    {
        failure=null;
        if(!enabled){Deinitialize(gl);return nativeFramebuffer;}
        var next=(w,h,gl.HighDynamicRange);
        if(next!=request){Deinitialize(gl);request=next;}
        if(Failure is not null){failure=Failure;return nativeFramebuffer;}
        destination=nativeFramebuffer;width=w;height=h;
        try
        {
            if(framebuffer==0)
            {
                var get=(delegate* unmanaged<int,int*,void>)gl.GetProcAddress("glGetIntegerv");
                if(get==null)throw new NotSupportedException("Framebuffer limits unavailable.");
                int max=0;int* viewport=stackalloc int[2];get(0x84E8,&max);get(0x0D3A,viewport);
                if(w<=0||h<=0||w>max/2||h>max/2||w>viewport[0]/2||h>viewport[1]/2)
                    throw new NotSupportedException("Doubled scene exceeds graphics device dimensions.");
                blit=(delegate* unmanaged<int,int,int,int,int,int,int,int,int,int,void>)gl.GetProcAddress("glBlitFramebuffer");
                if(blit==null)throw new NotSupportedException("Scene reduction unavailable.");
            }
            bool allocating=framebuffer==0;
            framebuffer=target.BeginScene(gl,nativeFramebuffer,w*2,h*2,gl.HighDynamicRange);
            if(allocating && gl.GetError()!=0)throw new InvalidOperationException("Doubled scene storage allocation failed.");
            gl.SupersamplingFramebuffer=framebuffer;active=true;return framebuffer;
        }
        catch(Exception ex) when(ex is InvalidOperationException or NotSupportedException)
        {
            Deinitialize(gl);request=next;Failure=failure="Supersampling unavailable: "+ex.Message;
            for(int i=0;i<8 && gl.GetError()!=0;i++){}gl.BindFramebuffer(0x8D40,nativeFramebuffer);gl.Viewport(0,0,w,h);return nativeFramebuffer;
        }
    }
    public int Complete(EnbGlApi gl,int currentFramebuffer)
    {
        if(!active)return currentFramebuffer;
        active=false;gl.SupersamplingFramebuffer=0;gl.Disable(0x0C11);
        gl.BindFramebuffer(0x8CA8,framebuffer);gl.BindFramebuffer(0x8CA9,destination);
        blit(0,0,width*2,height*2,0,0,width,height,0x4000,0x2601);
        blit(0,0,width*2,height*2,0,0,width,height,0x0100,0x2600);
        gl.BindFramebuffer(0x8D40,destination);gl.Viewport(0,0,width,height);return destination;
    }
    public void Deinitialize(EnbGlApi? gl)
    {
        if(gl is not null && gl.SupersamplingFramebuffer==framebuffer)gl.SupersamplingFramebuffer=0;
        target.Deinitialize(gl);active=false;framebuffer=destination=width=height=0;request=default;blit=null;Failure=null;
    }
}
