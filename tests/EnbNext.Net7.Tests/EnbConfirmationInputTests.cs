using EnbNext.Assets;
using EnbNext.Client;
using EnbNext.Engine;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

[Collection(LoadSensitiveCollection.Name)]
public sealed class EnbConfirmationInputTests
{
    private sealed class Handler : IEnbUiGadgetHandler
    {
        public bool HasFlash(int index) => index >= 3;
        public bool TryHandle(int index, ref EnbUiPointerFrame frame, uint flags, out EnbUiGadgetResponse response)
        { response = default; return false; }
    }

    [Theory]
    [InlineData("", 0, 0)]
    [InlineData("$^@", 0, 0)]
    [InlineData("^Return", 13, 512)]
    [InlineData("Esc", 27, 0)]
    [InlineData("$Y", 89, 1024)]
    [InlineData("~", 192, 1024)]
    [InlineData("Ext Del", 46, 1024)]
    [InlineData("Del", 46, 1024)]
    [InlineData("y", 0, 1024)]
    public void AuthoredKeysKeepNativeIdentityAndDialogOverride(string key, int code, int modifiers)
    {
        var actual = EnbAuthoredKey.Dialog(key);
        Assert.Equal(code, actual.Code); Assert.Equal(modifiers, actual.Modifiers);
    }

    [Fact]
    public void DynamicQueryResetsBrightnessBeforeSelectionTransitions()
    {
        var gadget = new EnbDynamicWindowGadget(true);
        var bounds = new EnbUiRect(160, 120, 320, 240);
        Assert.Equal(.6f, gadget.Emissive);
        Assert.True(gadget.Query(bounds, .25f, .25f)); Assert.Equal(1, gadget.Emissive);
        gadget.SelectionChanged(false, true); Assert.Equal(.35f, gadget.Emissive);
        Assert.True(gadget.Query(bounds, .75f, .75f));
        gadget.SelectionChanged(true, true); Assert.Equal(1, gadget.Emissive);
        Assert.False(gadget.Query(bounds, .8f, .8f)); Assert.Equal(.6f, gadget.Emissive);
        gadget.SelectionChanged(true, false); Assert.Equal(1, gadget.Emissive);
    }

    [Theory]
    [InlineData(1d)]
    [InlineData(1.5)]
    [InlineData(2d)]
    public void EnlargedDialogLabelsStayCenteredInsideAuthoredFrames(double scale)
    {
        var paths=EnbInstalledAssetLocator.TryLocate();if(paths is null)return;
        var assets=EnbRetailAssetBundle.Load(paths);
        using var panel=new EnbConfirmationPanel(assets,EnbFontEffectPalette.Load(assets.Paths.ClientIniDirectory));
        var state=new EnbConfirmationState();state.Show("Delete profile?\nTest profile",false);
        var draws=panel.DrawFrame(state,new(2560/scale,1440/scale),scale);var labels=draws[^1].Images;
        Assert.Equal(draws[0].Bounds.Center.X,labels[0].Bounds.Center.X,6);
        Assert.Equal(draws[2].Bounds.Center.X,labels[2].Bounds.Center.X,6);
        Assert.Equal(draws[3].Bounds.Center.X,labels[3].Bounds.Center.X,6);
        Assert.All(labels,label=>{Assert.True(label.Bounds.Left>=draws[0].Bounds.Left);Assert.True(label.Bounds.Right<=draws[0].Bounds.Right);});
    }

    [InstalledRetailFact]
    public void DialogChildrenShareCaptureAndRetireWithoutClearingParent()
    {
        var assets = EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        using var panel = new EnbConfirmationPanel(assets, EnbFontEffectPalette.Load(assets.Paths.ClientIniDirectory));
        var pointer = new EnbUiGadgetPointer(4, custom: new Handler()); panel.BindInput(pointer, 0);
        var state = new EnbConfirmationState(); var size = new EnbUiSize(1920, 1080);
        state.Show("Clear current mapping?", false); Assert.True(panel.Prepare(state, size, 1));
        bool[] hits = new bool[5]; int cues = 0; panel.SoundRequested += _ => cues++;
        var yes = panel.Bounds[2].Center;
        panel.PrepareInput(state, size, 1, (float)(yes.X / 640), (float)(yes.Y / 480), hits.AsSpan(0, 3));
        var press = new EnbUiPointerFrame(1, 0, true, false);
        Assert.Equal(1, panel.CompleteInput(pointer.Dispatch(ref press, hits), press));
        Assert.Equal(1, pointer.LeftCapture); Assert.False(pointer.Flash(1)); Assert.Equal(1, cues);
        var draws = panel.DrawFrame(state, size, 1);
        Assert.Equal(.35f, draws[2].EmissiveOverride!.Value.X);
        state.Clear(); Assert.False(panel.Prepare(state, size, 1));
        Assert.Equal(-1, pointer.LeftCapture); Assert.False(pointer.Selected(1));
        state.Show("Another dialog", false); Assert.True(panel.Prepare(state, size, 1));
        // Retiring dialog children must preserve a capture belonging to the parent.
        hits.AsSpan().Clear(); hits[3] = true;
        pointer.Dispatch(new(2, 0, false, true), hits);
        Assert.Equal(3, pointer.RightCapture);
        state.Clear(); Assert.False(panel.Prepare(state, size, 1));
        Assert.Equal(-1, pointer.LeftCapture); Assert.Equal(3, pointer.RightCapture);
        Assert.False(pointer.Selected(1)); Assert.True(pointer.Selected(3));
        Assert.All(panel.Bounds, bounds => Assert.Equal(default, bounds));
    }
}
