using EnbNext.Assets;
using EnbNext.Client;
using EnbNext.Engine;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

[Collection(LoadSensitiveCollection.Name)]
public sealed class EnbControlsNativeInputTests
{
    private sealed class Fixture : IDisposable
    {
        public readonly EnbConfirmationState Confirmation = new();
        public readonly EnbUiInputQueue Queue = new();
        public readonly EnbConfirmationPanel Dialog;
        public readonly EnbControlsPanel Panel;
        public readonly List<string> Sounds = [];
        public long Now;
        public Fixture()
        {
            var assets = EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
            var session = EnbRenderSession.Of(assets); var palette = EnbFontEffectPalette.Load(assets.Paths.ClientIniDirectory);
            Dialog = new(assets, palette); Panel = new(session.Controls, session.OptionsPanels, palette, Confirmation);
            Panel.BindDialog(Dialog); Panel.SoundRequested += Sounds.Add;
            var draft = new EnbRetailControlOptions(EnbRetailIniDocument.Load(Path.Combine(assets.Paths.ClientIniDirectory, "control_keys.ini")), assets.ResolveKeyBindings(null));
            draft.Default(false); Panel.Begin(draft);
        }
        public EnbControlsCommandResult Frame(ushort code = 0, ushort modifiers = 0, EnbUiPoint? point = null)
        {
            if (point is { } p) Queue.SetPosition((float)(p.X / 640), (float)(p.Y / 480));
            if (code != 0) Queue.Push(new(code, modifiers, Queue.X, Queue.Y));
            return Panel.AdvanceInput(Queue, new(1920, 1080), 1, Now++);
        }
        public void Click(string key)
        { Frame(1, 0, Panel.Design(key).Center); Frame(1, 0x1000); }
        public void Dispose() { Panel.Dispose(); Dialog.Dispose(); }
    }

    [Fact]
    public void ClickingBindingPreviewsAndCommitsOnlyOnKeyRelease()
    {
        using var f = new Fixture(); string action = f.Panel.Rows[0].Action;
        var before = f.Panel.Draft!.Binding(action, 1);
        f.Click("control/PRIME_KEY00"); Assert.True(f.Panel.Keyboard.Enabled); Assert.Equal(action, f.Panel.Capture.Action);
        ushort code = Enumerable.Range(0x41, 26).Select(i => (ushort)i).First(c =>
            f.Panel.Draft.Conflict(action, new(action, EnbRetailKeyIdentity.Name(c), EnbKeyModifiers.Control | EnbKeyModifiers.Shift)) is null);
        f.Frame(code, 0x300); Assert.Equal(before, f.Panel.Draft.Binding(action, 1));
        Assert.Equal("Ctrl-Shift-" + EnbRetailKeyIdentity.Name(code), f.Panel.Keyboard.Preview);
        f.Frame(code, 0x1300); Assert.False(f.Panel.Keyboard.Enabled); Assert.Null(f.Panel.Capture.Action);
        Assert.Equal(code, f.Panel.Draft.Binding(action, 1)!.Code); Assert.Null(f.Confirmation.Text);
    }

    [Fact]
    public void WheelUsesPanelBoundsAndModifiedWheelDoesNotDispatchParentCommand()
    {
        using var f = new Fixture(); Assert.True(f.Panel.Rows.Length > 11);
        f.Click("control/PRIME_KEY00"); string? action = f.Panel.Capture.Action;
        // 0042EEC0 retains the first press modifiers even for wheel identities.
        // Use the first wheel event to test the modified command boundary.
        var point = f.Panel.Resources.WheelBounds!.Value.Center;
        f.Frame(0x102, 0x100, new(0, 0)); Assert.Equal(0, f.Panel.ScrollInput.Value); Assert.Equal(action, f.Panel.Capture.Action);
        f.Frame(0x102, 0x100, point); Assert.Equal(1, f.Panel.ScrollInput.Value); Assert.Equal(action, f.Panel.Capture.Action);
        f.Frame(0x102, 0, point); Assert.Equal(2, f.Panel.ScrollInput.Value); Assert.Equal(action, f.Panel.Capture.Action);
        // The opposite wheel identity has no retained modifiers yet.
        f.Frame(0x101, 0, point);
        Assert.Equal(1, f.Panel.ScrollInput.Value); Assert.Null(f.Panel.Capture.Action); Assert.False(f.Panel.Keyboard.Enabled);
    }

    [Fact]
    public void RightSliderCaptureRepeatsThenReleasesOutside()
    {
        using var f = new Fixture(); var point = f.Panel.Resources.Down.Center;
        f.Frame(2, 0, point); Assert.Equal(4, f.Panel.Pointer.RightCapture); Assert.Equal(1, f.Panel.ScrollInput.Value);
        f.Now = 499; f.Frame(); Assert.Equal(1, f.Panel.ScrollInput.Value);
        f.Now = 500; f.Frame(); Assert.Equal(2, f.Panel.ScrollInput.Value);
        f.Frame(2, 0x1000, new(0, 0)); Assert.Equal(-1, f.Panel.Pointer.RightCapture);
        Assert.Equal(EnbScrollbarPart.None, f.Panel.ScrollInput.Pressed);
    }

    [Fact]
    public void ModalBlocksParentCommandsAndAnswersThroughSharedCapture()
    {
        using var f = new Fixture(); f.Confirmation.Show("Keep this mapping?", false);
        f.Frame(); string category = f.Panel.Category; f.Click("control/TOP_BUTTON01");
        Assert.Equal(category, f.Panel.Category); Assert.Empty(f.Sounds);
        f.Frame(1, 0, f.Dialog.Bounds[3].Center); Assert.Null(f.Confirmation.Text);
        Assert.Equal(-1, f.Panel.Pointer.LeftCapture); Assert.Single(f.Sounds); Assert.Equal("Roll_Select02b.wav", f.Sounds[0]);
        f.Frame(1, 0x1000); f.Click("control/TOP_BUTTON01"); Assert.Equal("Activation", f.Panel.Category);
    }

    [Fact]
    public void ModifierOnlyCaptureUsesActualHeldStateAndRetirementClearsEverything()
    {
        using var f = new Fixture(); f.Click("control/PRIME_KEY00");
        f.Frame(0x11, 0x200); Assert.Equal("Ctrl- ", f.Panel.Keyboard.Preview);
        f.Frame(0x11, 0x1000); Assert.True(f.Confirmation.Notice); Assert.Null(f.Panel.Capture.Action);
        f.Panel.Retire(); Assert.Null(f.Confirmation.Text); Assert.False(f.Panel.Keyboard.Enabled);
        Assert.Equal(-1, f.Panel.Pointer.LeftCapture); Assert.Equal(-1, f.Panel.Pointer.RightCapture);
        Assert.Empty(f.Panel.DrawFrame(new(1920, 1080), 1, f.Now));
    }
}
