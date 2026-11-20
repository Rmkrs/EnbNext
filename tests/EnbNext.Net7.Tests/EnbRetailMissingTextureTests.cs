using System.Reflection;
using EnbNext.Assets;
using EnbNext.Rendering;
using Xunit;

namespace EnbNext.Net7.Tests;

public sealed class EnbRetailMissingTextureTests
{
    private delegate EnbTextureImage DecodeRuns(int width, int height, ReadOnlySpan<byte> runs, ReadOnlySpan<byte> colors);
    private static readonly DecodeRuns Decode = typeof(EnbRetailMissingTexture)
        .GetMethod("Decode", BindingFlags.Static | BindingFlags.NonPublic)!.CreateDelegate<DecodeRuns>();

    [Fact]
    public void RunsRetainArgbAndRetailRowOrderAndGenerateRecursiveMips()
    {
        // One four-pixel run per source row, with distinct BGRA channels.
        var image = Decode(4, 4, [3, 3, 3, 3],
            [0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120, 130, 140, 150]);
        Assert.Equal(new EnbColor32(20, 10, 0, 30), image.GetPixel(0, 0));
        Assert.Equal(new EnbColor32(140, 130, 120, 150), image.GetPixel(3, 1));
        Assert.Equal(new EnbColor32(100, 90, 80, 110), image.GetPixel(0, 2));
        Assert.Equal(new EnbColor32(60, 50, 40, 70), image.GetPixel(3, 3));
        Assert.Equal(3, image.MipLevels.Count);
        Assert.Equal(new EnbColor32(80, 70, 60, 90), image.MipLevels[1].GetPixel(0, 0));
        Assert.Equal(new EnbColor32(80, 70, 60, 90), image.MipLevels[2].GetPixel(0, 0));
    }

    [Fact]
    public void MissingTextureDoesNotUndergoAuthoredTextureReduction()
    {
        var image = Decode(64, 64, Enumerable.Repeat((byte)255, 16).ToArray(), new byte[64]);
        var plan = EnbTextureUploadMipPlanner.Compile(image, EnbRenderTextureRuntimeSettings.FromRetailValues(2, 0));
        Assert.Equal(64, plan.Width);
        Assert.False(plan.UsesReducedBase);
        Assert.Equal(7, plan.UploadMipCount);
    }

    [Fact]
    public void InvalidRunTablesFailInsteadOfProducingPartialPixels()
    {
        Assert.Throws<InvalidDataException>(() => Decode(2, 2, [4], [0, 0, 0, 0]));
        Assert.Throws<InvalidDataException>(() => Decode(2, 2, [2], [0, 0, 0, 0]));
        Assert.Throws<InvalidDataException>(() => Decode(2, 2, [3], [0]));
    }
}
