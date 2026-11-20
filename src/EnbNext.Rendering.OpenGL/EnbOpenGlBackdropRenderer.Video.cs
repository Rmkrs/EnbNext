using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Rendering.OpenGL;

public sealed partial class EnbOpenGlBackdropRenderer
{
    public void RenderVideoSubtitle(GlInterface gl, int framebuffer, int width, int height,
        EnbUiRaster raster, System.Numerics.Vector4 bounds)
    {
        gl.BindFramebuffer(GlConsts.GL_FRAMEBUFFER, framebuffer); gl.Viewport(0, 0, width, height);
        gl.Disable(GlDepthTest); gl.DepthMask(0); gl.Disable(GlCullFace);
        gl.UseProgram(_program); gl.Uniform1i(_textureUniform, 0);
        if (_opacityUniform >= 0) _uniform1f!(_opacityUniform, 1);
        if (_brightnessUniform >= 0) _uniform1f!(_brightnessUniform, 1);
        if (_nativeRgbaUniform >= 0) gl.Uniform1i(_nativeRgbaUniform, 0);
        BindGeometry(gl); gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, _vertexBuffer);
        float left = 2 * bounds.X - 1, right = 2 * bounds.Z - 1, top = 1 - 2 * bounds.Y, bottom = 1 - 2 * bounds.W;
        UploadArray(gl, [left,bottom,0,1, right,bottom,1,1, left,top,0,0, right,top,1,0]);
        _environmentTexture = EnsureTexture(gl, _environmentTexture, raster, ref _uploadedEnvironment);
        _activeTexture!(GlTexture0); _bindTexture!(GlTexture2D, _environmentTexture);
        gl.Enable(GlBlend);
        LoadRequiredProc<GlBlendFuncSeparateProc>(gl, "glBlendFuncSeparate")(0x0302, 0x0303, 1, 0x0303);
        gl.DrawArrays(GlTriangleStrip, 0, 4); gl.Disable(GlBlend);
        if (_vertexArray != 0) gl.BindVertexArray(0);
        gl.UseProgram(0); gl.DepthMask(1);
    }
    private byte[]? _uploadedVideo;
    public unsafe void RenderVideoTile(GlInterface gl, int framebuffer, int width, int height, byte[] rgb565, EnbRetailVideoTile tile)
    {
        gl.BindFramebuffer(GlConsts.GL_FRAMEBUFFER, framebuffer); gl.Viewport(0, 0, width, height);
        gl.Disable(GlDepthTest); gl.DepthMask(0); gl.Disable(GlCullFace); gl.Disable(GlBlend);
        gl.UseProgram(_program); gl.Uniform1i(_textureUniform, 0);
        if (_opacityUniform >= 0) _uniform1f!(_opacityUniform, 1);
        if (_brightnessUniform >= 0) _uniform1f!(_brightnessUniform, 1);
        BindGeometry(gl); gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, _vertexBuffer);
        var d = tile.Destination; var uv = tile.Uv;
        float left = 2 * d.X - 1, right = 2 * d.Z - 1, top = 1 - 2 * d.Y, bottom = 1 - 2 * d.W;
        UploadArray(gl, [left,bottom,uv.X,uv.W, right,bottom,uv.Z,uv.W, left,top,uv.X,uv.Y, right,top,uv.Z,uv.Y]);
        if (_environmentTexture == 0) { _genTextures!(1, out _environmentTexture); _uploadedVideo = null; }
        _activeTexture!(GlTexture0); _bindTexture!(GlTexture2D, _environmentTexture);
        if (!ReferenceEquals(_uploadedVideo, rgb565))
        {
            _pixelStorei!(GlUnpackAlignment, 1);
            _texParameteri!(GlTexture2D, GlTextureMinFilter, GlLinear); _texParameteri!(GlTexture2D, GlTextureMagFilter, GlLinear);
            _texParameteri!(GlTexture2D, GlTextureWrapS, GlClampToEdge); _texParameteri!(GlTexture2D, GlTextureWrapT, GlClampToEdge);
            fixed (byte* pixels = rgb565) _texImage2D!(GlTexture2D, 0, 0x1907, tile.TextureWidth, tile.TextureHeight, 0, 0x1907, 0x8363, (IntPtr)pixels);
            _uploadedVideo = rgb565;
        }
        gl.Uniform1i(_nativeRgbaUniform, 1);
        gl.DrawArrays(GlTriangleStrip, 0, 4);
        if (_vertexArray != 0) gl.BindVertexArray(0);
        gl.UseProgram(0); gl.DepthMask(1);
    }
}
