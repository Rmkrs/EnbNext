using R = EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

/// <summary>63C590 proximity admission, 69F540/69F710 slot and 69F850 completion.</summary>
public sealed class EnbRetailStationDoorAnimation(int frames,float rate,uint now)
{
    private readonly EnbCurrentRetailHierarchyAnimationState _render=new()
        {Mode=2,HasHierarchy=true,HasAnimation=true};
    private uint _last=now;
    private bool _bound;
    public bool Open {get;private set;}
    public float Progress {get;private set;}
    public float Frame=>_render.CurrentFrame;
    public bool Complete=>frames==0 || !_bound || (Open?Progress>=frames:Progress<=0);

    /// <returns>The accepted direction change, or null while unchanged/in flight.</returns>
    public bool? Update(bool near,uint clock)
    {
        uint elapsed=unchecked(clock-_last);_last=clock;
        bool? changed=null;
        if(frames==0)
        {
            // 69F850 accepts a slot with no acquired HAnim; 63C590 still
            // updates proximity state and plays the accepted direction cue.
            if(near!=Open){Open=near;return near;}
            return null;
        }
        // Controller admission precedes the render callback's slot update.
        if(near!=Open && Complete)
        {
            Open=near;_bound=true;changed=near;
            Progress=near?0:frames;
            _render.CurrentFrame=near?0:frames-1;
            _render.PreviousTime=clock;_render.Playback=2;
        }
        else if(_bound)
        {
            using var precision=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
            float seconds=(float)((R)elapsed*(R).001f);
            R step=(R)rate*(R)seconds;
            Progress=(float)(Open?(R)Progress+step:(R)Progress-step);
            if(!Open)
            {
                // Reverse explicitly binds the slot's frame; forward lets
                // the render object run its independent one-shot HAnim clock.
                _render.CurrentFrame=Progress;_render.Playback=0;
                _render.PreviousTime=clock;
            }
        }
        if(_bound)_render.RefreshSingle(()=>clock,()=>rate,()=>frames,_=>{},()=>0,()=>0,(_,_,_)=>0);
        return changed;
    }

    public static string? Sound(uint stationType,bool open)=>stationType switch
    {
        1=>open?"jenquai_station_door_1.wav":"jenquai_station_door_close",
        2=>open?"progen_station_door_1.wav":"progen_station_door_close",
        0 or >=3 and <=9=>open?"terren_station_door_1.wav":"terran_station_door_close",
        _=>null
    };
}
