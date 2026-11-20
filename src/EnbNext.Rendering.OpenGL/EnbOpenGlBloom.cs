namespace EnbNext.Rendering.OpenGL;

/// <summary>Scene-only bloom and HDR tone mapping. Owns scratch textures; never reads back or alters scene depth.</summary>
public sealed unsafe class EnbOpenGlBloom
{
    private int program, array, width, height, mode, direction, sceneLocation, glowLocation, hdrLocation, bloomLocation;
    private bool floatingPoint;
    private readonly int[] textures=new int[3], targets=new int[3];
    private delegate* unmanaged<int,int,int,int,int,int,int,int,nint,void> image;
    private delegate* unmanaged<int,int,int,void> parameter;
    private delegate* unmanaged<int,int,int,int,int,void> attach;
    private delegate* unmanaged<int,int> check;
    private delegate* unmanaged<int,int,int,int,int,int,int,int,int,int,void> blit;
    private delegate* unmanaged<int,float,float,void> uniform2;
    public bool IsAllocated => program!=0;
    public string? Failure {get;private set;}

    public int BeginScene(EnbGlApi gl,EnbOpenGlScannerFramebuffer target,int destination,int w,int h,bool hdr,out string? failure)
    {
        failure=null;
        if(hdr && gl.ContextInfo.Version.Type==EnbGlProfileType.OpenGLES &&
            !gl.ContextInfo.Extensions.Contains("GL_EXT_color_buffer_half_float") &&
            !gl.ContextInfo.Extensions.Contains("GL_EXT_color_buffer_float"))
        {failure="HDR scene target is not supported by this graphics device.";hdr=false;}
        try {int fb=target.BeginScene(gl,destination,w,h,hdr);gl.HighDynamicRange=hdr;return fb;}
        catch(Exception ex) when(hdr && ex is InvalidOperationException or NotSupportedException)
        {
            target.Deinitialize(gl);gl.HighDynamicRange=false;failure="HDR scene target unavailable: "+ex.Message;
            return target.BeginScene(gl,destination,w,h);
        }
    }
    public string? CompleteScene(EnbGlApi gl,int destination,int w,int h,bool bloom)
    {
        bool hdr=gl.HighDynamicRange;gl.HighDynamicRange=false;
        if(hdr||bloom)Apply(gl,destination,w,h,hdr,bloom);
        return Failure;
    }
    public void Apply(EnbGlApi gl,int destination,int w,int h,bool hdr=false,bool bloom=true)
    {
        if(Failure is not null||w<1||h<1)return;
        try
        {
            Prepare(gl,w,h,hdr);
            gl.Disable(0x0C11);gl.Disable(0x0BE2);gl.Disable(0x0B71);gl.Disable(0x0B44);gl.DepthMask(0);
            gl.BindFramebuffer(0x8CA8,destination);gl.BindFramebuffer(0x8CA9,targets[0]);
            blit(0,0,w,h,0,0,w,h,0x4000,0x2600);
            gl.UseProgram(program);gl.BindVertexArray(array);
            gl.Uniform1i(hdrLocation,hdr?1:0);gl.Uniform1i(bloomLocation,bloom?1:0);
            gl.Uniform1i(sceneLocation,0);gl.Uniform1i(glowLocation,1);
            // Extract each source texel before averaging so narrow lights survive reduction.
            int bw=(w+3)/4,bh=(h+3)/4;
            float spread=Math.Max(1f,h/480f);
            if(bloom)
            {
            Pass(targets[1],bw,bh,textures[0],0,0,0);
            Pass(targets[2],bw,bh,textures[1],1,spread/bw,0);
            Pass(targets[1],bw,bh,textures[2],1,0,spread/bh);
            }
            gl.ActiveTexture(0x84C1);gl.BindTexture(0x0DE1,textures[1]);
            Pass(destination,w,h,textures[0],2,0,0);
            void Pass(int target,int pw,int ph,int texture,int operation,float dx,float dy)
            {
                gl.BindFramebuffer(0x8D40,target);gl.Viewport(0,0,pw,ph);gl.ActiveTexture(0x84C0);gl.BindTexture(0x0DE1,texture);
                gl.Uniform1i(mode,operation);uniform2(direction,dx,dy);gl.DrawArrays(4,0,3);
            }
        }
        catch(Exception ex) when(ex is InvalidOperationException or NotSupportedException)
        {Failure="Scene post-processing unavailable: "+ex.Message;Release(gl);}
        finally
        {
            gl.ActiveTexture(0x84C1);gl.BindTexture(0x0DE1,0);gl.ActiveTexture(0x84C0);gl.BindTexture(0x0DE1,0);
            gl.BindVertexArray(0);gl.UseProgram(0);gl.DepthMask(1);gl.BindFramebuffer(0x8D40,destination);gl.Viewport(0,0,w,h);
        }
    }
    private void Prepare(EnbGlApi gl,int w,int h,bool hdr)
    {
        if(program==0)
        {
            image=(delegate* unmanaged<int,int,int,int,int,int,int,int,nint,void>)gl.GetProcAddress("glTexImage2D");
            parameter=(delegate* unmanaged<int,int,int,void>)gl.GetProcAddress("glTexParameteri");
            attach=(delegate* unmanaged<int,int,int,int,int,void>)gl.GetProcAddress("glFramebufferTexture2D");
            check=(delegate* unmanaged<int,int>)gl.GetProcAddress("glCheckFramebufferStatus");
            blit=(delegate* unmanaged<int,int,int,int,int,int,int,int,int,int,void>)gl.GetProcAddress("glBlitFramebuffer");
            uniform2=(delegate* unmanaged<int,float,float,void>)gl.GetProcAddress("glUniform2f");
            if(image==null||parameter==null||attach==null||check==null||blit==null||uniform2==null)throw new NotSupportedException("Required GPU procedures missing.");
            bool es=gl.ContextInfo.Version.Type==EnbGlProfileType.OpenGLES;
            string header=es?"#version 300 es\nprecision highp float;\n":"#version 150\n";
            int vs=gl.CreateShader(0x8B31),fs=gl.CreateShader(0x8B30);
            try
            {
                string? error=gl.CompileShaderAndGetError(vs,header+"out vec2 uv;void main(){vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);uv=p;gl_Position=vec4(p*2.0-1.0,0,1);}");
                if(!string.IsNullOrEmpty(error))throw new InvalidOperationException(error);
                error=gl.CompileShaderAndGetError(fs,header+"""
                    in vec2 uv;out vec4 result;uniform sampler2D scene;uniform sampler2D glow;uniform int mode;uniform int hdr;uniform int bloomEnabled;uniform vec2 direction;
                    vec3 sampleGlow(vec2 p){return texture(scene,p).rgb;}
                    // Compress overbright channels without inventing light in the other channels.
                    vec3 toneMap(vec3 c){c=max(c,vec3(0));vec3 excess=max(c-0.75,vec3(0));
                    return min(c,vec3(0.75))+excess/(vec3(1)+4.0*excess);}

                    vec3 extractGlow(vec3 c){if(hdr!=0)c=toneMap(c);float peak=max(c.r,max(c.g,c.b));return c*max(peak-0.6,0.0)/(0.4*max(peak,0.0001));}
                    void main(){if(mode==0){ivec2 size=textureSize(scene,0);ivec2 start=ivec2(gl_FragCoord.xy)*4;vec3 sum=vec3(0);float count=0.0;
                    for(int y=0;y<4;y++)for(int x=0;x<4;x++){ivec2 p=start+ivec2(x,y);if(all(lessThan(p,size))){sum+=extractGlow(texelFetch(scene,p,0).rgb);count+=1.0;}}
                    result=vec4(sum/max(count,1.0),1);return;}if(mode==2){vec4 c=texture(scene,uv);vec3 rgb=c.rgb;
                    if(hdr!=0)rgb=toneMap(rgb);
                    if(bloomEnabled!=0){vec3 halo=max(texture(glow,uv).rgb-extractGlow(c.rgb),vec3(0));
                    rgb+=(1.0-max(rgb.r,max(rgb.g,rgb.b)))*(vec3(1)-exp(-halo*1.2));}
                    result=vec4(rgb,c.a);return;}
                    vec3 c=sampleGlow(uv)*0.227027;
                    c+=(sampleGlow(uv+direction*1.384615)+sampleGlow(uv-direction*1.384615))*0.316216;
                    c+=(sampleGlow(uv+direction*3.230769)+sampleGlow(uv-direction*3.230769))*0.070270;
                    result=vec4(c,1);}
                    """);
                if(!string.IsNullOrEmpty(error))throw new InvalidOperationException(error);
                program=gl.CreateProgram();gl.AttachShader(program,vs);gl.AttachShader(program,fs);
                error=gl.LinkProgramAndGetError(program);if(!string.IsNullOrEmpty(error))throw new InvalidOperationException(error);
                hdrLocation=gl.GetUniformLocationString(program,"hdr");bloomLocation=gl.GetUniformLocationString(program,"bloomEnabled");
                mode=gl.GetUniformLocationString(program,"mode");direction=gl.GetUniformLocationString(program,"direction");
                sceneLocation=gl.GetUniformLocationString(program,"scene");glowLocation=gl.GetUniformLocationString(program,"glow");array=gl.GenVertexArray();
                for(int i=0;i<3;i++){textures[i]=gl.GenTexture();int id;((delegate* unmanaged<int,int*,void>)gl.GetProcAddress("glGenFramebuffers"))(1,&id);targets[i]=id;}
            }
            finally{gl.DeleteShader(vs);gl.DeleteShader(fs);}
        }
        if(width==w&&height==h&&floatingPoint==hdr)return;
        for(int i=0;i<3;i++)
        {
            gl.ActiveTexture(0x84C0);gl.BindTexture(0x0DE1,textures[i]);
            parameter(0x0DE1,0x2801,0x2601);parameter(0x0DE1,0x2800,0x2601);parameter(0x0DE1,0x2802,0x812F);parameter(0x0DE1,0x2803,0x812F);
            image(0x0DE1,0,hdr?0x881A:0x8058,i==0?w:(w+3)/4,i==0?h:(h+3)/4,0,0x1908,hdr?0x140B:0x1401,0);
            gl.BindFramebuffer(0x8D40,targets[i]);attach(0x8D40,0x8CE0,0x0DE1,textures[i],0);
            if(check(0x8D40)!=0x8CD5)throw new InvalidOperationException("Incomplete bloom framebuffer.");
        }
        width=w;height=h;floatingPoint=hdr;
    }
    public void Reset(EnbGlApi? gl){Release(gl);Failure=null;}
    private void Release(EnbGlApi? gl)
    {
        for(int i=0;i<3;i++){if(gl is not null&&targets[i]!=0){int id=targets[i];((delegate* unmanaged<int,int*,void>)gl.GetProcAddress("glDeleteFramebuffers"))(1,&id);}if(textures[i]!=0)gl?.DeleteTexture(textures[i]);targets[i]=textures[i]=0;}
        if(array!=0)gl?.DeleteVertexArray(array);if(program!=0)gl?.DeleteProgram(program);array=program=width=height=0;
    }
}
