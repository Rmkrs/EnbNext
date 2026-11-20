using EnbNext.Client;
using EnbNext.Presentation;
using EnbNext.Rendering;
namespace EnbNext.Engine;

/// <summary>005D4890/005D6BC0 actual child owners, ordinary collection and shared modal.</summary>
internal sealed class EnbInformationPetitionInput
{
    private readonly EnbInformationUi ui;
    private readonly EnbPetitionController petition;
    private readonly EnbConfirmationPanel dialog;
    private readonly EnbConfirmationState confirmation;
    public EnbInformationEditorInput Editor {get;}
    public EnbRetailTextEditor Player {get;}
    private EnbInformationPanel? playerPanel;
    private EnbUiGadgetContext? context;
    private bool modal;
    public bool IsAttached=>context is not null;
    public int Answer {get;private set;}
    public event Action<string>? SoundRequested;
    public uint Category=>petition.Category;
    public EnbInformationPanel Root=>ui[petition.Category>=3?"complaint":"help"];
    public EnbInformationPetitionInput(EnbInformationUi ui,EnbPetitionController petition,EnbRetailTextEditor body,
        EnbRetailTextEditor entry,EnbRetailTextEditor player,EnbConfirmationPanel dialog,EnbConfirmationState confirmation)
    {
        this.ui=ui;this.petition=petition;this.dialog=dialog;this.confirmation=confirmation;Player=player;
        Editor=new(ui,ui.Load("petition"),null,body,entry);Editor.SoundRequested+=Sound;
    }
    private void Sound(string value)=>SoundRequested?.Invoke(value);
    public void Attach(EnbUiGadgetContext value)
    {
        if(context==value)return;Detach();context=value;
        ui.AttachPetition(petition.Category>=3);
        Player.TabTarget=Editor.Entry; // 005D61E0 +A0/+40 -> subject; 006AA200 follows the actual editor.
        playerPanel=petition.Category>=3?ui["player"]:null;
        if(playerPanel is not null){value.Add(playerPanel.Pointer);value.Add(Player.Pointer);Player.GadgetSoundRequested+=Sound;}
        value.Add(Editor.Panel.Pointer);Editor.Attach(value);dialog.SoundRequested+=Sound;
        SynchronizeModal();
    }
    public void Detach()
    {
        if(context is not {} c)return;
        dialog.DetachInput();dialog.SoundRequested-=Sound;modal=false;Editor.Body.Pointer.SetEnabled(0,true);
        Editor.Detach();c.Remove(Editor.Panel.Pointer);
        if(playerPanel is not null){c.Remove(Player.Pointer);c.Remove(playerPanel.Pointer);Player.GadgetSoundRequested-=Sound;}
        Player.TabTarget=null;context=null;playerPanel=null;ui.DetachPetition();
    }
    public void SynchronizeModal()
    {
        bool next=confirmation.Text is not null;if(next==modal || context is null)return;
        modal=next;Editor.Body.Pointer.SetEnabled(0,!modal);
        if(modal){Editor.Entry.IsFocused=false;dialog.AttachInput(context);}
        else dialog.DetachInput();
    }
    public void CancelInput(){Editor.CancelInput();Editor.Body.Pointer.SetEnabled(0,!modal);playerPanel?.CancelInput();dialog.RetireInput();}
    public void Retire(){Detach();Editor.Retire();}
    public void PrepareInput(EnbUiInputEvent input,EnbUiSize size,double scaling,long now)
    {
        Answer=0;SynchronizeModal();Root.PrepareInput(input);
        playerPanel?.PrepareInput(input);Player.PrepareGadget(default,false,false,false,input.Character);
        Editor.PrepareInput(input,size,now,input.Character);
        if(modal)dialog.PrepareInput(confirmation,size,scaling,input.X,input.Y);
    }
    public string? CompleteInput(EnbUiPointerFrame frame)
    {
        // 005D6DC0 -> 004BE9D0 -> 006AA570: active name is rewritten each tick.
        if(playerPanel is not null && Player.IsFocused)
        {
            string text=Player.Text;int count=Math.Min(16,text.Length);Span<char> normalized=stackalloc char[count];
            for(int i=0;i<count;i++)
            {
                char c=text[i];normalized[i]=i==0?(c is >= 'a' and <= 'z'?(char)(c-32):c):
                    c is >= 'A' and <= 'Z'?(char)(c+32):c;
            }
            if(!text.AsSpan().SequenceEqual(normalized))Player.Text=new string(normalized);
            Player.CaretIndex=count;
        }
        if(modal){Answer=dialog.CompleteInput(frame);return null;}
        string? key=Editor.CompleteInput(frame);
        if(Editor.Panel.Pointer.Consumed)return key;
        if(playerPanel?.Pointer.Consumed==true)
        {int command=playerPanel.Pointer.Command(frame);return command>=0?playerPanel.InputKey(command):null;}
        if(Root.Pointer.Consumed)return Root.CompleteInput(frame);
        if(Editor.Body.Pointer.Consumed || Editor.Entry.Pointer.Consumed || Editor.SliderPointer.Consumed || Player.Pointer.Consumed)return null;
        return key;
    }
    public bool Contains(float x,float y)=>Root.Contains(x,y) || Editor.Panel.Contains(x,y) || playerPanel?.Contains(x,y)==true ||
        modal && dialog.Bounds[0].Contains(new(x*640d,y*480d));
    public string? InputKey(EnbUiGadgetPointer pointer,int index)=>pointer==Editor.Body.Pointer?"petition/MESSAGE":
        pointer==Editor.Panel.Pointer?Editor.Panel.InputKey(index):pointer==playerPanel?.Pointer?playerPanel.InputKey(index):
        pointer==Root.Pointer?Root.InputKey(index):null;
}
