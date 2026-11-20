using EnbNext.Net7.Gameplay;

namespace EnbNext.Client;

public readonly record struct EnbFlightPointer(float X,float Y,bool ThrottleEnabled,bool SpeedLocked);

/// <summary>Local cockpit intent. Hosts own key translation/capture; Net7 owns transmission.</summary>
public sealed class EnbFlightInputState<TKey> where TKey:notnull
{
    private readonly Dictionary<TKey,string> held=[];
    private readonly HashSet<TKey> suspended=[];
    public EnbCockpitThrustState Cockpit { get; }=new();
    public Net7FlightInput Intent { get; private set; }

    public bool KeyUp(TKey key)
    {
        suspended.Remove(key);
        return held.Remove(key);
    }

    public bool KeyDown(TKey key,string? action,bool canControl,bool canThrust,out bool changed,out bool releasePointer,bool heldThrustAdmitted=true)
    {
        changed=releasePointer=false;
        if(suspended.Contains(key) || held.ContainsKey(key))return true;
        if(action is "Move Forward Toggle" or "Move Forward Lock" or "Move Backward Lock")
        {
            if(!canControl || !canThrust){suspended.Add(key);return true;}
            Cockpit.ApplyKeyboardLock(action=="Move Backward Lock"?-1:1,action=="Move Forward Toggle",Intent.Thrust);
            foreach(var pair in held.Where(p=>p.Value is "Move Forward" or "Move Backward" or "Stop").ToArray())
            {suspended.Add(pair.Key);held.Remove(pair.Key);}
            held[key]=action;changed=releasePointer=true;return true;
        }
        if(!canControl || action is not ("Turn Left" or "Turn Right" or "Tilt Up" or "Tilt Down" or "Move Forward" or "Move Backward" or "Stop"))return false;
        // 0057D6C0: held Forward/Backward need LockSpeed clear and a valid nonzero MaxSpeed; otherwise nothing is sent
        // (HUD-D05). Stop has its own gate.
        if(action is "Move Forward" or "Move Backward" && !heldThrustAdmitted){suspended.Add(key);return true;}
        held[key]=action;changed=true;
        if(action is "Stop" or "Move Forward" or "Move Backward")Cockpit.Clear();
        return true;
    }

    public Net7FlightInput Resolve(bool canControl,bool canThrust,long now,bool thrustPointerInside,EnbFlightPointer? pointer=null)
    {
        if(!canControl || !canThrust)Cockpit.Clear();
        Cockpit.AdvanceHeld(now,thrustPointerInside);
        bool Held(string action)=>held.ContainsValue(action);
        float h=(Held("Turn Right")?1:0)-(Held("Turn Left")?1:0);
        float v=(Held("Tilt Up")?1:0)-(Held("Tilt Down")?1:0);
        bool mouseThrust=false;
        if(pointer is {} mouse)
        {
            float x=Math.Clamp(mouse.X,0,1),y=Math.Clamp(mouse.Y,0,1);
            h=2*(x-.5f);v=-2*(y-.5f);
            mouseThrust=EnbMouseFlightPolicy.ShouldThrust(x,y,mouse.ThrottleEnabled,mouse.SpeedLocked);
        }
        int thrust=Held("Stop")?0:(Held("Move Forward")?1:0)-(Held("Move Backward")?1:0);
        if(mouseThrust && !Held("Stop") && !Held("Move Forward") && !Held("Move Backward"))thrust=1;
        bool cockpit=!mouseThrust && !Held("Stop") && !Held("Move Forward") && !Held("Move Backward");
        if(cockpit)thrust=Cockpit.Direction;
        return Intent=new(h,v,thrust){ThrustLocked=cockpit && thrust!=0 && Cockpit.UseLockedMove};
    }

    public void Suspend()
    {
        suspended.UnionWith(held.Keys);
        ResetAfterSendFailure();
    }

    public void ResetAfterSendFailure()
    {
        held.Clear();Cockpit.Clear();Intent=default;
    }
}
