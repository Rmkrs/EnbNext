using EnbNext.Presentation;
using System.Numerics;
namespace EnbNext.Rendering.OpenGL;
public sealed partial class EnbOpenGlBackdropRenderer
{
    private bool _dazzleMode;
    public void InitializeDazzle(GlInterface gl){_dazzleMode=true;Initialize(gl);}
    public void RenderDazzle(GlInterface gl,int framebuffer,int width,int height,EnbUiRaster texture,
        Vector2 center,Vector2 extent,Vector4 uv,Vector3 color,float? depth=null)
    {
        gl.BindFramebuffer(GlConsts.GL_FRAMEBUFFER,framebuffer);gl.Viewport(0,0,width,height);
        if(depth.HasValue){gl.Enable(GlDepthTest);gl.DepthFunc(0x0203);}else gl.Disable(GlDepthTest);
        gl.DepthMask(0);gl.Disable(GlCullFace);
        gl.UseProgram(_program);gl.Uniform1i(_textureUniform,0);
        _uniform1f!(_dazzleDepthUniform,depth??0);
        // Retail 0096C890 clamps each halo/flare RGB channel before packing
        // the quad vertex color, even when an authored intensity exceeds one.
        _uniform1f!(_dazzleTint0Uniform,Math.Clamp(color.X,0,1));
        _uniform1f!(_dazzleTint1Uniform,Math.Clamp(color.Y,0,1));
        _uniform1f!(_dazzleTint2Uniform,Math.Clamp(color.Z,0,1));
        BindGeometry(gl);gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER,_vertexBuffer);
        float l=center.X-extent.X,r=center.X+extent.X,b=center.Y-extent.Y,t=center.Y+extent.Y;
        UploadArray(gl,[l,b,uv.X,uv.W,r,b,uv.Z,uv.W,l,t,uv.X,uv.Y,r,t,uv.Z,uv.Y]);
        _environmentTexture=EnsureTexture(gl,_environmentTexture,texture,ref _uploadedEnvironment);
        _activeTexture!(GlTexture0);_bindTexture!(GlTexture2D,_environmentTexture);
        gl.Enable(GlBlend);_blendFuncSeparate!(1,1,0,1);
        gl.DrawArrays(GlTriangleStrip,0,4);gl.Disable(GlBlend);gl.Disable(GlDepthTest);gl.DepthMask(1);
        if(_vertexArray!=0)gl.BindVertexArray(0);gl.UseProgram(0);
    }
}
