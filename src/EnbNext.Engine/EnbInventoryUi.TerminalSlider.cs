using Point = EnbNext.Presentation.EnbUiPoint;
using Vector = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using KeyModifiers = EnbNext.Assets.EnbKeyModifiers;
using EnbNext.Engine;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbInventoryUi
{
    private EnbAuthoredUiAsset? _terminalThumbAsset;
    private double _terminalScroll, _terminalScrollMaximum, _terminalGrab, _terminalScrollAtGrab;
    private Rect _terminalTrack, _terminalThumb, _terminalSliderBounds;
    private int _terminalRepeat;
    private long _terminalRepeatAt;

    private void LoadTerminalSlider()
    {
        if(_terminalThumbAsset is not null)return;
        var catalog=_assets!.RetailMixResources!;
        if(EnbAuthoredUiAsset.Load(_assets,"slider_vert.w3d") is not {} thumb ||
            !catalog.TryReadResource("slider_vert.w3d",out var mesh,out var location) ||
            !catalog.TryReadResource("slider_vert-1.w3d",out var animation,out _) ||
            !EnbW3dModel.TryDecode([..mesh,..animation],out var model) ||
            !EnbAuthoredUiAsset.TryCompile(catalog,location,model,"slider_vert",out var resource))
            throw new InvalidDataException("Terminal slider asset unavailable");
        _terminalThumbAsset=thumb with {Resource=resource};
    }

    private void SetTerminalScroll(double value)
    {
        double next=Math.Clamp(value,0,_terminalScrollMaximum);
        if(Math.Round(next)!=Math.Round(_terminalScroll))InteractionSoundRequested?.Invoke("slider_bar_1.wav");
        _terminalScroll=next;
        // 005B1230/005B2950 use __ftol directly (unlike loot's +.5).
        _terminalPage=(int)next;
    }

    private void TerminalSlider(int count)
    {
        if(_terminal?.Type is not (0 or 2 or 3) || _terminalScrollMaximum<=0 || _terminalThumbAsset is not {} thumb)return;
        // 005B0314 / 005EBE79: Attach(parent, vertical=0, sliderIndex=1).
        // Jobs attaches index 0 at 005A1C7F; formula terminals use index 1.
        string slider=_terminal.Type==3?"SLIDER_00":"SLIDER_01";
        var first=_conversationAnchors[_terminalArt+"/"+slider+"_MIN"];
        var last=_conversationAnchors[_terminalArt+"/"+slider+"_MAX"];
        double top=Math.Min(first.Y,last.Y)+5,bottom=Math.Max(first.Y,last.Y)+5,x=first.X;
        var up=_art["up/CLICK_UP"].Design.Translate(new(x,top));
        var down=_art["down/CLICK_DOWN"].Design.Translate(new(x,bottom));
        double arrow=_art["up/CLICK_UP"].Design.Height-2;
        double start=top+arrow,length=Math.Max(0,bottom-top-2*arrow);
        if(length<=0)return;
        Draw("up/CLICK_UP",up,"terminalSliderUp");Draw("down/CLICK_DOWN",down,"terminalSliderDown");
        double height=Math.Max(16,length*10/count);
        var animation=thumb.Resource.Animations.Single();
        if(!EnbW3dHierarchyPose.TryEvaluate(thumb.Resource.Hierarchies.Single(),Matrix4x4.Identity,animation,
            (float)Math.Max(0,height-16),out var pose,out _) ||
            !EnbW3dSkinDeformer.TryDeform(thumb.Resource.Meshes.Single(),pose,out var geometry,out _))return;
        var min=geometry.Positions.Aggregate(Vector3.Min);var max=geometry.Positions.Aggregate(Vector3.Max);
        var source=new Rect(min.X,-max.Y,max.X-min.X,max.Y-min.Y);
        _terminalTrack=new(x-arrow/2,start,arrow,length);
        _terminalThumb=source.Translate(new(x,start+Math.Max(0,length-height)*_terminalScroll/_terminalScrollMaximum));
        _terminalSliderBounds=new(x-arrow/2,top,arrow,bottom-top);
        Hit("terminalSliderTrack",_terminalTrack);
        _draws.Add(new(thumb with {Geometry=geometry},0,(Screen(_terminalThumb)),_hover=="terminalSliderThumb"){DesignBounds=(source)});
        Hit("terminalSliderThumb",_terminalThumb);
    }

    private void UpdateTerminalSliderRepeat()
    {
            long now=Environment.TickCount64;
            if(_pressed is "terminalSliderUp" or "terminalSliderDown" or "terminalSliderTrack")
            {
                if(now>=_terminalRepeatAt && (_pressed!="terminalSliderTrack" || (_terminalRepeat<0?_point.Y<Screen(_terminalThumb).Top:_point.Y>Screen(_terminalThumb).Bottom)))
                {SetTerminalScroll(_terminalScroll+_terminalRepeat);_terminalRepeatAt=now+100;}
                _animatingText=true;
            }
    }

    private void TerminalSliderPress(string id)
    {
        if(_terminal?.Type is not (0 or 2 or 3) || _terminalScrollMaximum<=0)return;
        _terminalRepeat=id switch {"terminalSliderUp"=>-1,"terminalSliderDown"=>1,
            "terminalSliderTrack"=>_point.Y<Screen(_terminalThumb).Top?-10:10,_=>0};
        if(_terminalRepeat!=0){SetTerminalScroll(_terminalScroll+_terminalRepeat);_terminalRepeatAt=Environment.TickCount64+500;}
        if(id=="terminalSliderThumb"){_terminalGrab=_point.Y-Screen(_terminalThumb).Top;_terminalScrollAtGrab=_terminalScroll;}
    }

    private void TerminalSliderMove(Point point)
    {
        if(_pressed=="terminalSliderThumb")
        {
            if(!Screen(_terminalSliderBounds).Inflate(32*_size.Width/640,32*_size.Height/480).Contains(point))
                SetTerminalScroll(_terminalScrollAtGrab);
            else if(Screen(_terminalTrack).Height>Screen(_terminalThumb).Height)
                SetTerminalScroll((point.Y-_terminalGrab-Screen(_terminalTrack).Top)/(Screen(_terminalTrack).Height-Screen(_terminalThumb).Height)*_terminalScrollMaximum);
        }
        _point=point;Publish();
    }
}
