using EnbNext.Assets;
using EnbNext.Rendering;
using EnbNext.Presentation;

namespace EnbNext.Engine;

internal sealed partial class EnbInformationUi:IDisposable
{
    private readonly EnbInformationResources resources;
    private readonly EnbOptionsPanelResources fonts;
    private readonly EnbFontEffectPalette palette;
    public EnbRetailTextEditor Body {get;}=new(1024,true);
    public EnbRetailTextEditor Entry {get;}=new(250);
    public EnbRetailTextEditor Player {get;}=new(250){CharacterClass=EnbRetailEditorCharacterClass.Letters};
    public EnbPetitionController Petition {get;}=new();
    public EnbInformationUi(EnbInformationResources resources,EnbOptionsPanelResources fonts,EnbFontEffectPalette palette)
    {this.resources=resources;this.fonts=fonts;this.palette=palette;TextInput.Bind(Entry);TextInput.Bind(Player);}
    public EnbInformationPetitionInput? PetitionInput {get;private set;}
    public EnbInformationPetitionInput BindPetition(EnbConfirmationPanel dialog,EnbNext.Client.EnbConfirmationState confirmation)
        =>PetitionInput??=new(this,Petition,Body,Entry,Player,dialog,confirmation);
    public EnbSingleLineInputOwner TextInput { get; } = new();
    public EnbInformationEditorInput? LogInput { get; private set; }
    public EnbInformationEditorInput BindLog(EnbCaptainLogController controller)
        => LogInput ??= new(this,Load("log"),controller,Body,Entry);
    private EnbUiPoint offset;
    public EnbUiPoint Offset { get=>offset; set {offset=value;RefreshChildren(null);} }
    internal void RefreshChildren(EnbInformationPanel? parent)
    {
        if(player?.Parent is {} p && (parent is null?p.Parent is null:p==parent))player.ResetAttachment();
        if(petition?.Parent is {} q && (parent is null?q.Parent is null:q==parent))petition.ResetAttachment();
    }
    public void AttachPetition(bool complaintRoot)
    {
        DetachPetition();var root=Load(complaintRoot?"complaint":"help");
        if(complaintRoot){Load("player").Attach(root);Load("petition").Attach(player!);}
        else Load("petition").Attach(root);
    }
    public void DetachPetition(){petition?.Attach(null);player?.Attach(null);}
    private EnbInformationPanel? log,help,petition,player,complaint;
    public EnbInformationPanel? Find(string prefix)=>prefix switch {"log"=>log,"help"=>help,"petition"=>petition,"player"=>player,"complaint"=>complaint,_=>null};
    public EnbInformationPanel this[string prefix]=>Find(prefix)??throw new InvalidOperationException("Information panel is not loaded: "+prefix);
    public EnbInformationPanel Load(string prefix)
    {
        EnbInformationPanel Create()=>new(this,resources.Get(prefix),fonts,resources.Defaults,palette,prefix is "log" or "petition"?resources.Scrollbar:null);
        return prefix switch {"log"=>log??=Create(),"help"=>help??=Create(),"petition"=>petition??=Create(),
            "player"=>player??=Create(),"complaint"=>complaint??=Create(),_=>throw new ArgumentOutOfRangeException(nameof(prefix))};
    }
    public void CancelInput(){if(LogInput?.IsAttached==true)LogInput.CancelInput();if(PetitionInput?.IsAttached==true)PetitionInput.CancelInput();log?.CancelInput();help?.CancelInput();petition?.CancelInput();player?.CancelInput();complaint?.CancelInput();}
    public void Dispose(){Petition.Retire();PetitionInput?.Retire();PetitionInput=null;LogInput?.Retire();LogInput=null;TextInput.Deactivate();Body.Dispose();Entry.Dispose();Player.Dispose();log?.Dispose();help?.Dispose();petition?.Dispose();player?.Dispose();complaint?.Dispose();log=help=petition=player=complaint=null;}
}
