using EnbNext.Presentation;
using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Rendering.OpenGL;

public sealed partial class EnbOpenGlBackdropRenderer
{
    // Homogeneous GPU vertices preserve perspective interpolation and near-plane
    // clipping. This path is shared by galaxy system patches, links and labels.
    public void RenderMapQuad(GlInterface gl,int framebuffer,int width,int height,
        EnbMapQuad quad,EnbResolvedRenderCamera camera)
    {
        if (!MapQuadMode) throw new InvalidOperationException("Map quad renderer required.");
        Vector3[] positions = [quad.A,quad.B,quad.C,quad.D];
        Vector2[] uv = [quad.UvA,quad.UvB,quad.UvC,quad.UvD];
        Span<float> vertices = stackalloc float[24];
        for (int i=0;i<4;i++)
        {
            var clip = Vector4.Transform(new Vector4(camera.WorldToCanonicalView(positions[i]),1),camera.Projection.Matrix);
            vertices[i*6] = clip.X; vertices[i*6+1] = clip.Y;
            // Canonical depth is [0,1]; OpenGL clipping is [-1,1].
            vertices[i*6+2] = clip.Z*2-clip.W; vertices[i*6+3] = clip.W;
            vertices[i*6+4] = uv[i].X; vertices[i*6+5] = uv[i].Y;
        }
        gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER,_vertexBuffer);
        UploadArray(gl,vertices);
        gl.UseProgram(_program);
        _environmentTexture=EnsureTexture(gl,_environmentTexture,quad.Texture,ref _uploadedEnvironment);
        RequireProc(_activeTexture,"glActiveTexture")(GlTexture0);
        RequireProc(_bindTexture,"glBindTexture")(GlTexture2D,_environmentTexture);
        RequireProc(_texParameteri,"glTexParameteri")(GlTexture2D,GlTextureWrapS,0x2901);
        RequireProc(_texParameteri,"glTexParameteri")(GlTexture2D,GlTextureWrapT,0x2901);
        RenderOverlay(gl,framebuffer,0,0,width,height,quad.Texture,true,opacity:quad.Opacity,sourceAlphaAdditive:quad.SourceAlpha);
    }
}
