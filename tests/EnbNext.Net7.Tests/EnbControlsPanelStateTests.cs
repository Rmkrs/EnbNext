using EnbNext.Assets;
using EnbNext.Client;
using EnbNext.Engine;
using EnbNext.Rendering;
using EnbNext.Presentation;

namespace EnbNext.Net7.Tests;

[Collection(LoadSensitiveCollection.Name)]
public sealed class EnbControlsPanelStateTests
{
    private static EnbControlsPanel Create(out EnbRetailControlOptions draft, out EnbConfirmationState dialog)
    {
        var assets = EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var session = EnbRenderSession.Of(assets);
        draft = new(EnbRetailIniDocument.Load(Path.Combine(assets.Paths.ClientIniDirectory, "control_keys.ini")), assets.ResolveKeyBindings(null));
        draft.Default(false); dialog = new();
        var panel = new EnbControlsPanel(session.Controls, session.OptionsPanels,
            EnbFontEffectPalette.Load(assets.Paths.ClientIniDirectory), dialog);
        panel.Begin(draft); return panel;
    }

    [InstalledRetailFact]
    public void SaveFailureRetainsDraftAndRetryRetiresIt()
    {
        using var panel = Create(out var draft, out _);
        panel.SaveControls = _ => throw new IOException("test failure");
        var failed = panel.Activate("control/BUTTON00");
        Assert.True(failed.UpdateError); Assert.Equal("Could not save controls: test failure", failed.Error);
        Assert.Equal(EnbControlsNavigation.None, failed.Navigation); Assert.Same(draft, panel.Draft);
        int saves = 0; panel.SaveControls = value => { Assert.Same(draft, value); saves++; };
        var saved = panel.Activate("control/BUTTON00");
        Assert.Equal(1, saves); Assert.Equal(EnbControlsNavigation.Main, saved.Navigation);
        Assert.True(saved.UpdateError); Assert.Null(saved.Error); Assert.Null(panel.Draft); Assert.Empty(panel.Rows);
    }

    [InstalledRetailTheory]
    [InlineData("control/BUTTON03", 1)]
    [InlineData("control/EXIT", 2)]
    public void CancelAndExitRetireWithoutSaving(string command, int navigation)
    {
        using var panel = Create(out _, out var dialog);
        panel.SaveControls = _ => throw new Exception("Unexpected save");
        panel.Activate("control/PRIME_KEY00"); Assert.NotNull(panel.Capture.Action);
        Assert.Equal((EnbControlsNavigation)navigation, panel.Activate(command).Navigation);
        Assert.Null(panel.Draft); Assert.Null(panel.Capture.Action); Assert.Null(dialog.Text);
        Assert.Empty(panel.Rows); Assert.Equal(EnbScrollbarPart.None, panel.ScrollInput.Pressed);
    }

    [InstalledRetailFact]
    public void CategoryAndPresetUseRetainedRowsAndNativeCueOrder()
    {
        using var panel = Create(out _, out _);
        var rows = panel.Rows; var sounds = new List<string>(); panel.SoundRequested += sounds.Add;
        panel.SetScroll(5); sounds.Clear(); panel.Activate("control/TOP_BUTTON00");
        Assert.Same(rows, panel.Rows); Assert.Equal(0, panel.ScrollInput.Value);
        Assert.Equal(new[] { "Roll_Select02b.wav", "slider_bar_1.wav" }, sounds);
        panel.Activate("control/BUTTON02"); Assert.Same(rows, panel.Rows);
        panel.Activate("control/TOP_BUTTON01"); Assert.Equal("Activation", panel.Category);
        Assert.All(panel.Rows, row => Assert.Equal("Activation", row.Context)); Assert.NotSame(rows, panel.Rows);
    }

    [InstalledRetailFact]
    public void ModalBlocksCommandsUntilAnswerAndRetirementClearsTransaction()
    {
        using var panel = Create(out var draft, out var dialog);
        panel.Activate("control/PRIME_KEY00");
        panel.Capture.KeyDown(draft, "C", "C", 0, false, false); Assert.NotNull(dialog.Text);
        Assert.Equal(EnbControlsNavigation.None, panel.Activate("control/EXIT").Navigation); Assert.Same(draft, panel.Draft);
        panel.Activate("control/dialog/no"); Assert.Null(dialog.Text); Assert.Null(panel.Capture.Action);
        Assert.Equal("D", draft.Binding("Next Context Target", 1)?.Key);
        panel.Activate("control/PRIME_KEY00"); panel.Capture.KeyDown(draft, "C", "C", 0, false, false);
        panel.Retire(); Assert.Null(dialog.Text); Assert.Null(panel.Capture.Action); Assert.Null(panel.Draft);
    }
}
