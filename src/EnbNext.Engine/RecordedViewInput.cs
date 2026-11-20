namespace EnbNext.Engine;

internal sealed record RecordedViewInput(string Kind, double X=0,double Y=0,double Elapsed=0,
    bool Initial=false,bool Distance=false,bool Control=false,string? Action=null)
{
    internal static Action<RecordedViewInput>? Recorder;
    internal static void Observe(RecordedViewInput input)=>Recorder?.Invoke(input);
}
