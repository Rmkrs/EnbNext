namespace EnbNext.Engine;

internal sealed partial class EnbFrontendUi
{
    public readonly EnbUiTextInput Username=new(new(64));
    public readonly EnbUiTextInput Password=new(new(64)){PasswordChar='*'};
    public readonly EnbUiTextInput CharacterName=new(new(20){CharacterClass=EnbRetailEditorCharacterClass.Letters});
    public readonly EnbUiTextInput ShipName=new(new(25));
    public readonly EnbUiTextInput GroupName=new(new(40));
    public readonly EnbUiTextInput SiteUsername=new(new(128));
    public readonly EnbUiTextInput SitePassword=new(new(128)){PasswordChar='*'};
    private bool normalizingName;
    private readonly EnbSingleLineInputOwner inputFocus=new();
    private void InitializeInputs()
    {
        foreach(var input in new[]{Username,Password,CharacterName,ShipName,SiteUsername,SitePassword,GroupName}){inputFocus.Bind(input.Editor);input.KeyDown+=EditorKey;}
        CharacterName.Changed+=NormalizeEnteredName;
    }
    private static void EditorKey(EnbUiTextInput input,EnbUiInputEvent key,Action? handled)
    {
        if(key.Code==0x2d && key.Modifiers==0)
        {input.Editor.Key(key.Code);handled?.Invoke();}
    }
    private void NormalizeEnteredName(object? sender,EventArgs args)
    {
        if(normalizingName)return;
        string current=CharacterName.Text;
        string filtered=FilterCharacterName(current);
        if(current==filtered)return;
        int caret=Math.Clamp(CharacterName.CaretIndex,0,current.Length);
        int filteredCaret=FilterCharacterName(current[..caret]).Length;
        normalizingName=true;
        try{CharacterName.Text=filtered;CharacterName.CaretIndex=filteredCaret;}
        finally{normalizingName=false;}
    }
    private static string FilterCharacterName(string name)=>NormalizeCharacterName(new string(name.Where(c=>c<=255&&char.IsLetter(c)).ToArray()));
    public static string NormalizeCharacterName(string name)
    {
        //004BE9D0: at most16 bytes, initial upper case and remaining lower case.
        if(name.Length>16)name=name[..16];
        return name.Length==0?name:char.ToUpperInvariant(name[0])+name[1..].ToLowerInvariant();
    }
    private void DisposeInputs()
    {
        CharacterName.Changed-=NormalizeEnteredName;
        foreach(var input in new[]{Username,Password,CharacterName,ShipName,SiteUsername,SitePassword,GroupName})
        {input.KeyDown-=EditorKey;input.SetFocused(false);input.Editor.Dispose();}
    }
}
