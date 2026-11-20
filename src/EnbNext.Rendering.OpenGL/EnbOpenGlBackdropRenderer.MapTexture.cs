
namespace EnbNext.Rendering.OpenGL;

public sealed partial class EnbOpenGlBackdropRenderer
{
    private bool _mapTextureMode;

    private static string CreateMapTextureShader(string source)
    {
        string sample=source.Contains("texture2D(",StringComparison.Ordinal) ? "texture2D" : "texture";
        string function="vec4 mapSample(){ vec2 uv=vec2(vTexCoord.x,1.0-vTexCoord.y); vec3 b="+sample+
            "(uTexture,uv).rgb; if(uTransmit!=0) b=max(vec3(0.0),"+sample+"(uWhite,uv).rgb-b); return vec4(b,1.0); } ";
        return source.Replace("uniform sampler2D uTexture;","uniform sampler2D uTexture; uniform sampler2D uWhite; uniform int uTransmit;")
            .Replace(sample+"(uTexture,vTexCoord).bgra","mapSample()")
            .Replace("void main()",function+"void main()");
    }

    public void InitializeMapTexture(GlInterface gl)
    {
        _mapTextureMode=true;
        Initialize(gl);
    }

    // A cached planet retains its material operation C(destination)=B+T*destination.
    // B is its black-background capture; T is white capture minus black capture.
    // This preserves ATMOS Screen blending without making its dim glow opaque.
    public void RenderMapTexture(GlInterface gl,int framebuffer,int x,int y,int width,int height,int texture,int whiteTexture)
    {
        gl.BindFramebuffer(GlConsts.GL_FRAMEBUFFER,framebuffer);
        gl.Viewport(x,y,width,height);
        gl.Disable(GlDepthTest); gl.DepthMask(0); gl.Disable(GlCullFace);
        gl.UseProgram(_program); gl.Uniform1i(_textureUniform,0);
        gl.Uniform1i(gl.GetUniformLocationString(_program,"uWhite"),1);
        BindGeometry(gl);
        RequireProc(_activeTexture,"glActiveTexture")(GlTexture0);
        RequireProc(_bindTexture,"glBindTexture")(GlTexture2D,texture);
        RequireProc(_activeTexture,"glActiveTexture")(GlTexture0+1);
        RequireProc(_bindTexture,"glBindTexture")(GlTexture2D,whiteTexture);
        gl.Enable(GlBlend);
        var blend=LoadRequiredProc<GlBlendFuncSeparateProc>(gl,"glBlendFuncSeparate");
        gl.Uniform1i(gl.GetUniformLocationString(_program,"uTransmit"),1);
        blend(0,0x0300,0,1); // destination RGB *= transmittance; preserve coverage.
        gl.DrawArrays(GlTriangleStrip,0,4);
        gl.Uniform1i(gl.GetUniformLocationString(_program,"uTransmit"),0);
        blend(1,1,0,1); // then add the cached emitted/reflected light.
        gl.DrawArrays(GlTriangleStrip,0,4);
        RequireProc(_activeTexture,"glActiveTexture")(GlTexture0);
        gl.Disable(GlBlend); gl.DepthMask(1);
        if(_vertexArray!=0) gl.BindVertexArray(0);
        gl.UseProgram(0);
    }
}
