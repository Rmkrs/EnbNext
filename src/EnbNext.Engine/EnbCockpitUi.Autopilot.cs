using EnbNext.Rendering;
using EnbNext.Presentation;

namespace EnbNext.Engine;
internal sealed partial class EnbCockpitUi
{
    private readonly EnbRetailText _autopilotText=new();
    private readonly object _autopilotButtonOwner=new();
    private EnbUiRect _autopilotBounds;
    private bool _autopilotActive,_autopilotCanStart,_autopilotWasStop;
    internal event Action<bool>? AutopilotRequested;
    internal EnbUiModelDraw? AutopilotDraw {get;private set;}
    internal void StopAutopilotWarp(){if(WarpEngaged)WarpRequested?.Invoke();}
    internal bool AutopilotCaptured=>_pressedName=="AUTOPILOT";
    private EnbAuthoredUiAsset? _autopilotSource,_autopilotArt;
    internal void UpdateAutopilot(bool enabled,bool active,bool canStart)
    {
        _autopilotActive=active;_autopilotCanStart=canStart;AutopilotDraw=null;DamageMeterDraw=null;
        if(!_interactive || _shortcutAsset is null || !_hitBounds.TryGetValue("BANK_0",out var bank) ||
            !_hitBounds.TryGetValue("WARP",out var warp) || Font("FONT_SMALL") is not {} font)return;
        if(!ReferenceEquals(_autopilotSource,_shortcutAsset))
        {
            _autopilotSource=_shortcutAsset;
            // BANK_0 is the authored round body; BANK_0_0/1 are its separate arrows.
            _autopilotArt=_shortcutAsset with {Resource=_shortcutAsset.Resource.Copy().SetMeshes(
                [_shortcutAsset.Resource.Meshes.First(m=>m.Name=="BANK_0")])};
        }
        // Modern UI: the A and D buttons sit at the ends of its action bar (screen space).
        if(Modern)
        {
            if(ModernRoundButtons?.Invoke() is not {} ends)return;
            double sx=640/_size.Width,sy=480/_size.Height,size=ends.Left.Width*sx;
            if(DamageMeterButtonEnabled)
                DamageMeterDraw=RoundButton(ends.Right.Center.X*sx,ends.Right.Center.Y*sy,size,bank,"D",_damageMeterText,_damageMeterButtonOwner,"DAMAGE",DamageMeterShown,true,out _damageMeterBounds) with {LayoutGroup=EnbUiGroup.None,NextPanel=true};
            if(enabled)AutopilotDraw=RoundButton(ends.Left.Center.X*sx,ends.Left.Center.Y*sy,size,bank,"A",_autopilotText,_autopilotButtonOwner,"AUTOPILOT",active,canStart,out _autopilotBounds) with {LayoutGroup=EnbUiGroup.None,NextPanel=true};
            return;
        }
        double radius=Math.Sqrt(Math.Pow(bank.Center.X-warp.Center.X,2)+Math.Pow(bank.Center.Y-warp.Center.Y,2));
        double diameter=bank.Width*.45, dy=(bank.Center.Y-warp.Center.Y)*.4;
        double dx=Math.Sqrt(radius*radius-dy*dy), y=warp.Center.Y+dy;
        // The Next-only damage meter toggle mirrors the autopilot button on the
        // right of the warp dial.
        if(DamageMeterButtonEnabled)
            DamageMeterDraw=RoundButton(warp.Center.X+dx,y,diameter,bank,"D",_damageMeterText,_damageMeterButtonOwner,"DAMAGE",
                DamageMeterShown,true,out _damageMeterBounds);
        if(!enabled)return;
        AutopilotDraw=RoundButton(warp.Center.X-dx,y,diameter,bank,"A",_autopilotText,_autopilotButtonOwner,"AUTOPILOT",
            active,canStart,out _autopilotBounds);
    }
    /// <summary>Modern UI action-bar ends (screen space) for the autopilot and damage meter buttons.</summary>
    internal Func<(EnbUiRect Left,EnbUiRect Right)?>? ModernRoundButtons {get;set;}
    private EnbUiModelDraw RoundButton(double x,double y,double diameter,EnbUiRect bank,string letter,EnbRetailText text,object owner,
        string name,bool active,bool available,out EnbUiRect bounds)
    {
        var font=Font("FONT_SMALL")!;
        bounds=new((x-diameter/2)*_size.Width/640,(y-diameter/2)*_size.Height/480,
            diameter*_size.Width/640,diameter*_size.Height/480);
        bool pressed=active || _pressedName==name;
        text.SetText(letter,font,active?new(1,1,.1f):available?new(1,1,1):new(.5f,.5f,.5f));
        text.SetDisplayScale(1,1);
        text.FitTo(new(bounds.Width*.7,bounds.Height*.7),_scaling);
        var image=text.Raster;
        return new(_autopilotArt!,0,bounds,_hovered==name&&!pressed)
        {GadgetOwner=owner,LayoutGroup=EnbUiGroup.Cockpit,DesignBounds=bank.Translate(-320,-240),
         EmissiveOverride=pressed?new System.Numerics.Vector3(.35f):null,
         Images=image is null?[]:[new(image,new(bounds.Center.X-text.Width/2,
             bounds.Center.Y-text.Height/2,text.Width,text.Height))]};
    }

    // Damage meter toggle (Next-only), shown when the Interface option enables it.
    private readonly EnbRetailText _damageMeterText=new();
    private readonly object _damageMeterButtonOwner=new();
    private EnbUiRect _damageMeterBounds;
    internal bool DamageMeterButtonEnabled {get;set;}
    internal bool DamageMeterShown {get;set;}
    internal event Action? DamageMeterToggleRequested;
    internal EnbUiModelDraw? DamageMeterDraw {get;private set;}
    private void RequestDamageMeterToggle()=>DamageMeterToggleRequested?.Invoke();
    internal System.Collections.Immutable.ImmutableList<EnbUiModelDraw> RoundButtons(System.Collections.Immutable.ImmutableList<EnbUiModelDraw> draws)
    {
        if(DamageMeterDraw is {} damage)draws=draws.Insert(0,damage);
        return AutopilotDraw is {} autopilot?draws.Insert(0,autopilot):draws;
    }
}
