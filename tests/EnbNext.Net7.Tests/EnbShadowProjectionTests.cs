using System.Numerics;
using EnbNext.Rendering;
using EnbNext.Rendering.OpenGL;
using Xunit;

namespace EnbNext.Net7.Tests;

public sealed class EnbShadowProjectionTests
{
    [Theory]
    [InlineData(62, 62, 1)]
    [InlineData(126, 62, 4)]
    public void CaptureRasterCentersMatchTopDownIntegerSamples(int width, int height, float w)
    {
        // A D3D sample at integer (17,23), expressed in clip coordinates.
        var native = new Vector4((2f * 17 / width - 1) * w,
            (1 - 2f * 23 / height) * w, .3f * w, w);
        var gl = Vector4.Transform(native, EnbOpenGlRenderTexture.RetailCaptureRasterTranslation(width, height));
        Assert.Equal(17.5f, (gl.X / gl.W + 1) * width / 2, 4);
        Assert.Equal(height - 23.5f, (gl.Y / gl.W + 1) * height / 2, 4);
        Assert.Equal(native.Z, gl.Z);
        Assert.Equal(native.W, gl.W);
    }

    [Theory]
    [InlineData(0, 0, -1)]
    [InlineData(0, 0, 1)]
    [InlineData(2, -1, -3)]
    public void CaptureCameraProjectsNativeBoundsIntoInsetViewport(float x, float y, float z)
    {
        var p = EnbCurrentRetailShadowProjection.Create(new(1, 2, 3), new(4, 2, 1),
            Matrix4x4.CreateTranslation(20, -10, 30), Vector3.Normalize(new(x, y, z)), 64);
        Assert.True(p.TryCreateCaptureCamera(64, out var camera));
        Assert.Equal(62, camera.Viewport.PixelWidth);
        Assert.Equal(62, camera.Viewport.PixelHeight);
        Assert.Equal(.01f, camera.Projection.ClipRange.NearPlane);
        Assert.Equal(p.Far, camera.Projection.ClipRange.FarPlane);
        foreach (float u in new[] { 0f, .5f, 1f })
        foreach (float v in new[] { 0f, .5f, 1f })
        {
            var world = Vector3.Transform(new(
                p.Bounds.X + u * (p.Bounds.Y - p.Bounds.X),
                p.Bounds.Z + v * (p.Bounds.W - p.Bounds.Z), -p.Near), p.WorldFromProjector);
            Assert.True(camera.Projection.TryProjectViewPoint(camera.WorldToCanonicalView(world), out var ndc));
            Assert.Equal(2 * u - 1, ndc.X, 4);
            Assert.Equal(2 * v - 1, ndc.Y, 4);
        }
    }

    [Fact]
    public void DownwardProjectionRetainsNativeDepthVolumeAndTextureBorder()
    {
        var p = EnbCurrentRetailShadowProjection.Create(Vector3.Zero, new(2, 3, 4),
            Matrix4x4.Identity, -Vector3.UnitZ, 64);
        float distance = 2 * MathF.Sqrt(29);
        Assert.Equal(new Vector4(-3, 3, -2, 2), p.Bounds);
        Assert.Equal(distance, p.Near, 5);
        // Raw compare sequence leaves operands 4,3,3 for these input axes.
        Assert.Equal(2 * (distance + 4 + 3 * .34375f + 3 * .25f), p.Far, 5);
        Assert.Equal(new Vector3(0, -1, 0), Vector3.TransformNormal(Vector3.UnitX, p.WorldFromProjector));
        Assert.Equal(new Vector3(1, 0, 0), Vector3.TransformNormal(Vector3.UnitY, p.WorldFromProjector));
        var uv = Vector3.Transform(Vector3.Zero, p.TextureFromWorld);
        Assert.Equal(31f / 64, uv.X, 6);
        Assert.Equal(31f / 64, uv.Y, 6);
        Assert.Equal(0, Vector3.Transform(new(0, 3, 0), p.TextureFromWorld).X, 6);
        Assert.Equal(0, Vector3.Transform(new(2, 0, 0), p.TextureFromWorld).Y, 6);
        var gradient = Vector3.Transform(Vector3.Zero, p.GradientFromWorld);
        Assert.Equal(.5f, gradient.X);
        Assert.Equal((distance - .1f) / (p.Far - .1f), gradient.Y, 6);
        Assert.Equal(distance - (p.Near + p.Far) * .5f, p.VolumeCenter.Z, 5);
        Assert.Equal((p.Far - p.Near) * .5f, p.VolumeHalfExtent.Z, 5);
    }

    [Fact]
    public void ExplicitPlanesChangeVolumeButMapperDepthKeepsFixedNativeNear()
    {
        var p = EnbCurrentRetailShadowProjection.Create(Vector3.Zero, Vector3.One,
            Matrix4x4.Identity, -Vector3.UnitZ, 64, 7, 3000);
        float distance = 2 * MathF.Sqrt(3);
        Assert.Equal(7 + distance, p.Near, 5);
        Assert.Equal(3000 + distance, p.Far, 4);
        Assert.Equal(0, Vector3.Transform(new(0, 0, distance - .1f), p.GradientFromWorld).Y, 6);
    }

    [Fact]
    public void TranslatingSourceAndSamplePreservesProjectionAndMovesReceiverVolume()
    {
        var direction = Vector3.Normalize(new Vector3(2, -1, -3));
        var offset = new Vector3(32, -16, 8);
        var first = EnbCurrentRetailShadowProjection.Create(new(1, 2, 3), new(4, 2, 1),
            Matrix4x4.Identity, direction, 64);
        var moved = EnbCurrentRetailShadowProjection.Create(new(1, 2, 3), new(4, 2, 1),
            Matrix4x4.CreateTranslation(offset), direction, 64);
        Assert.True(Vector3.Distance(first.VolumeCenter + offset, moved.VolumeCenter) < .00002f);
        var sample = new Vector3(2, 3, 0);
        Assert.True(Vector3.Distance(Vector3.Transform(sample, first.TextureFromWorld),
            Vector3.Transform(sample + offset, moved.TextureFromWorld)) < .00001f);
    }
}
