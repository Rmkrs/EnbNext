using EnbNext.Presentation;
using EnbNext.Rendering;
namespace EnbNext.Engine;

internal sealed partial class EnbOptionsGadgetCollection
{
    public EnbUiInputQueue Input {get;}=new();
    public EnbUiGadgetContext Context {get;}=new();
    public EnbOptionsPanel? Panel {get;private set;}
    public EnbUiGadgetPointer? Pointer {get;private set;}
    public EnbUiGadgetPointer? Utility {get;private set;}
    public EnbInformationEditorInput? Log {get;private set;}
    public EnbInformationPetitionInput? Petition {get;private set;}
    public EnbBrowserPanel? Browser {get;private set;}
    private bool active;
    public event Action? Changed;
    public bool HasCapture=>Context.LeftCapture.Owner is not null||Context.RightCapture.Owner is not null||
        Pointer is {LeftCapture:>=0} or {RightCapture:>=0}||Utility is {LeftCapture:>=0} or {RightCapture:>=0};
    public void ForgetDisposedPanel()=>Panel=null;
    public bool Synchronize(EnbOptionsPanel? next,EnbUiGadgetPointer? nextPointer,EnbUiGadgetPointer? utility,
        EnbInformationEditorInput? log,EnbInformationPetitionInput? petition,bool nextActive,float x,float y,EnbBrowserPanel? browser=null)
    {
        if(ReferenceEquals(nextPointer,Pointer)&&ReferenceEquals(utility,Utility)&&nextActive==active&&ReferenceEquals(log,Log)&&ReferenceEquals(petition,Petition)&&ReferenceEquals(browser,Browser))
        {Petition?.SynchronizeModal();return false;}
        if(!ReferenceEquals(log,Log)){Log?.Detach();Log=log;}
        if(!ReferenceEquals(petition,Petition)){Petition?.Detach();Petition=petition;}
        if(!ReferenceEquals(browser,Browser)){Browser?.Detach();Browser=browser;}
        bool crossingBoundary=nextActive!=active;
        if(!ReferenceEquals(utility,Utility))
        {if(Utility is not null)Context.Remove(Utility);Utility=utility;if(utility is not null)Context.Add(utility);}
        if(!ReferenceEquals(nextPointer,Pointer))
        {
            if(Pointer is not null)Context.Remove(Pointer);
            Panel?.RetireInput();next?.RetireInput();Pointer=nextPointer;
            if(nextPointer is not null)Context.Add(nextPointer);
        }
        Log?.Attach(Context);Petition?.Attach(Context);Browser?.Attach(Context);Panel=next;active=nextActive;
        Changed?.Invoke();
        if(crossingBoundary){Input.Clear();Input.SetPosition(x,y);}
        return true;
    }
}
