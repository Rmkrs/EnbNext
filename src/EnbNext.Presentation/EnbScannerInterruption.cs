namespace EnbNext.Presentation;

public sealed record EnbSceneInterruptionSnapshot(int? State, string? AbilityName,
    long? ActivationTime, float? Progress);

/// <summary>Scanner interruption lifecycle: retail 0060F1A0, 0060E6A0, 0059E790.</summary>
public sealed class EnbScannerInterruption
{
    private ulong? _target;
    private long _start, _activation, _finished;
    private bool _completing;
    public bool Visible { get; private set; }
    public int State { get; private set; }
    public string Text { get; private set; } = string.Empty;
    public float Progress { get; private set; }
    public float ActivationProgress { get; private set; }
    public ulong TextRevision { get; private set; }
    public ulong Generation { get; private set; }

    public void Reset()
    {
        _target=null; Visible=false; State=0; Text=string.Empty;
        Progress=ActivationProgress=0; _start=_activation=_finished=0; _completing=false;
        TextRevision++;
    }

    public void Update(ulong? target, EnbSceneInterruptionSnapshot? value, uint time)
    {
        if (_target!=target) { Reset(); _target=target; }
        if(target is null || value is null)return;
        if(value.State==1 && State!=1)
        {
            bool created=!Visible;
            Visible=true; _completing=false; _start=time;
            Generation++;
            if(value.ActivationTime is long activation)_activation=activation;
            Text=value.AbilityName??"Unknown Skill"; TextRevision++;
            // Resource construction clears the state; the following update
            // enters state 1 again through the already-created resource path.
            State=created?0:1;
        }
        else if(State==1 && value.State is >=2 and <=5)
        {
            State=value.State.Value;
            Text=State switch {2=>"Interrupted!",3=>"Interruption Failed!",4=>"Skill Activated!",_=>"Skill Deactivated"};
            TextRevision++; _completing=true; _finished=time;
        }
        if(!Visible)return;
        // 0060D7D0 initializes the result retention interval to 2000 ms.
        if(_completing && (long)time-_finished>=2000)
        {
            Reset();_target=target;return;
        }
        // 59E7A2..59E7E0: unordered FCOM takes the same branch as below zero.
        float progress=value.Progress??0;
        Progress=!(progress>=0)?0:progress>1?1:progress;
        if(State is 1 or 3)
        {
            // Retail asserts for a non-positive interval. Contain malformed
            // optional timing locally, without emitting invalid GPU values.
            if(_activation>_start)
                ActivationProgress=(float)Math.Clamp(((double)time-_start)/(_activation-_start),0,1);
        }
    }
}
