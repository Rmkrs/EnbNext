using EnbNext.Presentation;
namespace EnbNext.Engine;

// The text gadget owns its active buffer, visibility, focus and authored bounds.
// A host may deliver native focus; an engine-only host activates it directly.
internal sealed class EnbUiTextInput(EnbRetailTextEditor editor)
{
    public EnbRetailTextEditor Editor { get; private set; } = editor;
    public event EventHandler? Changed, GotFocus, LostFocus;
    public event Action<EnbUiTextInput, EnbUiInputEvent, Action?>? KeyDown;
    public Action? TransportChanged;
    public Func<bool>? FocusRequested;
    private bool visible,enabled=true;
    public bool IsEnabled {get=>enabled;set{if(enabled==value)return;enabled=value;TransportChanged?.Invoke();if(!value)SetFocused(false);}}
    private EnbUiRect bounds;
    private ushort modifiers;
    public bool IsVisible { get => visible; set { visible=value; TransportChanged?.Invoke(); if(!visible)SetFocused(false); } }
    public EnbUiRect Bounds { get => bounds; set { bounds=value; TransportChanged?.Invoke(); } }
    public bool IsFocused => Editor.IsFocused;
    public char PasswordChar { get; set; }
    public int MaxLength => Editor.MaxLength;
    public string Text { get => Editor.Text; set { Editor.Text=value; Changed?.Invoke(this,EventArgs.Empty); } }
    public int CaretIndex { get => Editor.CaretIndex; set { Editor.CaretIndex=value; Changed?.Invoke(this,EventArgs.Empty); } }
    public void Use(EnbRetailTextEditor next)
    {
        if(ReferenceEquals(Editor,next))return;
        bool focused=IsFocused;Editor.IsFocused=false;Editor=next;Editor.IsFocused=focused;
    }
    public bool Focus()
    {
        return IsVisible && FocusRequested is {} focus && focus();
    }
    public void SetFocused(bool focused)
    {
        if(IsFocused==focused)return;
        Editor.IsFocused=focused;
        if(focused)GotFocus?.Invoke(this,EventArgs.Empty);
        else {modifiers=0;LostFocus?.Invoke(this,EventArgs.Empty);}
    }
    public void InsertBulk(string text){Editor.InsertBulk(text);Changed?.Invoke(this,EventArgs.Empty);}
    public void Key(EnbUiInputEvent input,Action? handled=null)
    {
        if(!IsEnabled)return;
        modifiers=input.Modifiers;
        if(input.Code!=0x2d && Editor.Key(input.Code,modifiers))
        {handled?.Invoke();Changed?.Invoke(this,EventArgs.Empty);return;}
        KeyDown?.Invoke(this,input,handled);
    }
    public void TextInput(string text)
    {
        if(!IsEnabled)return;
        foreach(char c in text)Editor.Key(0,modifiers,c);
        Changed?.Invoke(this,EventArgs.Empty);
    }
}
internal sealed class EnbChatHit
{
    public EnbUiRect Bounds;
    /// <summary>The placement group its bounds are in: the retail chat's, or the Modern UI chat panel's.</summary>
    public EnbNext.Presentation.EnbUiGroup Group=EnbNext.Presentation.EnbUiGroup.Chat;
    public bool IsVisible,IsHitTestVisible,Published;
    public int ZIndex;
    public Action Left=null!;
    public Action? Right;
    public Action<double>? Wheel;
}
