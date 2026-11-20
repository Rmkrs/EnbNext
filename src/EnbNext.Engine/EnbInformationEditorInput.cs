using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

/// <summary>005AB030/005AB510: actual Log editor/slider owners in one dispatcher.</summary>
internal sealed class EnbInformationEditorInput : IEnbUiGadgetHandler
{
    public EnbInformationPanel Panel { get; }
    public EnbRetailTextEditor Body { get; }
    public EnbRetailTextEditor Entry { get; }
    private readonly EnbCaptainLogController? log;
    public EnbSliderInput Slider { get; }
    public EnbUiGadgetPointer SliderPointer { get; }
    private EnbUiGadgetContext? context;
    private EnbUiPoint point;
    private long time;
    private bool wheel,forceBody,pageEditing;
    public bool IsAttached=>context is not null;
    public int Page=>log?.Page??1;
    public int Scroll=>Slider.Offset;
    public bool CommitPage { get; private set; }
    public bool FollowCaret { get; private set; }
    public event Action<string>? SoundRequested;
    public EnbInformationEditorInput(EnbInformationUi ui,EnbInformationPanel panel,EnbCaptainLogController? log,EnbRetailTextEditor body,EnbRetailTextEditor entry)
    {
        Panel=panel;this.log=log;Body=body;Entry=entry;
        Entry.CharacterClass=log is null?EnbRetailEditorCharacterClass.Printable:EnbRetailEditorCharacterClass.Digits;
        Slider=new(panel.Resource.ScrollTrack,panel.Resource.ScrollBounds);SliderPointer=new(1,custom:this,hasBackground:false);
    }
    private void Sound(string sound)=>SoundRequested?.Invoke(sound);
    public void Attach(EnbUiGadgetContext value)
    {
        if(context==value)return;Detach();context=value;
        Body.GadgetSoundRequested+=Sound;Entry.GadgetSoundRequested+=Sound;
        // Newest ordinary registrations first: slider(-1002), entry(0), panel buttons/background.
        value.Add(Entry.Pointer);value.Add(SliderPointer);value.Add(Body.Pointer,EnbUiGadgetGroup.First,-1001);
    }
    public void Detach()
    {
        if(context is {} c){c.Remove(Body.Pointer);c.Remove(SliderPointer);c.Remove(Entry.Pointer);context=null;Body.GadgetSoundRequested-=Sound;Entry.GadgetSoundRequested-=Sound;}
        CancelInput();pageEditing=false;
    }
    public void CancelInput(){Body.RetireGadgetInput();SliderPointer.Retire();Slider.Cancel();forceBody=false;}
    public void Retire()=>Detach();
    public void SetScroll(int value){Slider.SetValue(value);FollowCaret=false;}
    public void UpdateLayout(int total,int visible,int scroll)
    {Slider.Configure(total,visible);Slider.SetValue(scroll);}
    public void RequestBodyFocus(){uint flags=1;Body.FocusGadget(true,ref flags);forceBody=true;}
    public void RequestPageEdit(){Entry.Text="";Entry.Activate();pageEditing=true;}
    public void PrepareInput(EnbUiInputEvent input,EnbUiSize size,long now,char character='\0')
    {
        time=now;wheel=input.Code is 0x101 or 0x102;CommitPage=false;FollowCaret=false;
        if(forceBody)
        {
            uint flags=1;
            if(Body.FocusGadget(true,ref flags))
            {Panel.Pointer.SetSelected(Array.IndexOf(Panel.Resource.PointerOrder,"log/DATE_STAMP"),false);Body.Entered=true;forceBody=false;FollowCaret=true;}
        }
        float x=Math.Clamp(input.X,0,1),y=Math.Clamp(input.Y,0,1);
        point=new(x*640d-Panel.Offset.X,y*480d-Panel.Offset.Y);
        Panel.PrepareInput(input);Slider.Query(point);
        bool sliderHit=wheel?Panel.Resource.WheelBounds.Contains(point):Slider.CurrentHit!=EnbScrollbarPart.None;
        SliderPointer.Prepare(sliderHit?1u:0u);
        bool hit=Panel.ModelContains(Panel.Resource.Prefix=="log"?"log/MESSAGE":"petition/MESSAGE",x,y);
        Body.PrepareGadget(new(x*size.Width,y*size.Height),hit,false,SliderPointer.LeftCapture==0,character);
        //Character admission belongs to the actual editor buffer (005AB030/005D4890).
        Entry.PrepareGadget(default,false,false,false,character);
    }
    public string? CompleteInput(EnbUiPointerFrame frame)
    {
        if(Body.Pointer.LastHandler>=0&&frame.Modifiers==0)Entry.IsFocused=false;
        if(log is null && (Entry.GadgetKeyResult&EnbRetailEditorKeyResult.Tab)!=0)
        {uint flags=frame.GadgetFlags;Body.FocusGadget(false,ref flags,(float)Slider.Value);FollowCaret=true;}
        CommitPage=pageEditing&&!Entry.IsFocused;
        if(CommitPage)pageEditing=false;
        FollowCaret|=Body.GadgetKeyResult!=EnbRetailEditorKeyResult.None;
        return Body.Pointer.Consumed || Entry.Pointer.Consumed || SliderPointer.Consumed?null:Panel.CompleteInput(frame);
    }
    bool IEnbUiGadgetHandler.HasFlash(int index)=>true;
    int IEnbUiGadgetHandler.FlashSource(int index,bool beforeHitQuery)
    {
        if(wheel)return -1;var part=beforeHitQuery?Slider.PreviousHit:Slider.CurrentHit;
        return part is EnbScrollbarPart.Up or EnbScrollbarPart.Thumb or EnbScrollbarPart.Down?(int)part:-1;
    }
    bool IEnbUiGadgetHandler.TryHandle(int index,ref EnbUiPointerFrame frame,uint flags,out EnbUiGadgetResponse response)
    {
        response=default;flags&=0x1077;if(flags==0)return true;
        bool captured=SliderPointer.LeftCapture==0||SliderPointer.RightCapture==0;
        var change=EnbUiCaptureChange.None;bool rewritten=false,update=false;
        if((flags&0x11)!=0)
        {rewritten=true;change=(flags&0x10)!=0?EnbUiCaptureChange.TakeRight:EnbUiCaptureChange.TakeLeft;update=Slider.Begin(Slider.CurrentHit,point,time);}
        else if(captured&&(flags&0x44)!=0)
        {rewritten=true;change=(flags&0x40)!=0?EnbUiCaptureChange.ReleaseRight:EnbUiCaptureChange.ReleaseLeft;Slider.Cancel();}
        else if(captured&&(flags&0x22)!=0)
        {rewritten=true;update=Slider.Move(point)||Slider.Advance(point,time);}
        else if(!captured&&(flags&0x1000)!=0)
        {rewritten=update=true;Slider.SetValue(Slider.Value+(frame.Code==0x101?-1:1));}
        if(update&&Slider.NotchChanged)Sound("slider_bar_1.wav");
        response=new(true,rewritten,change);return true;
    }
}
