using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Engine;

internal sealed class EnbWorldStationInput<TKey>(EnbWorldController world,Func<EnbRetailAssetBundle?> assetsAccessor,
    Func<uint?> session,Action<int> setCursor,Action<string> report,Action releasePointer) where TKey:notnull
{
    private readonly Dictionary<TKey,string> _stationKeys=[];
    private bool _stationMovementLatched;
    private int _stationThrust;
    // Next free movement: the mouse buttons held (left looks around, right turns, both walk) and the mouse travel since.
    private bool _freeLeft,_freeRight;
    private float _freeMouseX,_freeMouseY;
    // The turn the mouse asked for and not yet given to the view (radians), eased in over a few frames so a quick, large
    // mouse movement swings the view instead of snapping it; and when the last part was given.
    private float _freeYawPending,_freePitchPending;
    private long _freeEased;
    /// <summary>How quickly the view catches up with the mouse: about 63% of the turn within this many seconds.</summary>
    private const double FreeEaseSeconds=.045;
    /// <summary>Next free movement: which mouse buttons are held in the world.</summary>
    internal void FreeButtons(bool left,bool right){_freeLeft=left;_freeRight=right;}
    /// <summary>Next free movement: mouse travel (pixels) while a button is held.</summary>
    internal void FreeMouse(float x,float y){_freeMouseX+=x;_freeMouseY+=y;}
    internal bool KeyUp(TKey key)=>_stationKeys.Remove(key);
    internal bool KeyDown(TKey key,string? action)
    {
        if(!world.CanControlStation)return false;
        if(action == "Move Forward Toggle")
        {
            if(_stationKeys.TryAdd(key,action))
            {
                // 00638500 command 29 starts forward or clears movement flags.
                // 0063A100 subsequently lets held keys replace that direction.
                _stationMovementLatched=!_stationMovementLatched;
                _stationThrust=_stationMovementLatched?1:0;
            }

            return true;
        }
        if(action == "Avatar Run")
        {
            if(_stationKeys.TryAdd(key,action) && assetsAccessor() is {} assets && session() is uint owner && owner != 0)
            {
                try { assets.ToggleStarbaseDefaultRun(owner); }
                catch(Exception ex) when(ex is System.IO.IOException or UnauthorizedAccessException)
                { report("Could not save Starbase Default Run: "+ex.Message); }
            }

            return true;
        }
        if(action is not ("Move Forward" or "Move Backward" or "Turn Left" or "Turn Right" or "Tilt Up" or "Tilt Down" or "Stop"))return false;
        _stationKeys[key]=action;return true;
    }
    internal void Update(EnbUiSize size,EnbUiPoint? pointer,EnbUiPoint? hoverPoint,EnbKeyModifiers modifiers)
    {
        if(!world.CanControlStation)return;
        if(world.StationFreeMovement){UpdateFree(size,hoverPoint,modifiers);return;}
        bool Held(string action)=>_stationKeys.ContainsValue(action);
        int thrust=Held("Move Forward")?1:Held("Move Backward")?-1:0;
        float turn=(Held("Turn Right")?1:0)-(Held("Turn Left")?1:0);
        float look=Held("Tilt Up")?-1:Held("Tilt Down")?1:0;
        bool orbit=modifiers.HasFlag(EnbKeyModifiers.Control);
        if(turn==0 && orbit && hoverPoint is {} horizontal)
        {
            double x=horizontal.X/Math.Max(1,size.Width);
            turn=x<.01?-1:x>.99?1:0;
        }
        // 0063A7B0: without mouse steering, Control plus the 1% screen
        // edges supplies full-rate look input when neither tilt key is held.
        if(look==0 && modifiers.HasFlag(EnbKeyModifiers.Control) && hoverPoint is {} hover)
        {
            double y=hover.Y/Math.Max(1,size.Height);
            look=y<.01?-1:y>.99?1:0;
        }
        if(pointer is not null && size.Width>0)
        {
            float x=(float)Math.Clamp(pointer.Value.X/size.Width,0,1);
            float y=(float)(pointer.Value.Y/Math.Max(1,size.Height));
            look=y-.5f;
            // 0063A100 selects modes 15/17/18/19/21; 007228C0 maps these
            // to the authored bl4/tl4/t4/tr4/br4 textures and hotspots.
            int direction=x>.4f && x<.6f ? 3 : x<.5f ? (y<.5f?2:0) : (y<.5f?4:6);
            setCursor(7+direction);
            turn=x-.5f;
            if(thrust==0 && !orbit && x>.2f && x<.8f)thrust=1;
        }
        // Retail's latch suppresses the idle clear; it does not force forward
        // again after a held backward key changes the avatar movement flags.
        if(thrust!=0 || !_stationMovementLatched)_stationThrust=thrust;
        if(Held("Stop")){_stationMovementLatched=false;_stationThrust=0;}
        bool run=assetsAccessor()?.ResolvePlayerOptions(session()).Options.StarbaseDefaultRun == true;
        // 0063A100 has no horizontal/vertical setter at exactly0.5.
        // 786C70 only clears thrust bits, so retain prior steering there.
        if(pointer is not null && turn==0)world.SetStationThrust(_stationThrust,run);
        else
        {
            world.SetStationMovement(_stationThrust,orbit?0:turn,run);
            world.SetStationCameraTurn(orbit?turn:0);
        }
        if(pointer is null || look!=0)world.SetStationLook(look);
    }
    /// <summary>
    /// Next free station movement (an option, off by default), as in other games: forward, backward and the
    /// turn keys move the avatar forward, back and sideways as the camera sees it, diagonals too. Holding the right
    /// mouse button turns the view and the avatar with the mouse; the left button swings the view around the avatar
    /// alone; both walk forward. Control with the screen edges still turns the view.
    /// </summary>
    private void UpdateFree(EnbUiSize size,EnbUiPoint? hoverPoint,EnbKeyModifiers modifiers)
    {
        bool Held(string action)=>_stationKeys.ContainsValue(action);
        int forward=Held("Move Forward")?1:Held("Move Backward")?-1:0;
        float right=(Held("Turn Right")?1:0)-(Held("Turn Left")?1:0);
        float look=Held("Tilt Up")?-1:Held("Tilt Down")?1:0;
        float view=0;
        if(modifiers.HasFlag(EnbKeyModifiers.Control) && hoverPoint is {} edge)
        {
            double x=edge.X/Math.Max(1,size.Width),y=edge.Y/Math.Max(1,size.Height);
            view=x<.01?-1:x>.99?1:0;
            if(look==0)look=y<.01?-1:y>.99?1:0;
        }
        if(_freeLeft && _freeRight)forward=1;
        if(forward!=0 || !_stationMovementLatched)_stationThrust=forward;
        if(Held("Stop")){_stationMovementLatched=false;_stationThrust=0;}
        bool run=assetsAccessor()?.ResolvePlayerOptions(session()).Options.StarbaseDefaultRun == true;
        // The mouse: a pixel turns 0.006 radians (to the right lowers the yaw) and tilts 0.004. With the right button the
        // avatar faces the view, except while stepping sideways: it has no sideways step, so it turns to walk that way.
        bool face=_freeRight && right==0;
        float speed=Math.Clamp(world.StationLookSpeed,.1f,5f);
        _freeYawPending+=-_freeMouseX*.006f*speed;_freePitchPending+=_freeMouseY*.004f*speed;_freeMouseX=_freeMouseY=0;
        long now=System.Diagnostics.Stopwatch.GetTimestamp();
        double elapsed=_freeEased==0?0:(now-_freeEased)/(double)System.Diagnostics.Stopwatch.Frequency;_freeEased=now;
        if(_freeYawPending!=0 || _freePitchPending!=0)
        {
            // The same total turn as before, given out over time; a remainder too small to see is given at once.
            float share=(float)(1-Math.Exp(-Math.Clamp(elapsed,0,.25)/FreeEaseSeconds));
            if(Math.Abs(_freeYawPending)<1e-4f && Math.Abs(_freePitchPending)<1e-4f)share=1;
            float yaw=_freeYawPending*share,pitch=_freePitchPending*share;
            _freeYawPending-=yaw;_freePitchPending-=pitch;
            world.TurnStationFree(yaw,pitch,turnAvatar:face);
        }
        else if(face)world.TurnStationFree(0,0,turnAvatar:true);
        world.SetStationFreeMovement(new(right,_stationThrust),view,run);
        // In third person the view turns on its own (to the right lowers its yaw); in first person the avatar turns.
        world.SetStationCameraTurn(-view);
        world.SetStationLook(look);
    }
    internal void Release()
    {
        _stationKeys.Clear();_stationMovementLatched=false;_stationThrust=0;
        _freeLeft=_freeRight=false;_freeMouseX=_freeMouseY=0;_freeYawPending=_freePitchPending=0;_freeEased=0;
        releasePointer();world.StopStationMovement();
    }
}
