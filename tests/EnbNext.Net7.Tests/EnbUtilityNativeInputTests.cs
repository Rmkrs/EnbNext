using System.Collections.Immutable;
using EnbNext.Assets;
using EnbNext.Engine;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

[Collection(LoadSensitiveCollection.Name)]
public sealed class EnbUtilityNativeInputTests
{
    [Fact]
    public void AuthoredUtilityQueriesPreserveMasksCaptureAndTooltipOnlyBars()
    {
        var assets = EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var panel = new EnbUtilityHudPanel(EnbRenderSession.Of(assets).UtilityHud);
        var queue = new EnbUiInputQueue();
        string? Step(EnbUiPoint p, ushort code = 0, ushort modifiers = 0)
        {
            float x = (float)(p.X / 640), y = (float)(p.Y / 480); queue.SetPosition(x, y);
            if (code != 0) queue.Push(new(code, modifiers, x, y));
            return panel.AdvanceInput(queue);
        }
        for (int i = 0; i < 100; i++) Step(new(0, 0));
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 4096; i++) Step(new(0, 0));
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - bytes);
        for (int index = 3; index <= 6; index++) foreach (ushort button in new ushort[] { 1, 2 })
        {
            string key = EnbUtilityHudPanel.InputKey(index)!; var center = panel.Design(key).Center;
            Step(center); Step(center); Assert.Equal(index, panel.Pointer.TooltipGadget); Assert.True(panel.Pointer.Flash(index));
            Assert.Equal(key, Step(center, button)); Assert.True(panel.Pointer.Selected(index));
            Assert.Equal(index, button == 1 ? panel.Pointer.LeftCapture : panel.Pointer.RightCapture);
            Assert.Null(Step(new(0, 0))); Assert.True(panel.Pointer.Selected(index));
            Assert.Null(Step(new(0, 0), button, 0x1000)); Assert.False(panel.Pointer.Selected(index));
            Assert.Equal(-1, panel.Pointer.LeftCapture); Assert.Equal(-1, panel.Pointer.RightCapture);
            Assert.Null(Step(center, button, 0x200)); Assert.True(panel.Pointer.Selected(index));
            Step(center, button, 0x1200); Assert.False(panel.Pointer.Selected(index));
            panel.CancelInput(); queue.Clear();
        }
        for (int index = 0; index < 3; index++)
        {
            var point = panel.Design(EnbUtilityHudPanel.InputKey(index)!).Center;
            Step(point); Step(point); Assert.Equal(index, panel.Pointer.TooltipGadget); Assert.False(panel.Pointer.Flash(index));
            Assert.Null(Step(point, 1)); Assert.Null(Step(point, 1, 0x1000));
            Assert.Null(Step(point, 2)); Assert.Null(Step(point, 2, 0x1000));
            Assert.Null(Step(point, 0x101)); Assert.False(panel.Pointer.Selected(index)); Assert.False(panel.Pointer.Flash(index));
            Assert.Equal(-1, panel.Pointer.LeftCapture); Assert.Equal(-1, panel.Pointer.RightCapture);
        }
        Step(panel.Design("hud/MAP_BUT").Center, 1); panel.CancelInput(); queue.Clear();
        Assert.Equal(-1, panel.Pointer.LeftCapture); Assert.Equal(-1, panel.Pointer.TooltipGadget);
        var draws = ImmutableList.CreateBuilder<EnbUiModelDraw>(); panel.DrawFrame(draws, new(800, 600), 1, new(0, .5f, 1));
        Assert.All(draws, d => { Assert.False(d.Hovered); Assert.Null(d.EmissiveOverride); });
    }
}
