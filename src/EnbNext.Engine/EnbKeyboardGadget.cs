using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Engine;

/// <summary>0066A880/0066A9C0: one owner for key preview and release-time completion.</summary>
internal sealed class EnbKeyboardGadget
{
    public bool Enabled { get; private set; }
    public ushort Code { get; private set; }
    public ushort Modifiers { get; private set; }
    private ushort previousModifiers;
    private ushort previewCode, previewModifiers;
    public string Preview { get; private set; } = "????";
    private void RefreshPreview()
    {
        if (previewCode == Code && previewModifiers == Modifiers) return;
        previewCode = Code; previewModifiers = Modifiers;
        Preview = Code == 0 && Modifiers == 0 ? "????" : EnbRetailKeyIdentity.Display(Code, Modifiers);
    }

    public void Begin() { Cancel(); Enabled = true; }
    public void Cancel() { Enabled = false; Code = Modifiers = previousModifiers = 0; RefreshPreview(); }

    public bool Handle(ref EnbUiPointerFrame frame, uint flags, ushort heldModifiers)
    {
        if (!Enabled) return false;
        if ((flags & 0x100) != 0)
        {
            Code = frame.Code; Modifiers = frame.Modifiers;
            if ((Modifiers & 0x1000) != 0) { Modifiers ^= 0x1000; Enabled = false; }
            RefreshPreview();
            frame = frame with { Code = 0, Modifiers = 0 };
            return true;
        }
        if (Code == 0) Modifiers = (ushort)(heldModifiers & 0x700);
        if (previousModifiers != 0 && Modifiers == 0 && Code == 0)
        { Enabled = false; RefreshPreview(); return false; }
        RefreshPreview(); previousModifiers = Modifiers;
        return false;
    }
}
