using EnbNext.Assets;
using EnbNext.Client;

namespace EnbNext.Net7.Tests;

public sealed class EnbControlCaptureStateTests
{
    private static EnbRetailControlOptions Draft()
    {
        var ini = EnbRetailIniDocument.Parse("""
            [First]
            Text=First action
            Context=Targeting
            PSW_1=D
            PSW_2=
            [Second]
            Text=Second action
            Context=Targeting
            PSW_1=C
            PSW_2=E
            """);
        return new(ini, EnbRetailKeyBindings.Resolve(ini, EnbRetailIniDocument.Parse(""), null));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ConflictWaitsForAnswerAndTransfersOnlyOnYes(int slot)
    {
        var draft = Draft(); var dialog = new EnbConfirmationState(); var capture = new EnbControlCaptureState(dialog);
        capture.Begin("First", slot); var before = draft.Binding("First", slot);
        Assert.True(capture.KeyDown(draft, "C", "C", 0, false, false));
        Assert.Equal(before, draft.Binding("First", slot)); Assert.False(dialog.Notice);
        Assert.Equal("C is already tied to:\nSecond action\nDo you want to change it to:\nFirst action?", dialog.Text);
        Assert.Equal("YES_NO", dialog.Definition); Assert.Equal(320f, dialog.WrapWidth);
        capture.Answer(draft, false); Assert.Equal(before, draft.Binding("First", slot)); Assert.Null(capture.Action);
        capture.Begin("First", slot); capture.KeyDown(draft, "C", "C", 0, false, false); capture.Answer(draft, true);
        Assert.Equal("C", draft.Binding("First", slot)?.Key); Assert.Null(draft.Binding("Second", 1));
        Assert.Null(dialog.Text); Assert.Null(capture.Action);
    }

    [Fact]
    public void EscapeRequestsClearAndEmptySlotUsesNativeInvalidKeyNotice()
    {
        var draft = Draft(); var dialog = new EnbConfirmationState(); var capture = new EnbControlCaptureState(dialog);
        capture.Begin("First", 1); capture.KeyDown(draft, "Esc", "Escape", 0, false, false);
        Assert.Equal("Do you wish to clear D from First action?", dialog.Text); Assert.Equal("First", capture.Action);
        capture.Answer(draft, true); Assert.Null(draft.Binding("First", 1));
        capture.Begin("First", 1); capture.KeyDown(draft, "Esc", "Escape", 0, false, false);
        Assert.Equal("Esc is not allowed as a hot key.", dialog.Text); Assert.True(dialog.Notice); Assert.Null(capture.Action);
        Assert.Equal("OK", dialog.Definition); Assert.Equal(320f, dialog.WrapWidth);
        capture.Answer(draft, true); Assert.Null(draft.Binding("First", 1)); Assert.Null(dialog.Text);
    }

    [Theory]
    [InlineData("A", "A", EnbKeyModifiers.Alt, false)]
    [InlineData("7", "7", EnbKeyModifiers.Alt, false)]
    [InlineData(null, "LWin", EnbKeyModifiers.None, true)]
    [InlineData(null, "PrintScreen", EnbKeyModifiers.None, true)]
    public void InvalidNoticeRetiresCaptureWithoutChangingDraft(string? key, string display, EnbKeyModifiers modifiers, bool forbidden)
    {
        var draft = Draft(); var dialog = new EnbConfirmationState(); var capture = new EnbControlCaptureState(dialog);
        capture.Begin("First", 1); capture.KeyDown(draft, key, display, modifiers, false, forbidden);
        Assert.Equal($"{key ?? display} is not allowed as a hot key.", dialog.Text);
        Assert.True(dialog.Notice); Assert.Null(capture.Action); Assert.Equal("D", draft.Binding("First", 1)?.Key);
        capture.KeyDown(draft, "F12", "F12", 0, false, false);
        capture.Answer(draft, true); Assert.Equal("D", draft.Binding("First", 1)?.Key);
    }

    [Theory]
    [InlineData("F12", EnbKeyModifiers.Alt)]
    [InlineData("A", EnbKeyModifiers.Alt | EnbKeyModifiers.Control)]
    [InlineData("M Mouse", EnbKeyModifiers.None)]
    [InlineData("Keypad Enter", EnbKeyModifiers.Control)]
    public void LegalUnusedChordAssignsDirectlyToAlternate(string key, EnbKeyModifiers modifiers)
    {
        var draft = Draft(); var dialog = new EnbConfirmationState(); var capture = new EnbControlCaptureState(dialog);
        capture.Begin("First", 2); capture.KeyDown(draft, key, key, modifiers, false, false);
        Assert.Null(dialog.Text); Assert.Null(capture.Action); Assert.Equal("D", draft.Binding("First", 1)?.Key);
        Assert.Equal(key, draft.Binding("First", 2)?.Key); Assert.Equal(modifiers, draft.Binding("First", 2)?.Modifiers);
    }

    [Fact]
    public void ModifierReleaseWarnsOnlyWhenNoChordWasCapturedAndResetDiscardsPendingAnswer()
    {
        var draft = Draft(); var dialog = new EnbConfirmationState(); var capture = new EnbControlCaptureState(dialog);
        capture.Begin("First", 2); capture.KeyDown(draft, null, "LeftCtrl", 0, true, false);
        Assert.True(capture.ModifierReleased()); Assert.True(dialog.Notice); Assert.Null(capture.Action);
        capture.Answer(draft, true); capture.Begin("First", 2);
        capture.KeyDown(draft, null, "LeftCtrl", 0, true, false);
        capture.KeyDown(draft, "F12", "F12", EnbKeyModifiers.Control, false, false);
        Assert.False(capture.ModifierReleased()); Assert.Null(dialog.Text);
        capture.Begin("First", 1); capture.KeyDown(draft, "C", "C", 0, false, false);
        capture.Reset(); capture.Answer(draft, true); Assert.Equal("D", draft.Binding("First", 1)?.Key);
        ulong lifetime = dialog.Lifetime;
        dialog.Show("Authored petition prompt", false, "PETITION_ENTRY_CANCEL"); Assert.Equal("Authored petition prompt", dialog.Text);
        Assert.Equal("PETITION_ENTRY_CANCEL", dialog.Definition); Assert.Null(dialog.WrapWidth); Assert.True(dialog.Lifetime > lifetime);
        dialog.Clear(); Assert.Null(dialog.Text); Assert.Null(dialog.WrapWidth);
    }
}
