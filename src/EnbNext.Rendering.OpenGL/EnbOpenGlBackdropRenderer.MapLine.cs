using EnbNext.Presentation;
using System.Numerics;

namespace EnbNext.Rendering.OpenGL;

public sealed partial class EnbOpenGlBackdropRenderer
{
    // 007BF930: route RGB (0,.8,0), alpha .5, width .75 in authored interface units.
    private static readonly EnbUiRaster RouteColor = new(1, 1, [0, 204, 0, 128]);
    private static readonly EnbUiRaster WarpRouteColor = new(1, 1, [0, 0, 204, 128]);

    public void RenderMapLine(GlInterface gl, int framebuffer, int width, int height, Vector2 start, Vector2 end,
        EnbUiRaster? texture=null,float lineWidth=.75f,float repeat=1,float offset=0,bool warp=false)
    {
        var delta = end - start;
        if (delta.LengthSquared() < .000001f) return;
        var normal = Vector2.Normalize(new(-delta.Y, delta.X)) * (lineWidth*.5f);
        Vector2[] corners = [start + normal, start - normal, end + normal, end - normal];
        Span<float> vertices = stackalloc float[16];
        for (int i = 0; i < 4; i++)
        {
            vertices[i * 4] = corners[i].X / 320 - 1;
            vertices[i * 4 + 1] = 1 - corners[i].Y / 240;
            vertices[i * 4 + 2] = texture is null ? .5f : i%2;
            vertices[i * 4 + 3] = texture is null ? .5f : offset+(i<2 ? 0 : repeat);
        }
        gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, _vertexBuffer);
        UploadArray(gl, vertices);
        if (texture is not null)
        {
            _environmentTexture=EnsureTexture(gl,_environmentTexture,texture,ref _uploadedEnvironment);
            RequireProc(_activeTexture,"glActiveTexture")(GlTexture0);
            RequireProc(_bindTexture,"glBindTexture")(GlTexture2D,_environmentTexture);
            RequireProc(_texParameteri,"glTexParameteri")(GlTexture2D,0x2802,0x2901);
            RequireProc(_texParameteri,"glTexParameteri")(GlTexture2D,0x2803,0x2901);
        }
        RenderOverlay(gl, framebuffer, 0, 0, width, height, texture ?? (warp ? WarpRouteColor : RouteColor),texture is not null);
    }
}
