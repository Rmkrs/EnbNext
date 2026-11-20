using System.Numerics;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbInformationPanel : IEnbUiGadgetHandler
{
    public EnbInformationPanel? Parent {get;private set;}
    private EnbUiPoint attachedPosition;
    public EnbUiPoint Offset
    {
        get=>Parent is null?owner.Offset:attachedPosition;
        set {if(Parent is null)owner.Offset=value;else {attachedPosition=value;owner.RefreshChildren(this);} }
    }
    public void Attach(EnbInformationPanel? parent){Parent=parent;if(parent is not null)ResetAttachment();}
    internal void ResetAttachment()
    {
        var pivot=Parent!.Resource.Pivot("TRANS_BONE");var p=Parent.Offset;
        attachedPosition=new(p.X+pivot.X,p.Y-pivot.Y);owner.RefreshChildren(this);
    }
    private EnbUiGadgetPointer? pointer;
    public EnbUiGadgetPointer Pointer => pointer ??= new(Resource.PointerOrder.Length,custom:this);
    private EnbUiPoint inputPoint,dragPoint,dragOffset;
    public string? InputKey(int index)=>(uint)index<Resource.PointerOrder.Length?Resource.PointerOrder[index]:null;
    public void CancelInput(){pointer?.Retire();inputPoint=dragPoint=dragOffset=default;}
    private void Hits(Span<bool> hits,float x,float y)
    {
        hits.Clear();
        var translation=Matrix4x4.CreateTranslation((float)Offset.X,-(float)Offset.Y,0);
        var sphere=Resource.BackgroundSphere is {} s?s with {Center=s.Center+translation.Translation}:(EnbW3dWorldBounds?)null;
        foreach(var piece in Resource.Pieces)
        {
            var query=piece.Query with {World=piece.Query.World*translation};
            int index=Array.IndexOf(Resource.PointerOrder,piece.Key);
            if(index>=0)hits[index]=query.Contains(x,y);
            if((query with {ParentSphere=sphere}).Contains(x,y))hits[^1]=true;
        }
    }
    public bool ModelContains(string key,float x,float y)
    {
        var translation=Matrix4x4.CreateTranslation((float)Offset.X,-(float)Offset.Y,0);
        foreach(var piece in Resource.Pieces)
            if(piece.Key==key)return (piece.Query with {World=piece.Query.World*translation}).Contains(x,y);
        return false;
    }
    public bool Contains(float x,float y)
    {Span<bool> hits=stackalloc bool[Resource.PointerOrder.Length+1];Hits(hits,x,y);foreach(bool hit in hits)if(hit)return true;return false;}
    public void PrepareInput(EnbUiInputEvent input)
    {
        float x=Math.Clamp(input.X,0,1),y=Math.Clamp(input.Y,0,1);inputPoint=new(x,y);
        Span<bool> hits=stackalloc bool[Resource.PointerOrder.Length+1];Hits(hits,x,y);Pointer.Prepare(hits);
        for(int i=0;i<Resource.PointerOrder.Length;i++)Pointer.SetEnabled(i,Enabled(Resource.PointerOrder[i],
            Resource.Prefix=="log"?10:Resource.Prefix=="help"?11:12,owner.PetitionInput?.Category??0,owner.LogInput?.Page??1));
    }
    public string? CompleteInput(EnbUiPointerFrame frame)
    {
        int command=Pointer.Command(frame);
        return command>=0?InputKey(command):frame.Code==0x1b&&frame.Modifiers==0?Resource.Prefix+(Resource.Prefix=="petition"?"/EXIT_BUTTON":"/EXIT2"):null;
    }
    bool IEnbUiGadgetHandler.HasFlash(int index)=>Resource.Prefix is "log" or "petition" or "player" && index<Resource.PointerOrder.Length;
    bool IEnbUiGadgetHandler.TryHandle(int index,ref EnbUiPointerFrame frame,uint flags,out EnbUiGadgetResponse response)
    {
        response=default;bool background=index==Resource.PointerOrder.Length;
        if(!background && Resource.Prefix is "log" or "petition" or "player")return false;
        flags&=background?0x10ffu:0x55u;if(flags==0)return true;
        var change=EnbUiCaptureChange.None;
        if(!background)
        {
            // 005D34F0/005D5880 style4: callbacks and capture, no press selection.
            if((flags&0x11)!=0)change=(flags&0x10)!=0?EnbUiCaptureChange.TakeRight:EnbUiCaptureChange.TakeLeft;
            else if((flags&0x40)!=0&&Pointer.RightCapture==index)change=EnbUiCaptureChange.ReleaseRight;
            else if((flags&4)!=0&&Pointer.LeftCapture==index)change=EnbUiCaptureChange.ReleaseLeft;
            else return true;
        }
        else if((flags&1)!=0){dragPoint=inputPoint;dragOffset=Offset;change=EnbUiCaptureChange.TakeLeft;}
        else if((flags&4)!=0&&Pointer.LeftCapture==index)change=EnbUiCaptureChange.ReleaseLeft;
        else if((flags&2)!=0&&Pointer.LeftCapture==index)
        {
            float dx=((float)inputPoint.X-(float)dragPoint.X)*640;
            float dy=((float)inputPoint.Y-(float)dragPoint.Y)*-480;
            double x=Math.Round((float)((float)dragOffset.X+dx));
            double y=Math.Round((float)(-(float)dragOffset.Y+dy));
            var e=Resource.BackgroundExtents;var p=Resource.DragOrigin;
            Offset=new(Clamp(x,-320+e.X-p.X,320-e.X-p.X),-Clamp(y,-240+e.Y-p.Y,240-e.Y-p.Y));
        }
        response=new(true,true,change);return true;
    }
    private static double Clamp(double value,double min,double max)=>value<min?min:value>max?max:value;
}
