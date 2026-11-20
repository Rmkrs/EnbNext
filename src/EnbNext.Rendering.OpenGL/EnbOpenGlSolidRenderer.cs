using System.Numerics;

namespace EnbNext.Rendering.OpenGL;

/// <summary>Retained untextured Render2D quad; color and extent are uniforms.</summary>
public sealed unsafe class EnbOpenGlSolidRenderer
{
    private int program,vertices,array,colorLocation,rectangleLocation;
    private delegate* unmanaged<int,float,float,float,float,void> uniform4;
    private delegate* unmanaged<int,int,int,int,void> blend;
    public bool IsInitialized=>program!=0;
    public void Initialize(EnbGlApi gl)
    {
        if(IsInitialized)return;
        var version=gl.ContextInfo.Version;
        bool es=version.Type==EnbGlProfileType.OpenGLES;
        if(es?version.Major<3:version.Major<3 || version.Major==3&&version.Minor<2)
            throw new NotSupportedException("Solid UI quads require ES3 or core GL3.2.");
        string header=es?"#version 300 es\nprecision highp float;\n":"#version 150\n";
        int vs=gl.CreateShader(0x8b31),fs=gl.CreateShader(0x8b30);
        try
        {
            var error=gl.CompileShaderAndGetError(vs,header+"in vec2 position;uniform vec4 rectangle;void main(){gl_Position=vec4(mix(rectangle.xy,rectangle.zw,position),0.0,1.0);}");
            if(!string.IsNullOrWhiteSpace(error))throw new InvalidOperationException(error);
            error=gl.CompileShaderAndGetError(fs,header+"uniform vec4 color;out vec4 result;void main(){result=color;}");
            if(!string.IsNullOrWhiteSpace(error))throw new InvalidOperationException(error);
            program=gl.CreateProgram();gl.AttachShader(program,vs);gl.AttachShader(program,fs);gl.BindAttribLocationString(program,0,"position");
            error=gl.LinkProgramAndGetError(program);if(!string.IsNullOrWhiteSpace(error))throw new InvalidOperationException(error);
            colorLocation=gl.GetUniformLocationString(program,"color");rectangleLocation=gl.GetUniformLocationString(program,"rectangle");
            uniform4=(delegate* unmanaged<int,float,float,float,float,void>)gl.GetProcAddress("glUniform4f");
            blend=(delegate* unmanaged<int,int,int,int,void>)gl.GetProcAddress("glBlendFuncSeparate");
            if(uniform4==null||blend==null)throw new NotSupportedException("Missing solid UI GL procedures.");
            array=gl.GenVertexArray();gl.BindVertexArray(array);vertices=gl.GenBuffer();gl.BindBuffer(0x8892,vertices);
            ReadOnlySpan<float> quad=[0,0,1,0,0,1,1,1];
            fixed(float* data=quad)gl.BufferData(0x8892,(nint)(quad.Length*sizeof(float)),(nint)data,0x88e4);
            gl.VertexAttribPointer(0,2,0x1406,0,2*sizeof(float),0);gl.EnableVertexAttribArray(0);
            gl.BindVertexArray(0);gl.BindBuffer(0x8892,0);
        }
        catch{Deinitialize(gl);throw;}
        finally{gl.DeleteShader(vs);gl.DeleteShader(fs);}
    }
    public void Render(EnbGlApi gl,int framebuffer,int width,int height,Vector4 bounds,uint argb)
    {
        if(width<=0||height<=0||bounds.Z<=bounds.X||bounds.W<=bounds.Y||(argb>>24)==0)return;
        if(!IsInitialized)Initialize(gl);
        gl.BindFramebuffer(0x8d40,framebuffer);gl.Viewport(0,0,width,height);gl.Disable(0x0b71);gl.DepthMask(0);gl.Disable(0x0b44);
        gl.UseProgram(program);gl.BindVertexArray(array);
        uniform4(rectangleLocation,bounds.X*2/width-1,1-bounds.W*2/height,bounds.Z*2/width-1,1-bounds.Y*2/height);
        float alpha=(argb>>24)/255f;
        uniform4(colorLocation,((argb>>16)&255)/255f*alpha,((argb>>8)&255)/255f*alpha,(argb&255)/255f*alpha,alpha);
        gl.Enable(0x0be2);blend(1,0x0303,1,0x0303);gl.DrawArrays(5,0,4);
        gl.Disable(0x0be2);gl.BindVertexArray(0);gl.UseProgram(0);gl.DepthMask(1);
    }
    public void Deinitialize(EnbGlApi? gl)
    {
        if(array!=0)gl?.DeleteVertexArray(array);if(vertices!=0)gl?.DeleteBuffer(vertices);if(program!=0)gl?.DeleteProgram(program);
        array=vertices=program=0;uniform4=null;blend=null;
    }
}
