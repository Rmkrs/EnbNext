namespace EnbNext.Engine;

/// <summary>006AA720: one active single-line editor, independent of mouse capture.</summary>
public sealed class EnbSingleLineInputOwner
{
    public EnbRetailTextEditor? Active { get; private set; }
    public void Bind(EnbRetailTextEditor editor)
    {
        if(editor.Multiline)throw new ArgumentException("Multiline editors use gadget capture.",nameof(editor));
        editor.BindSingleLineOwner(this);
    }
    internal void SetFocus(EnbRetailTextEditor editor,bool focused)
    {
        if(focused)
        {
            if(Active is {} previous && previous!=editor)previous.SetFocusStorage(false);
            Active=editor;
        }
        else if(Active==editor)Active=null;
        editor.SetFocusStorage(focused);
    }
    public void Deactivate(){if(Active is {} editor)SetFocus(editor,false);}
}
