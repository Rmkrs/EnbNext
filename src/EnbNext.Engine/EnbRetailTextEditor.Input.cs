using EnbNext.Presentation;

namespace EnbNext.Engine;

[Flags]
public enum EnbRetailEditorKeyResult { None = 0, Consumed = 1, Tab = 2, Escape = 4 }

public sealed partial class EnbRetailTextEditor
{
    public EnbRetailTextEditor? TabTarget { get; set; }

    // 006AA720/00673150: placeholder retirement belongs to activation, not hit position.
    public void Activate()
    {
        if (IsFocused) return;
        if (!Entered) Text = "";
        IsFocused = true;
    }

    // 006AA200 and the keyboard branch of00672120. The parent consumes Tab/Escape;
    // OS character translation and the global quit command remain outside this owner.
    public EnbRetailEditorKeyResult HandleKey(ushort key, ushort modifiers = 0, char translated = '\0')
    {
        if (!IsFocused) return EnbRetailEditorKeyResult.None;
        const EnbRetailEditorKeyResult consumed = EnbRetailEditorKeyResult.Consumed;
        if ((modifiers & 0x1000) != 0) return consumed;
        if (key == 27) { IsFocused = false; return consumed | EnbRetailEditorKeyResult.Escape; }
        if (!Multiline && key is 9 or 13)
        {
            IsFocused = false;
            if (key == 9 && TabTarget is { } next) { next.Activate(); return consumed; }
            return key == 9 ? consumed | EnbRetailEditorKeyResult.Tab : consumed;
        }
        if (Multiline && HasLayout && Wrapped is not null && key is 33 or 34 or 35 or 36 or 38 or 40)
        {
            Move(key switch
            {
                33 => EnbRetailDialogText.EditorMove.PageUp, 34 => EnbRetailDialogText.EditorMove.PageDown,
                35 => EnbRetailDialogText.EditorMove.End, 36 => EnbRetailDialogText.EditorMove.Home,
                38 => EnbRetailDialogText.EditorMove.Up, _ => EnbRetailDialogText.EditorMove.Down
            });
        }
        else Key(key, modifiers, translated);
        return consumed;
    }
}
