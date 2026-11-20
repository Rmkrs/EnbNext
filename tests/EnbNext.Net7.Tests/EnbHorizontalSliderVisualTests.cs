using EnbNext.Assets;
using EnbNext.Engine;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

[Collection(LoadSensitiveCollection.Name)]
public sealed class EnbHorizontalSliderVisualTests
{
    [InstalledRetailFact]
    public void ResizingOneRowPreservesOtherRowsAndTheSharedSource()
    {
        var paths = EnbInstalledAssetLocator.TryLocate(); Assert.NotNull(paths);
        var resources = EnbRenderSession.Of(EnbRetailAssetBundle.Load(paths)).HorizontalSlider;
        var first = new EnbHorizontalSliderVisual(resources); var second = new EnbHorizontalSliderVisual(resources);
        var original = resources.Thumb; var originalBounds = resources.ThumbBounds;
        first.Resize(69); var retained = first.Asset; var bounds = first.Bounds;
        foreach (double length in new[] { 16d, 16.5d, 32d, 46d, 128.25d })
        {
            second.Resize(length); var asset = second.Asset; second.Resize(length);
            Assert.Same(asset, second.Asset); Assert.Same(retained, first.Asset); Assert.Equal(bounds, first.Bounds);
            Assert.Same(original, resources.Thumb); Assert.Equal(originalBounds, resources.ThumbBounds);
            Assert.Same(original.Resource, asset.Resource);
        }
        second.Resize(69);
        Assert.True(retained.Geometry!.Positions.SequenceEqual(second.Asset.Geometry!.Positions));
        Assert.True(retained.Geometry.Normals.SequenceEqual(second.Asset.Geometry.Normals));
        Assert.Equal(bounds, second.Bounds);
        first.Resize(16); Assert.Same(original, first.Asset); Assert.Equal(originalBounds, first.Bounds);
    }

    [InstalledRetailFact]
    public void MinimumAndUnchangedLengthsRetainGeometryWithoutAllocating()
    {
        var paths = EnbInstalledAssetLocator.TryLocate(); Assert.NotNull(paths);
        var resources = EnbRenderSession.Of(EnbRetailAssetBundle.Load(paths)).HorizontalSlider;
        var slider = new EnbHorizontalSliderVisual(resources);
        slider.Resize(0); Assert.Same(resources.Thumb, slider.Asset);
        slider.Resize(16.5); Assert.False(resources.Thumb.Geometry!.Positions.SequenceEqual(slider.Asset.Geometry!.Positions));
        var retained = slider.Asset;
        for (int i=0;i<100;i++) slider.Resize(16.5);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i=0;i<4096;i++) slider.Resize(16.5);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread()-before); Assert.Same(retained, slider.Asset);
    }
}
