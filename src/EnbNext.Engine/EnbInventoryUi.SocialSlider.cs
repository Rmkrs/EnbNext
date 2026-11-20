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
    private sealed class SocialSliderState
    {
        public double Value,Maximum,Grab,AtGrab;
        public int Visible,Repeat;
        public long RepeatAt;
        public bool Enabled;
        public Rect Track,Thumb,Bounds,Up,Down;
    }
    private readonly Dictionary<string,SocialSliderState> _socialSliders=[];
    private int SocialPage(string key)=>key switch {"friends"=>_friendsPage,"guild"=>_guildPage,"vendor"=>_vendorPage,"missions"=>_characterPage,"factions"=>_factionPage,"skills"=>_skillPage,_=>_guildDisplayPage};
    private static int SliderRow(string key,double value)=>key=="vendor"?(int)(value+.5):(int)value;
    private void SetSocialScroll(string key,double value)
    {
        if(!_socialSliders.TryGetValue(key,out var state))return;
        double next=Math.Clamp(value,0,state.Maximum);
        if(Math.Round(next)!=Math.Round(state.Value))InteractionSoundRequested?.Invoke("slider_bar_1.wav");
        state.Value=next;
        // 0058FF19 / 0081A333: direct __ftol of slider +D4.
        if(key=="friends")_friendsPage=(int)next;else if(key=="guild")_guildPage=(int)next;else if(key=="vendor")_vendorPage=SliderRow(key,next);else if(key=="missions")_characterPage=(int)next;else if(key=="factions")_factionPage=(int)next;else if(key=="skills")_skillPage=(int)next;else _guildDisplayPage=(int)next;
    }
    private void SocialSlider(string key,int count,int visible,bool enabled)
    {
        LoadTerminalSlider();
        if(!_socialSliders.TryGetValue(key,out var state))_socialSliders[key]=state=new();
        state.Maximum=Math.Max(0,count-visible);state.Visible=visible;state.Enabled=enabled;
        SetSocialScroll(key,SocialPage(key)!=SliderRow(key,state.Value)?SocialPage(key):state.Value);
        string prefix="socialSlider/"+key+"/";
        if(enabled && _pressed?.StartsWith(prefix,StringComparison.Ordinal)==true && state.Repeat!=0)
        {
            bool samePart=_pressed.EndsWith("Track") ? (state.Repeat<0?_point.Y<Screen(state.Thumb).Top:_point.Y>Screen(state.Thumb).Bottom)
                : Screen(_pressed.EndsWith("Up")?state.Up:state.Down).Contains(_point);
            long now=Environment.TickCount64;
            if(samePart && now>=state.RepeatAt){SetSocialScroll(key,state.Value+state.Repeat);state.RepeatAt=now+100;}
            _animatingText=true;
        }
        if(state.Maximum<=0 || _terminalThumbAsset is not {} thumb)return;
        string anchor=key=="skills"?"skillFrame/SLIDER_"+(_skillVisibleUnits==10?"01":"00"):key+"/SLIDER_00";
        var first=_guildAnchors[anchor+"_MIN"];var last=_guildAnchors[anchor+"_MAX"];
        double top=Math.Min(first.Y,last.Y),bottom=Math.Max(first.Y,last.Y),x=first.X;
        double arrow=_art["up/CLICK_UP"].Design.Height-2,start=top+arrow,length=Math.Max(0,bottom-top-2*arrow);
        if(length<=0)return;
        state.Up=_art["up/CLICK_UP"].Design.Translate(new(x,top));state.Down=_art["down/CLICK_DOWN"].Design.Translate(new(x,bottom));
        Draw("up/CLICK_UP",state.Up,prefix+"Up",enabled);Draw("down/CLICK_DOWN",state.Down,prefix+"Down",enabled);
        double height=Math.Max(16,length*visible/count);
        if(!EnbW3dHierarchyPose.TryEvaluate(thumb.Resource.Hierarchies.Single(),Matrix4x4.Identity,thumb.Resource.Animations.Single(),
            (float)Math.Max(0,height-16),out var pose,out _) || !EnbW3dSkinDeformer.TryDeform(thumb.Resource.Meshes.Single(),pose,out var geometry,out _))return;
        var min=geometry.Positions.Aggregate(Vector3.Min);var max=geometry.Positions.Aggregate(Vector3.Max);
        var source=new Rect(min.X,-max.Y,max.X-min.X,max.Y-min.Y);
        state.Track=new(x-arrow/2,start,arrow,length);
        state.Thumb=source.Translate(new(x,start+Math.Max(0,length-height)*state.Value/state.Maximum));
        state.Bounds=new(x-arrow/2,top,arrow,bottom-top);
        if(enabled)Hit(prefix+"Track",state.Track);
        _draws.Add(new(thumb with{Geometry=geometry},0,(Screen(state.Thumb)),_hover==prefix+"Thumb"){LayoutGroup=_layoutGroup,DesignBounds=(source),CompositeOpaqueCoverage=true});
        if(enabled)Hit(prefix+"Thumb",state.Thumb);
    }
    private void SocialSliderPress(string id)
    {
        var parts=id.Split('/');
        if(parts.Length!=3 || parts[0]!="socialSlider" || !_socialSliders.TryGetValue(parts[1],out var state) || !state.Enabled || state.Maximum<=0)return;
        state.Repeat=parts[2] switch {"Up"=>-1,"Down"=>1,"Track"=>_point.Y<Screen(state.Thumb).Top?-state.Visible:state.Visible,_=>0};
        if(state.Repeat!=0){SetSocialScroll(parts[1],state.Value+state.Repeat);state.RepeatAt=Environment.TickCount64+500;}
        else if(parts[2]=="Thumb"){state.Grab=_point.Y-Screen(state.Thumb).Top;state.AtGrab=state.Value;}
    }
    private void SocialSliderMove(Point point)
    {
        var parts=_pressed?.Split('/');
        if(parts is {Length:3} && _socialSliders.TryGetValue(parts[1],out var state) && state.Enabled && parts[2]=="Thumb")
        {
            if(!Screen(state.Bounds).Inflate(32*_size.Width/640,32*_size.Height/480).Contains(point))SetSocialScroll(parts[1],state.AtGrab);
            else if(Screen(state.Track).Height>Screen(state.Thumb).Height)
                SetSocialScroll(parts[1],(point.Y-state.Grab-Screen(state.Track).Top)/(Screen(state.Track).Height-Screen(state.Thumb).Height)*state.Maximum);
        }
        _point=point;Publish();
    }
}
