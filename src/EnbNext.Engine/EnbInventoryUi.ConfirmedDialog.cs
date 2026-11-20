using EnbNext.Net7.Presentation;

namespace EnbNext.Engine;

/// <summary>
/// The server's yes/no question (type-0 offer, current client.exe 0074E710 -> 00588A70 with dialogbox.ini
/// CONFIRMED_ACTION_DIALOG: the server's caption, Option0 Yes / Option1 No, keys Y and N), for example entering a
/// wormhole another pilot opens. 0074F5C0: Yes sends acceptance 1, No acceptance 0, for the oldest queued offer.
/// </summary>
internal sealed partial class EnbInventoryUi
{
    private IReadOnlyDictionary<string, string>? _confirmedActionDialog;
    private Net7ConfirmedActionPending? _answeredConfirmedDialog;
    /// <summary>Answers the open question: character, offer id, accepted.</summary>
    internal Func<uint, uint, bool, Task<bool>>? ConfirmedDialogAnswered { get; set; }
    /// <summary>Next: a question auto pilot answers itself (the wormhole it asked for) is not shown.</summary>
    internal Func<Net7ConfirmedActionPending, bool>? ConfirmedDialogAutomatic { get; set; }

    private Net7ConfirmedActionPending? ShownConfirmedDialog =>
        _scene?.Hud.Chat?.ConfirmedDialog is { } dialog && !ReferenceEquals(dialog, _answeredConfirmedDialog) &&
        ConfirmedDialogAutomatic?.Invoke(dialog) != true ? dialog : null;
    internal bool ConfirmedDialogOpen => ShownConfirmedDialog is not null;

    private void ConfirmedActionDialog()
    {
        if (ShownConfirmedDialog is not { } dialog) return;
        var definition = _confirmedActionDialog;
        if (ModernUi)
        {
            string Option(int i, string fallback) => definition?.GetValueOrDefault("Option" + i, "") is { Length: > 0 } label ? label : fallback;
            ModernQuestion("confirmedAction/Modal", "", dialog.Message, [("confirmedAction/Yes", Option(0, "Yes")), ("confirmedAction/No", Option(1, "No"))], !_busy);
            return;
        }
        Flush(); Hit("confirmedAction/Modal", new(0, 0, 640, 480));
        StandardConfirmationDialog("confirmedAction", dialog.Message, definition, ["Yes", "No"], !_busy);
        Flush();
    }

    private async Task<bool> ActivateConfirmedDialog(string id)
    {
        if (!id.StartsWith("confirmedAction/", StringComparison.Ordinal)) return false;
        if (id is not ("confirmedAction/Yes" or "confirmedAction/No") || ShownConfirmedDialog is not { } dialog || _owner is not uint owner) return true;
        _answeredConfirmedDialog = dialog;
        InteractionSoundRequested?.Invoke("fontclick.wav");
        if (ConfirmedDialogAnswered is { } answer)
            try { await answer(owner, dialog.Id, id == "confirmedAction/Yes"); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException) { }
        return true;
    }

    internal async Task ConfirmedDialogKey(ushort key)
    {
        for (int index = 0; index < 2; index++)
            if (_confirmedActionDialog?.ContainsKey("Option" + index) == true &&
                ParseRetailKey(_confirmedActionDialog.GetValueOrDefault("Key" + index, "")) == key)
            {
                if (!_busy) { await ActivateConfirmedDialog(index == 0 ? "confirmedAction/Yes" : "confirmedAction/No"); ClearHover(); Publish(); }
                return;
            }
    }
}
