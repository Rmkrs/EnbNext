using EnbNext.Engine;
using EnbNext.Presentation;
namespace EnbNext.Net7.Tests;

public sealed class UiLayoutEditorToolsTests
{
    [Theory]
    [InlineData(1d,8)]
    [InlineData(1.5,16)]
    [InlineData(2d,32)]
    [InlineData(2d,64)]
    public void SnapAlignsRenderedTopLeftInPhysicalPixelsWithoutResizing(double uiScale,int spacing)
    {
        var size=new EnbUiSize(2560/uiScale,1440/uiScale);var bounds=new EnbUiRect(117,93,210,80);
        var start=new EnbUiPlacement(.137,.081,.6);
        var snapped=EnbUiLayoutEditor.SnapPlacement(start,bounds,size,spacing/uiScale);
        var before=start.Apply(bounds,size);var after=snapped.Apply(bounds,size);
        Assert.Equal(start.Scale,snapped.Scale);Assert.Equal(before.Width,after.Width);Assert.Equal(before.Height,after.Height);
        Assert.Equal(Math.Round(after.X*uiScale/spacing),after.X*uiScale/spacing,8);
        Assert.Equal(Math.Round(after.Y*uiScale/spacing),after.Y*uiScale/spacing,8);
        Assert.InRange(Math.Abs(after.X-before.X)*uiScale,0,spacing/2d+.000001);
        Assert.InRange(Math.Abs(after.Y-before.Y)*uiScale,0,spacing/2d+.000001);
        Assert.Equal(snapped,EnbUiLayoutEditor.SnapPlacement(snapped,bounds,size,spacing/uiScale));
    }
}
