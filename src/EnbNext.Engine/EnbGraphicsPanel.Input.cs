using System.Collections.Immutable;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbGraphicsPanel : IEnbUiGadgetHandler
{
    // Existing Next display overlay precedes the retail collection; 14..23 retain native order.
    private EnbUiGadgetPointer? pointer;
    public EnbUiGadgetPointer Pointer=>pointer??=new(choiceCapacity+20,1u<<14,custom:this);
    private EnbUiPoint inputPoint;
    private long inputTime;
    private bool wheelFrame;
    public bool SlidersEnabled { get; private set; } = true;
    public void OpenInput() { CancelInput();Relayout();LayoutChoices(state.DisplayChoices.Count);SetSlidersEnabled(); }
    public void ToggleRecommended() { state.Recommended=!state.Recommended;SetSlidersEnabled(); }
    private void SetSlidersEnabled()
    {
        SlidersEnabled=!state.Recommended;
        for(int row=0;row<5;row++)if(!RowEnabled(row)){pointer?.Retire(19-row);Slider(row).Cancel();}
    }
    public event Action<string>? SoundRequested;
    public static string? InputKey(int index)=>index switch
    {
        >=0 and <12=>$"graphics/choice{index:00}",12=>"graphics/scopeSystem",13=>"graphics/scopeCharacter",
        14=>"graphics/CHECK00",15=>"graphics/TRACK04",16=>"graphics/TRACK03",17=>"graphics/TRACK02",18=>"graphics/TRACK01",19=>"graphics/TRACK00",
        20=>"graphics/RESOLUTION",21=>"graphics/CANCEL",22=>"graphics/OK",23=>"graphics/EXIT",24=>"graphics/UISCALE",25=>"graphics/TEXTUREOVERRIDES",26=>"graphics/ENHANCED",27=>"graphics/BLOOM",28=>"graphics/HDR",29=>"graphics/SUPERSAMPLING",30=>"graphics/ANISOTROPIC",31=>"graphics/BGCAP",>=32=>$"graphics/choice{index-20:00}",_=>null
    };
    private static int InputIndex(string key)
    {
        if(Row(key) is int row)return 19-row;
        if(key.StartsWith("graphics/choice")&&int.TryParse(key.AsSpan(15),out int choice)&&choice>=0)return ChoiceInput(choice);
        return key switch {"graphics/scopeSystem"=>12,"graphics/scopeCharacter"=>13,"graphics/CHECK00"=>14,
            "graphics/ANISOTROPIC"=>30,"graphics/BGCAP"=>31,"graphics/SUPERSAMPLING"=>29,"graphics/HDR"=>28,"graphics/ENHANCED"=>26,"graphics/BLOOM"=>27,"graphics/TEXTUREOVERRIDES"=>25,"graphics/UISCALE"=>24,"graphics/RESOLUTION"=>20,"graphics/CANCEL"=>21,"graphics/OK"=>22,"graphics/EXIT"=>23,_=>-1};
    }
    private static int? Row(string key)
    {
        ReadOnlySpan<char> s=key;if(!s.StartsWith("graphics/"))return null;s=s[9..];
        int prefix=s.StartsWith("TRACK")||s.StartsWith("right")||s.StartsWith("thumb")?5:s.StartsWith("left")?4:0;
        return prefix>0&&int.TryParse(s[prefix..],out int row)&&row is >=0 and <5?row:null;
    }
    public void CancelInput(){pointer?.Retire();foreach(var slider in state.Sliders)slider.Cancel();inputPoint=default;}
    private void Hits(Span<bool> hits,float x,float y,bool dropdown,int choices,bool wheel)
    {
        hits.Clear();if(disposed||!state.PreviewActive)return;
        var point=new EnbUiPoint(x*640d,y*480d);
        LayoutChoices(choices);
        for(int i=0;i<choices;i++)hits[ChoiceInput(i)]=dropdown&&ChoiceGadget(i).Bounds.Contains(point);
        bool advanced=state.Advanced;var checkbox=Gadgets[scopeStart+3].Bounds;
        hits[26]=advanced&&Gadgets[scopeStart+4].Bounds.Contains(point);
        hits[30]=advanced&&state.Enhanced.Enabled&&Gadgets[scopeStart+8].Bounds.Contains(point);
        hits[29]=advanced&&state.Enhanced.Enabled&&Gadgets[scopeStart+7].Bounds.Contains(point);
        hits[28]=advanced&&state.Enhanced.Enabled&&Gadgets[scopeStart+6].Bounds.Contains(point);
        hits[27]=advanced&&state.Enhanced.Enabled&&Gadgets[scopeStart+5].Bounds.Contains(point);
        hits[25]=advanced&&checkbox.Contains(point);
        hits[24]=advanced&&Gadgets[scopeStart+2].Bounds.Contains(point);
        hits[31]=advanced&&Gadgets[scopeStart+9].Bounds.Contains(point);
        hits[12]=!advanced&&Gadgets[scopeStart].Bounds.Contains(point);hits[13]=!advanced&&Gadgets[scopeStart+1].Bounds.Contains(point);
        for(int i=0;i<resources.Pieces.Length;i++)
        {
            var piece=resources.Pieces[i];int index=InputIndex(Gadgets[i].Key);
            if((index==14||index is >=20 and <=23)&&RetailVisible(piece.Name))hits[index]=piece.Query.Contains(x,y-(float)((Gadgets[i].Bounds.Y-piece.Bounds.Y)/480));
            if((piece.Query with {ParentSphere=resources.BackgroundSphere}).Contains(x,y))hits[choiceCapacity+20]=true;
        }
        hits[choiceCapacity+20]|=Design("graphics/FRAME").Contains(point);
        for(int row=0;row<5;row++)if(RowEnabled(row))
            hits[19-row]=wheel?resources.WheelBounds[row].Contains(point):Slider(row).Hit(point)!=EnbScrollbarPart.None;
    }
    public bool Contains(float x,float y,bool dropdown,int choices)
    {LayoutChoices(choices);Span<bool> hits=stackalloc bool[choiceCapacity+21];Hits(hits,x,y,dropdown,choices,false);foreach(bool h in hits)if(h)return true;return false;}
    public string? AdvanceInput(EnbUiInputQueue queue,EnbUiSize size,double scaling,long now,bool dropdown,int choices)
    {
        if(disposed||!state.PreviewActive){CancelInput();return null;}
        var input=queue.TakeFrame();
        PrepareInput(input,now,dropdown,choices);
        var frame=new EnbUiPointerFrame(input.Code,input.Modifiers,queue.LeftDown,queue.RightDown);
        Pointer.DispatchPrepared(ref frame);
        return CompleteInput(frame,queue,size,scaling,now);
    }
    public void PrepareInput(EnbUiInputEvent input,long now) => PrepareInput(input,now,state.Dropdown,state.ChoiceCount);

    public void PrepareInput(EnbUiInputEvent input,long now,bool dropdown,int choices)
    {
        float x=Math.Clamp(input.X,0,1),y=Math.Clamp(input.Y,0,1);
        inputPoint=new(x*640d,y*480d);inputTime=now;wheelFrame=input.Code is 0x101 or 0x102;
        for(int row=0;row<5;row++)
        {
            var slider=Slider(row);slider.Query(inputPoint);
            if(!RowEnabled(row)){Pointer.Retire(19-row);slider.Cancel();}
        }
        LayoutChoices(choices);
        for(int i=0;i<choiceCapacity;i++)if(!dropdown||i>=choices)Pointer.Retire(ChoiceInput(i));
        if(!state.Advanced)for(int i=24;i<=31;i++)Pointer.Retire(i);
        else{Pointer.Retire(12);Pointer.Retire(13);Pointer.Retire(14);Pointer.Retire(20);}
        if(!state.Enhanced.Enabled){Pointer.Retire(27);Pointer.Retire(28);Pointer.Retire(29);Pointer.Retire(30);}
        Span<bool> hits=stackalloc bool[choiceCapacity+21];Hits(hits,x,y,dropdown,choices,wheelFrame);
        Pointer.Prepare(hits);
    }
    public string? CompleteInput(EnbUiPointerFrame frame,EnbUiInputQueue queue,EnbUiSize size,double scaling,long now)
    {
        int command=Pointer.Command(frame);
        if(Pointer.ToggleHandler==14)state.Recommended=!state.Recommended;
        if(command==14){SetSlidersEnabled();SoundRequested?.Invoke("fontclick.wav");}
        if(command is >=20 and <=23)SoundRequested?.Invoke("Roll_Select02b.wav");
        int width=Math.Max(1,(int)Math.Round(size.Width*scaling)),height=Math.Max(1,(int)Math.Round(size.Height*scaling));
        for(int i=0;i<12;i++)
        {
            ref var label=ref labels[i];if(label.Definition is not {} d)continue;
            int index=label.InputIndex;
            label.Ink=label.Clock.Update(palette,d.Properties,EnbAttachedFontColor.Contains(label.Clip,width,height,queue.X,queue.Y),index>=0&&Pointer.Selected(index),now);
        }
        if(command>=0&&command<choiceCapacity+20)return InputKey(command);
        return command<0&&frame.Code==0x1b&&frame.Modifiers==0?"graphics/EXIT":null;
    }
    bool IEnbUiGadgetHandler.HasFlash(int index)=>index<choiceCapacity+20;
    int IEnbUiGadgetHandler.FlashSource(int index,bool beforeHitQuery)
    {
        if(index==choiceCapacity+20)return -1;if(index is <15 or >19)return 0;if(wheelFrame)return -1;
        var s=Slider(19-index);var part=beforeHitQuery?s.PreviousHit:s.CurrentHit;
        return part is EnbScrollbarPart.Up or EnbScrollbarPart.Thumb or EnbScrollbarPart.Down?(int)part:-1;
    }
    bool IEnbUiGadgetHandler.TryHandle(int index,ref EnbUiPointerFrame frame,uint flags,out EnbUiGadgetResponse response)
    {
        response=default;if(index is <15 or >19)return false;
        flags&=0x1077;if(flags==0)return true;
        var slider=Slider(19-index);bool captured=Pointer.LeftCapture==index||Pointer.RightCapture==index;
        var change=EnbUiCaptureChange.None;bool rewritten=false,update=false;
        if((flags&0x11)!=0)
        {rewritten=true;change=(flags&0x10)!=0?EnbUiCaptureChange.TakeRight:EnbUiCaptureChange.TakeLeft;update=slider.Begin(slider.CurrentHit,inputPoint,inputTime);}
        else if(captured&&(flags&0x44)!=0)
        {rewritten=true;change=(flags&0x40)!=0?EnbUiCaptureChange.ReleaseRight:EnbUiCaptureChange.ReleaseLeft;slider.Cancel();}
        else if(captured&&(flags&0x22)!=0){rewritten=true;update=slider.Move(inputPoint)||slider.Advance(inputPoint,inputTime);}
        else if(!captured&&(flags&0x1000)!=0){rewritten=update=true;slider.SetValue(slider.Value+(frame.Code==0x101?-1:1));}
        if(update&&slider.NotchChanged)SoundRequested?.Invoke("slider_bar_1.wav");
        response=new(true,rewritten,change);return true;
    }
    public ImmutableList<EnbUiModelDraw> DrawFrame(EnbUiSize size,double scaling,string? error,IReadOnlyList<EnbDisplayChoice> choices,int choice,bool dropdown,EnbDisplayScope scope,string character)
        =>DrawCore(size,scaling,null,null,0,error,choices,choice,dropdown,scope,character,true);
}
