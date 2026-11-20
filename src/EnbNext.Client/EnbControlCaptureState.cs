using EnbNext.Assets;

namespace EnbNext.Client;

/// <summary>005CB000 capture/confirmation transaction; 0071A860 owns assignment.</summary>
public sealed class EnbControlCaptureState(EnbConfirmationState dialog)
{
    public string? Action { get; private set; }
    public int Slot { get; private set; }
    private EnbRetailKeyBinding? candidate;
    private bool modifierOnly;

    public void Reset()
    {
        Action = null; Slot = 0; candidate = null; modifierOnly = false; dialog.Clear();
    }

    public void Begin(string action, int slot) { Reset(); Action = action; Slot = slot; }

    public void Answer(EnbRetailControlOptions draft, bool yes)
    {
        if (dialog.Text is null) return;
        if (yes && !dialog.Notice && Action is { } action) draft.Assign(action, Slot, candidate);
        Reset();
    }

    public bool KeyDown(EnbRetailControlOptions draft, string? key, string display,
        EnbKeyModifiers modifiers, bool isModifier, bool forbidden)
        => HandleKey(draft, key, display, modifiers, isModifier, forbidden, null);

    public bool Complete(EnbRetailControlOptions draft, ushort code, ushort nativeModifiers)
    {
        if (dialog.Text is not null) return true;
        if (Action is null) return false;
        if (code == 0)
        {
            Notice("Ctrl, Alt, and Shift cannot be used alone.  They can be used as modifiers to other keys.");
            return true;
        }
        var modifiers = ((nativeModifiers & 0x100) != 0 ? EnbKeyModifiers.Shift : 0) |
            ((nativeModifiers & 0x200) != 0 ? EnbKeyModifiers.Control : 0) |
            ((nativeModifiers & 0x400) != 0 ? EnbKeyModifiers.Alt : 0);
        string name = EnbRetailKeyIdentity.Name(code);
        return HandleKey(draft, name, EnbRetailKeyIdentity.Display(code, nativeModifiers), modifiers,
            false, code is 0x2c or 0x5b or 0x5c, code);
    }

    private bool HandleKey(EnbRetailControlOptions draft, string? key, string display,
        EnbKeyModifiers modifiers, bool isModifier, bool forbidden, ushort? capturedCode)
    {
        if (dialog.Text is not null) return true;
        if (Action is not { } action) return false;
        if (isModifier) { modifierOnly = true; return true; }
        modifierOnly = false;
        var definition = draft.Definitions.First(d => d.Action == action);
        if (key == "Esc" && modifiers == 0)
        {
            var old = draft.Binding(action, Slot);
            if (old is null) Notice("Esc is not allowed as a hot key.");
            else { candidate = null; dialog.Show($"Do you wish to clear {old.Display} from {definition.Text}?", false, wrapWidth: 320); }
        }
        else if (key is null || forbidden ||
            modifiers == EnbKeyModifiers.Alt && key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]))
            Notice($"{(capturedCode is not null ? display : key ?? display)} is not allowed as a hot key.");
        else
        {
            candidate = new(action, key, modifiers) { Slot = Slot, CapturedCode = capturedCode };
            if (draft.Conflict(action, candidate) is { } conflict)
            {
                string other = draft.Definitions.First(d => d.Action == conflict.Action).Text;
                dialog.Show($"{candidate.Display} is already tied to:\n{other}\nDo you want to change it to:\n{definition.Text}?", false, wrapWidth: 320);
            }
            else { draft.Assign(action, Slot, candidate); Reset(); }
        }
        return true;
    }

    public bool ModifierReleased()
    {
        if (Action is null || dialog.Text is not null || !modifierOnly) return false;
        Notice("Ctrl, Alt, and Shift cannot be used alone.  They can be used as modifiers to other keys.");
        return true;
    }

    private void Notice(string text)
    {
        Reset(); dialog.Show(text, true, wrapWidth: 320);
    }
}
