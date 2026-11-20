using System.Runtime.InteropServices;

namespace EnbNext.Rendering;

/// <summary>A2E19E's per-thread default and A28A24/A28A31's CRT stream.
/// Separate from the process-wide texture mapper generator.</summary>
public sealed class EnbRetailCrtRandomSource
{
    [ThreadStatic] private static EnbRetailCrtRandomSource? current;
    public static EnbRetailCrtRandomSource Current=>current??=new();
    public uint State {get;set;}=1;
    public uint Next()
    {
        State=unchecked(State*0x343fdu+0x269ec3u);
        return (State>>16)&0x7fffu;
    }
    // 008C1FC0: low twelve CRT bits, scaled by the authored 00B0C620 float.
    // This is 1/4095 rounded to float, not NextSingle's [0,1) distribution.
    public float NextUnit() => (Next() & 0xfffu) * BitConverter.UInt32BitsToSingle(0x39800801);
    public uint SeedClientStartup(uint clock)
    {
        State=clock;
        return Next(); //7262EB..7262FE consumes the first draw immediately.
    }
    public static uint InitializeClientThread()=>Current.SeedClientStartup(TimeGetTime());
    [DllImport("winmm.dll",EntryPoint="timeGetTime",ExactSpelling=true)]
    private static extern uint TimeGetTime();
}
