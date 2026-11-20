using System.Numerics;
using System.Runtime.InteropServices;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Rendering.OpenGL;

/// <summary>Context-owned UI uploads and premultiplied GPU composition.</summary>
public sealed unsafe class EnbOpenGlUiCompositor
{
    public sealed class Image
    {
        internal object? Source;
        internal uint Texture;
    }
    private int program,vertices,array,rectangle,pixelRectangle,uvRectangle,clipRectangle,holeRectangle,tint,solid,targetHeight;
    private delegate* unmanaged<int,float,float,float,float,void> uniform4;
    private delegate* unmanaged<int,float,void> uniform1;
    private delegate* unmanaged<uint,void> activeTexture;
    private delegate* unmanaged<uint,uint,void> bindTexture,bindSampler;
    private delegate* unmanaged<int,uint*,void> genTextures,deleteTextures;
    private delegate* unmanaged<uint,uint,int,void> textureParameter;
    private delegate* unmanaged<uint,int,int,int,int,int,uint,uint,void*,void> textureImage;
    private delegate* unmanaged<int,int,int,int,void> blend;
    private uint white;
    public void Initialize(EnbGlApi gl)
    {
        if(program!=0)return;
        bool es=gl.ContextInfo.Version.Type==EnbGlProfileType.OpenGLES;
        string header=es?"#version 300 es\nprecision highp float;\n":"#version 150\n";
        int vs=gl.CreateShader(0x8b31),fs=gl.CreateShader(0x8b30);
        try
        {
            string? error=gl.CompileShaderAndGetError(vs,header+"in vec2 position;uniform vec4 rectangle;uniform vec4 uvRectangle;out vec2 uv;void main(){gl_Position=vec4(mix(rectangle.xy,rectangle.zw,position),0,1);uv=mix(uvRectangle.xy,uvRectangle.zw,position);}");
            if(!string.IsNullOrWhiteSpace(error))throw new InvalidOperationException(error);
            error=gl.CompileShaderAndGetError(fs,header+"uniform sampler2D image;uniform vec4 pixelRectangle;uniform vec4 uvRectangle;uniform vec4 tint;uniform vec4 clipRectangle;uniform vec4 holeRectangle;uniform float targetHeight;uniform int solid;in vec2 uv;out vec4 result;float coverage(vec4 r,vec2 p){vec2 c=clamp(min(p-r.xy,r.zw-p)+vec2(0.5),0.0,1.0);return min(c.x,c.y);}void main(){vec2 p=vec2(gl_FragCoord.x,targetHeight-gl_FragCoord.y);float c=coverage(clipRectangle,p);if(solid==2)c=max(0.0,c-coverage(holeRectangle,p));vec4 sampleColor=texture(image,mix(uvRectangle.xy,uvRectangle.zw,vec2((p.x-pixelRectangle.x)/(pixelRectangle.z-pixelRectangle.x),1.0-(p.y-pixelRectangle.y)/(pixelRectangle.w-pixelRectangle.y))));if(solid==0)sampleColor.rgb*=sampleColor.a;if(solid==1||solid==2)sampleColor=vec4(1);result=sampleColor*tint*c;}");
            if(!string.IsNullOrWhiteSpace(error))throw new InvalidOperationException(error);
            program=gl.CreateProgram();gl.AttachShader(program,vs);gl.AttachShader(program,fs);gl.BindAttribLocationString(program,0,"position");
            error=gl.LinkProgramAndGetError(program);if(!string.IsNullOrWhiteSpace(error))throw new InvalidOperationException(error);
            rectangle=gl.GetUniformLocationString(program,"rectangle");pixelRectangle=gl.GetUniformLocationString(program,"pixelRectangle");uvRectangle=gl.GetUniformLocationString(program,"uvRectangle");clipRectangle=gl.GetUniformLocationString(program,"clipRectangle");holeRectangle=gl.GetUniformLocationString(program,"holeRectangle");tint=gl.GetUniformLocationString(program,"tint");solid=gl.GetUniformLocationString(program,"solid");targetHeight=gl.GetUniformLocationString(program,"targetHeight");
            uniform4=(delegate* unmanaged<int,float,float,float,float,void>)gl.GetProcAddress("glUniform4f");uniform1=(delegate* unmanaged<int,float,void>)gl.GetProcAddress("glUniform1f");
            activeTexture=(delegate* unmanaged<uint,void>)gl.GetProcAddress("glActiveTexture");bindTexture=(delegate* unmanaged<uint,uint,void>)gl.GetProcAddress("glBindTexture");bindSampler=(delegate* unmanaged<uint,uint,void>)gl.GetProcAddress("glBindSampler");
            genTextures=(delegate* unmanaged<int,uint*,void>)gl.GetProcAddress("glGenTextures");deleteTextures=(delegate* unmanaged<int,uint*,void>)gl.GetProcAddress("glDeleteTextures");
            textureParameter=(delegate* unmanaged<uint,uint,int,void>)gl.GetProcAddress("glTexParameteri");textureImage=(delegate* unmanaged<uint,int,int,int,int,int,uint,uint,void*,void>)gl.GetProcAddress("glTexImage2D");blend=(delegate* unmanaged<int,int,int,int,void>)gl.GetProcAddress("glBlendFuncSeparate");
            array=gl.GenVertexArray();gl.BindVertexArray(array);vertices=gl.GenBuffer();gl.BindBuffer(0x8892,vertices);
            ReadOnlySpan<float> points=[0,0,1,0,0,1,1,1];fixed(float* data=points)gl.BufferData(0x8892,(nint)(points.Length*sizeof(float)),(nint)data,0x88e4);
            gl.VertexAttribPointer(0,2,0x1406,0,8,0);gl.EnableVertexAttribArray(0);gl.BindVertexArray(0);gl.BindBuffer(0x8892,0);
            white=Upload(1,1,[255,255,255,255]);
        }
        catch{Deinitialize(gl);throw;}
        finally{gl.DeleteShader(vs);gl.DeleteShader(fs);}
    }
    private uint Upload(int width,int height,ReadOnlySpan<byte> rgba)
    {
        uint texture;genTextures(1,&texture);activeTexture(0x84c0);bindTexture(0x0de1,texture);bindSampler(0,0);
        textureParameter(0x0de1,0x2801,0x2601);textureParameter(0x0de1,0x2800,0x2601);textureParameter(0x0de1,0x2802,0x812f);textureParameter(0x0de1,0x2803,0x812f);
        fixed(byte* data=rgba)textureImage(0x0de1,0,0x8058,width,height,0,0x1908,0x1401,data);
        return texture;
    }
    public void Use(EnbGlApi gl,Image image,EnbUiRaster raster)
    {
        Initialize(gl);if(ReferenceEquals(image.Source,raster))return;Release(gl,image);
        // Preserve straight-alpha texels; filtering precedes shader premultiplication.
        var source=raster.BgraPixels.AsSpan();byte[] pixels=new byte[source.Length];
        for(int i=0;i<pixels.Length;i+=4){pixels[i]=source[i+2];pixels[i+1]=source[i+1];pixels[i+2]=source[i];pixels[i+3]=source[i+3];}
        image.Texture=Upload(raster.Width,raster.Height,pixels);image.Source=raster;
    }
    public void Use(EnbGlApi gl,Image image,EnbTextureImage texture)
    {
        Initialize(gl);if(ReferenceEquals(image.Source,texture))return;Release(gl,image);
        image.Texture=Upload(texture.Width,texture.Height,MemoryMarshal.AsBytes(texture.Pixels.Span));image.Source=texture;
    }
    public void Release(EnbGlApi? gl,Image image)
    {
        uint texture=image.Texture;if(gl is not null&&texture!=0)deleteTextures(1,&texture);image.Texture=0;image.Source=null;
    }
    public void Draw(EnbGlApi gl,int framebuffer,int width,int height,Image image,Vector4 bounds,float opacity,Vector4 clip)
        =>DrawTexture(gl,framebuffer,width,height,image.Texture,bounds,new(0,1,1,0),new(opacity),clip,0,default);
    public void DrawCaptured(EnbGlApi gl,int framebuffer,int width,int height,int texture,int size)
        =>DrawTexture(gl,framebuffer,width,height,(uint)texture,new(0,0,width,height),new(0,0,(float)width/size,(float)height/size),Vector4.One,new(0,0,width,height),3,default);
    public void MakeOpaque(EnbGlApi gl,int framebuffer,int width,int height)
        =>DrawTexture(gl,framebuffer,width,height,white,new(0,0,width,height),new(0,1,1,0),new(0,0,0,1),new(0,0,width,height),1,default,true);
    public void Fill(EnbGlApi gl,int framebuffer,int width,int height,Vector4 bounds,uint argb,Vector4? hole=null)
    {
        Initialize(gl);uint alpha=argb>>24;float a=alpha/255f;
        float Premultiply(uint channel)=>((channel*alpha+127)/255)/255f;
        DrawTexture(gl,framebuffer,width,height,white,bounds,new(0,1,1,0),new(Premultiply((argb>>16)&255),Premultiply((argb>>8)&255),Premultiply(argb&255),a),bounds,hole.HasValue?2:1,hole??default);
    }
    private void DrawTexture(EnbGlApi gl,int framebuffer,int width,int height,uint texture,Vector4 bounds,Vector4 uv,Vector4 color,Vector4 clip,int mode,Vector4 hole,bool behind=false)
    {
        if(bounds.Z<=bounds.X||bounds.W<=bounds.Y||color.W<=0)return;Initialize(gl);
        if(mode!=0)bounds=new(MathF.Floor(bounds.X),MathF.Floor(bounds.Y),MathF.Ceiling(bounds.Z),MathF.Ceiling(bounds.W));
        gl.BindFramebuffer(0x8d40,framebuffer);gl.Viewport(0,0,width,height);gl.Disable(0x0b71);gl.DepthMask(0);gl.Disable(0x0b44);gl.Disable(0x0c11);gl.UseProgram(program);gl.BindVertexArray(array);
        uniform4(pixelRectangle,bounds.X,bounds.Y,bounds.Z,bounds.W);uniform4(rectangle,bounds.X*2/width-1,1-bounds.W*2/height,bounds.Z*2/width-1,1-bounds.Y*2/height);uniform4(uvRectangle,uv.X,uv.Y,uv.Z,uv.W);uniform4(tint,color.X,color.Y,color.Z,color.W);uniform4(clipRectangle,clip.X,clip.Y,clip.Z,clip.W);uniform4(holeRectangle,hole.X,hole.Y,hole.Z,hole.W);uniform1(targetHeight,height);gl.Uniform1i(solid,mode);
        activeTexture(0x84c0);bindSampler(0,0);bindTexture(0x0de1,texture);gl.Enable(0x0be2);if(behind)blend(0x0305,1,0x0305,1);else blend(1,0x0303,1,0x0303);gl.DrawArrays(5,0,4);gl.Disable(0x0be2);gl.BindVertexArray(0);gl.UseProgram(0);gl.DepthMask(1);
    }
    public void Deinitialize(EnbGlApi? gl)
    {
        if(gl is not null){if(white!=0){uint texture=white;deleteTextures(1,&texture);}if(array!=0)gl.DeleteVertexArray(array);if(vertices!=0)gl.DeleteBuffer(vertices);if(program!=0)gl.DeleteProgram(program);}
        white=0;array=vertices=program=0;
    }
}
