using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbSettingsPanel : IEnbUiGadgetHandler
{
    private EnbUiGadgetPointer? pointer;
    // Gadgets: 0 CHECK00, 1..5 sliders, 6..9 buttons, 10 ROUTE, 12.. QOL2..QOL45, then the frame.
    private const int QolFirst=2,QolCount=44,QolGadgetLast=12+QolCount-1,FrameGadget=QolGadgetLast+1;
    public EnbUiGadgetPointer Pointer => pointer ??= new(FrameGadget,1ul | (1ul<<10) | (((1ul<<QolCount)-1)<<12),custom:this);
    private EnbUiPoint inputPoint, normalizedPoint, dragPoint, dragOffset;
    private long inputTime;
    private bool wheelFrame;
    public event Action<string>? SoundRequested;
    public static string? InputKey(int index) => index switch
    {
        0=>"settings/CHECK00",1=>"settings/TRACK04",2=>"settings/TRACK03",3=>"settings/TRACK02",4=>"settings/TRACK01",5=>"settings/TRACK00",
        6=>"settings/RESOLUTION",7=>"settings/CANCEL",8=>"settings/OK",9=>"settings/EXIT",10=>"settings/ROUTE",FrameGadget=>"settings/FRAME",>=12 and <=QolGadgetLast=>QolKeys[index-12],_=>null
    };
    private static readonly string[] QolKeys=Enumerable.Range(QolFirst,QolCount).Select(i=>"settings/QOL"+i).ToArray();
    private static int Gadget(string key) => key.StartsWith("settings/QOL")?int.Parse(key.AsSpan(12))+10:Row(key) is int row?5-row:key switch
    {"settings/ROUTE"=>10,"settings/CHECK00"=>0,"settings/RESOLUTION"=>6,"settings/CANCEL"=>7,"settings/OK"=>8,"settings/EXIT"=>9,_=>-1};
    public void CancelInput()
    {
        pointer?.Retire();foreach(var slider in state.Sliders)slider.Cancel();encounter?.Cancel();
        inputPoint=normalizedPoint=dragPoint=dragOffset=default;
    }
    private void Hits(Span<bool> hits,float x,float y,bool wheel)
    {
        hits.Clear();if(state.Page is not (7 or 8 or 9))return;
        var translation=Matrix4x4.CreateTranslation((float)Offset.X,-(float)Offset.Y,0);
        var sphere=resources.BackgroundSphere is { } s?s with {Center=s.Center+translation.Translation}:(EnbW3dWorldBounds?)null;
        for(int i=0;i<resources.Pieces.Length;i++)
        {
            var piece=resources.Pieces[i];var query=piece.Query with {World=piece.Query.World*translation};
            int gadget=Gadget(keys[i]);
            if(gadget is 0 or >=6 && gadget<10 && Visible(InputKey(gadget)!))hits[gadget]=query.Contains(x,y);
            if((query with {ParentSphere=sphere}).Contains(x,y))hits[FrameGadget]=true;
        }
        var point=new EnbUiPoint(x*640d-Offset.X,y*480d-Offset.Y);
        double extra=ExtraHeight;
        if(extra>0) { hits[7]=authoredCancel.Translate(0,extra).Contains(point);hits[8]=authoredOk.Translate(0,extra).Contains(point);hits[FrameGadget]|=(authoredFrame with {Height=authoredFrame.Height+extra}).Contains(point); }
        if(state.Page==7)hits[0]=false;
        foreach(var row in Rows)
        {
            bool enabled=RowEnabled(row.Key);
            if(row.Key==NextSliderKey)continue;
            hits[Gadget(row.Key)]=enabled&&RowCheck(Slot(row.Key)).Contains(point);
        }
        for(int row=0;row<5;row++)
            if(Visible(InputKey(5-row)!)&&SliderEnabled(row))hits[5-row]=wheel?(EncounterSlider?new EnbUiRect(Track(0).X-16,Track(0).Y,Track(0).Width+32,16):resources.WheelBounds[row]).Contains(point):Slider(row).Hit(point)!=EnbScrollbarPart.None;
    }
    public bool Contains(float x,float y)
    {Span<bool> hits=stackalloc bool[FrameGadget+1];Hits(hits,x,y,false);foreach(bool hit in hits)if(hit)return true;return false;}

    public string? AdvanceInput(EnbUiInputQueue queue,EnbUiSize size,double scaling,long now)
    {
        if(state.Page is not (7 or 8 or 9)){CancelInput();return null;}
        var input=queue.TakeFrame();
        PrepareInput(input,now);
        var frame=new EnbUiPointerFrame(input.Code,input.Modifiers,queue.LeftDown,queue.RightDown);
        Pointer.DispatchPrepared(ref frame);
        return CompleteInput(frame,queue,size,scaling,now);
    }
    public void PrepareInput(EnbUiInputEvent input,long now)
    {
        float x=Math.Clamp(input.X,0,1),y=Math.Clamp(input.Y,0,1);
        normalizedPoint=new(x,y);inputPoint=new(x*640d-Offset.X,y*480d-Offset.Y);inputTime=now;
        wheelFrame=input.Code is 0x101 or 0x102;
        for(int row=0;row<5;row++)
        {
            var slider=Slider(row);slider.Query(inputPoint);
            if(!Visible(InputKey(5-row)!)||!SliderEnabled(row)){Pointer.Retire(5-row);slider.Cancel();}
        }
        Span<bool> hits=stackalloc bool[FrameGadget+1];Hits(hits,x,y,wheelFrame);
        Pointer.SetEnabled(0,state.Page!=7 || Visible("settings/CHECK00") && state.RoutePlanner);
        Pointer.SetEnabled(10,Visible("settings/ROUTE"));Pointer.SetEnabled(11,false);
        for(int i=12;i<=QolGadgetLast;i++)Pointer.SetEnabled(i,Visible(InputKey(i)!) && RowEnabled(InputKey(i)!));
        Pointer.Prepare(hits);
    }
    public string? CompleteInput(EnbUiPointerFrame frame,EnbUiInputQueue queue,EnbUiSize size,double scaling,long now)
    {
        int command=Pointer.Command(frame);
        if(Pointer.ToggleHandler==0)state.ToggleCheck(command==0);
        if(Pointer.ToggleHandler==10 && state.Page==7)state.ToggleRoutePlanner();
        if(Pointer.ToggleHandler is >=12 and <=QolGadgetLast && state.Page is 7 or 8)state.ToggleConvenience(Pointer.ToggleHandler-10);
        if(command==0 && state.Page==8)SoundRequested?.Invoke("fontclick.wav");
        if(command is >=1 and <=5)state.ApplySliderCommand(5-command);
        SyncEncounter();
        state.PreviewQualityChanges();
        state.PreviewSound();
        int width=Math.Max(1,(int)Math.Round(size.Width*scaling)),height=Math.Max(1,(int)Math.Round(size.Height*scaling));
        foreach(var label in labels)
            if(label.Definition is { } d && label.Style is { } style)
            {
                int gadget=label.GadgetIndex;
                label.Ink=label.Clock.Update(palette,d.Properties,EnbAttachedFontColor.Contains(label.Clip,width,height,queue.X,queue.Y),gadget>=0&&Pointer.Selected(gadget),now);
            }
        if(command is >=6 and <=9 or 11)return InputKey(command);
        return command<0 && frame.Code==0x1b && frame.Modifiers==0?"settings/EXIT":null;
    }

    bool IEnbUiGadgetHandler.HasFlash(int index)=>index!=FrameGadget;
    int IEnbUiGadgetHandler.FlashSource(int index,bool beforeHitQuery)
    {
        if(index==FrameGadget)return -1;if(index is <1 or >5)return 0;
        if(wheelFrame)return -1;
        var slider=Slider(5-index);var part=beforeHitQuery?slider.PreviousHit:slider.CurrentHit;
        return part is EnbScrollbarPart.Up or EnbScrollbarPart.Thumb or EnbScrollbarPart.Down?(int)part:-1;
    }
    bool IEnbUiGadgetHandler.TryHandle(int index,ref EnbUiPointerFrame frame,uint flags,out EnbUiGadgetResponse response)
    {
        response=default;
        if(index==FrameGadget && state.Page==9)
        {
            flags&=0x10ff;if(flags==0)return true;
            var capture=EnbUiCaptureChange.None;
            if((flags&1)!=0){dragPoint=normalizedPoint;dragOffset=Offset;capture=EnbUiCaptureChange.TakeLeft;}
            else if(Pointer.LeftCapture==FrameGadget && (flags&4)!=0)capture=EnbUiCaptureChange.ReleaseLeft;
            else if(Pointer.LeftCapture==FrameGadget && (flags&2)!=0)
            {
                float dx=(float)((float)normalizedPoint.X-(float)dragPoint.X)*640;
                float dy=(float)((float)normalizedPoint.Y-(float)dragPoint.Y)*480;
                double nextX=Math.Round((float)((float)dragOffset.X+dx)),nextY=Math.Round((float)((float)dragOffset.Y+dy));
                var extents=resources.BackgroundExtents;
                var next=new EnbUiPoint(ClampNative(nextX,-320+extents.X,320-extents.X),ClampNative(nextY,-240+extents.Y,240-extents.Y));
                if(next!=Offset)
                {
                    Offset=next;
                    state.RefreshAfterMove();
                    UpdateClips();
                }
            }
            response=new(true,true,capture);return true;
        }
        if(index is <1 or >5)return false;
        flags&=0x1077;if(flags==0)return true;
        var slider=Slider(5-index);bool captured=Pointer.LeftCapture==index||Pointer.RightCapture==index;
        var change=EnbUiCaptureChange.None;bool rewritten=false,update=false;
        if((flags&0x11)!=0)
        {
            rewritten=true;change=(flags&0x10)!=0?EnbUiCaptureChange.TakeRight:EnbUiCaptureChange.TakeLeft;
            update=slider.Begin(slider.CurrentHit,inputPoint,inputTime);
        }
        else if(captured&&(flags&0x44)!=0)
        {rewritten=true;change=(flags&0x40)!=0?EnbUiCaptureChange.ReleaseRight:EnbUiCaptureChange.ReleaseLeft;slider.Cancel();}
        else if(captured&&(flags&0x22)!=0)
        {rewritten=true;update=slider.Move(inputPoint)||slider.Advance(inputPoint,inputTime);}
        else if(!captured&&(flags&0x1000)!=0)
        {rewritten=update=true;slider.SetValue(slider.Value+(frame.Code==0x101?-1:1));}
        if(update&&slider.NotchChanged)SoundRequested?.Invoke("slider_bar_1.wav");
        response=new(true,rewritten,change);return true;
    }
    private void UpdateClips()
    {
        foreach(var label in labels)if(label.Style is { } style)
        {
            var b=style.CenteredClipBounds;float x=(float)Offset.X,y=-(float)Offset.Y;
            if(label.Definition?.Properties.GetValueOrDefault("Attach","").ToUpperInvariant() is "OK" or "CANCEL")y-=(float)ExtraHeight;
            int left=Math.Clamp((int)(b.X+x),-320,320),right=Math.Clamp((int)(b.Z+x),-320,320);
            int bottom=Math.Clamp((int)(b.Y+y),-240,240),top=Math.Clamp((int)(b.W+y),-240,240);
            label.Clip=new(320+left,240-top,right-left,top-bottom);
        }
    }
    // Native comparisons preserve the lower branch even for an oversized authored object.
    private static double ClampNative(double value,double min,double max)=>value<min?min:value>max?max:value;
    public ImmutableList<EnbUiModelDraw> DrawFrame(EnbUiSize size,double scaling,long now,string? error=null)
        =>DrawCore(size,scaling,null,null,now,error,true);
}
