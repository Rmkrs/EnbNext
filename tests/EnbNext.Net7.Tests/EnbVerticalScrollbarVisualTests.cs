using EnbNext.Assets;
using EnbNext.Engine;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

[Collection(LoadSensitiveCollection.Name)]
public sealed class EnbVerticalScrollbarVisualTests
{
    [Fact]
    public void InstalledThumbRetainsUnchangedGeometryAndDoesNotMutateAnotherSlider()
    {
        var paths = EnbInstalledAssetLocator.TryLocate(); Assert.NotNull(paths);
        var assets = EnbRetailAssetBundle.Load(paths);
        var resources = EnbRenderSession.Of(assets).VerticalScrollbar;
        Assert.Same(resources, EnbRenderSession.Of(assets).VerticalScrollbar);
        var controls = new EnbVerticalScrollbarVisual(resources);
        var information = new EnbVerticalScrollbarVisual(resources);
        controls.Resize(79.75); var retained = controls.Thumb; var bounds = controls.ThumbBounds;
        foreach (double height in new[] { 0d, 16d, 16.5d, 32d, 128.25d })
        {
            information.Resize(height); var thumb = information.Thumb;
            information.Resize(height); Assert.Same(thumb, information.Thumb);
            Assert.Same(retained, controls.Thumb); Assert.Equal(bounds, controls.ThumbBounds);
            Assert.Same(resources.Thumb.Resource, thumb.Resource);
            Assert.NotNull(thumb.Geometry);
        }
        information.Resize(79.75);
        Assert.True(retained.Geometry!.Positions.SequenceEqual(information.Thumb.Geometry!.Positions));
        Assert.True(retained.Geometry.Normals.SequenceEqual(information.Thumb.Geometry.Normals));
        Assert.Equal(bounds, information.ThumbBounds);
        controls.Resize(79.75); Assert.Same(retained, controls.Thumb);
    }

    [Fact]
    public void SizesBelowNativeMinimumUseAnimationFrameZero()
    {
        var paths = EnbInstalledAssetLocator.TryLocate(); Assert.NotNull(paths);
        var slider = new EnbVerticalScrollbarVisual(EnbRenderSession.Of(EnbRetailAssetBundle.Load(paths)).VerticalScrollbar);
        slider.Resize(16); var minimum = slider.Thumb.Geometry!;
        slider.Resize(0); Assert.True(minimum.Positions.SequenceEqual(slider.Thumb.Geometry!.Positions));
        slider.Resize(16.5); Assert.False(minimum.Positions.SequenceEqual(slider.Thumb.Geometry!.Positions));
        slider.Resize(16); Assert.True(minimum.Positions.SequenceEqual(slider.Thumb.Geometry!.Positions));
    }
}
