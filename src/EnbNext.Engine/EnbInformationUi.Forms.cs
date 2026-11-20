using EnbNext.Assets;
using EnbNext.Client;
using EnbNext.Presentation;
namespace EnbNext.Engine;

internal sealed partial class EnbInformationUi
{
    public string? Prompt {get;private set;}
    private bool backAfterPrompt;
    public EnbRetailTextEditor? FocusedEditor=>Prompt is not null?Player.IsFocused?Player:null:
        Body.Pointer.LeftCapture==0 || Body.IsFocused?Body:Entry.IsFocused?Entry:Player.IsFocused?Player:null;
    public bool Editing=>FocusedEditor is not null;
    public bool DataValid=>EnbPetitionController.DataValid(Entry.Text,Body.Text);
    public void ResetEditors()
    {Body.Entered=false;Body.InsertMode=true;Entry.Entered=false;Entry.InsertMode=true;Player.Entered=false;Player.InsertMode=true;}
    public void BlurEditors(){Body.IsFocused=false;Entry.IsFocused=false;Player.IsFocused=false;}
    public void FocusEditor(EnbRetailTextEditor editor)
    {if(FocusedEditor is {} previous && previous!=editor)previous.IsFocused=false;editor.Activate();}
    public EnbInformationPetitionInput BeginPetition(uint category,EnbConfirmationPanel dialog,EnbConfirmationState confirmation)
    {
        Petition.Open(category);var input=BindPetition(dialog,confirmation);input.Editor.SetScroll(0);
        Entry.Text="";Entry.MaxLength=250;Entry.CharacterClass=EnbRetailEditorCharacterClass.Printable;
        Body.Text="";Player.Text="";ResetEditors();return input;
    }
    public void RetireForm(){Prompt=null;Petition.Retire();BlurEditors();}
    public string? ShowPrompt(string section,bool back,EnbConfirmationState confirmation)
    {
        EnbRetailIniDocument ini;
        try{ini=EnbRetailIniDocument.Load(Path.Combine(resources.Assets.Paths.ClientIniDirectory,"dialogbox.ini"));}
        catch(Exception ex) when(Failure(ex)){return "The authored petition dialog is unavailable: "+ex.Message;}
        if(!ini.TryGetSection(section,out var d))return "The authored petition dialog is unavailable.";
        Prompt=section;backAfterPrompt=back;
        confirmation.Show(string.Join('\n',Enumerable.Range(0,d.GetValueOrDefault("FontSize")=="FONT_SMALL"?16:10)
            .Select(i=>d.GetValueOrDefault("Header"+i)).TakeWhile(s=>s is not null)),!d.ContainsKey("Option1"),section);
        return null;
    }
    public void FinishPetition(bool back,EnbConfirmationState confirmation)
    {Prompt=null;confirmation.Clear();if(back){Petition.Back();BlurEditors();}}
    public (bool Canceled,bool Back) AnswerPrompt(bool yes,EnbConfirmationState confirmation)
    {bool cancel=Prompt=="PETITION_ENTRY_CANCEL",back=backAfterPrompt;Prompt=null;confirmation.Clear();return(cancel&&yes,back);}
    // 005D6890: a complaint checks its player names before subject and body (OPT-D12).
    public string? ValidationPrompt(bool complaint)=>complaint&&!EnbPetitionController.TextValid(Player.Text,3)?"PETITION_PLAYER_ENTRY_NO_DATA":
        !DataValid?"PETITION_ENTRY_NO_DATA":null;
    public bool ImportPlayer(IEnbSceneFrame? scene)
    {
        var target=scene?.Objects.FirstOrDefault(o=>o.IsCurrentTarget&&o.ObjectType==1);if(target is null)return false;
        string? name=target.PlayerPresentation?.Owner;if(string.IsNullOrEmpty(name))name=target.Name??"Unknown";
        string imported=Player.Text.Length==0?name:Player.Text+"; "+name;
        // 005D6EE0 -> 006AA570 -> 0084B020 applies editor capacity to imports.
        Player.Text=imported[..Math.Min(imported.Length,Player.MaxLength)];return true;
    }
}
