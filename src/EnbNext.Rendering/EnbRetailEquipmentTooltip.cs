using System.Globalization;
using R = EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.Rendering;

/// <summary>68E360 retained recharge time and 68F510 action-slot tooltip suffix.</summary>
public static class EnbRetailEquipmentTooltip
{
    public static long Remaining(long? readyTime, long now)
        => readyTime is {} ready && ready>unchecked((uint)now) ? ready-unchecked((uint)now) : 0;

    public static string Suffix(float distance,float range,long remaining,
        EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        string text="";
        if(distance>0)
        {
            using var scope=new EnbRetailLineArithmetic.Scope(precision,true);
            text=string.Format(CultureInfo.InvariantCulture,"Range: {0:F2}k / {1:F2}k\n",
                (double)((R)distance*(R).001f),(double)((R)range*(R).001f));
        }
        if(remaining==0)return text;
        long seconds=remaining<1000?1:remaining/1000;
        long hours=seconds/3600,minutes=seconds%3600/60;
        seconds%=60;
        // These are the actual executable literals at B90540/B90554, including "02".
        return text+(hours!=0?FormattableString.Invariant($"Time: {Math.Min(hours,int.MaxValue)}:02{minutes}:02{seconds}\n"):
            minutes!=0?FormattableString.Invariant($"Time: {minutes}:02{seconds}\n"):
            seconds==1?"Time: 1 second\n":FormattableString.Invariant($"Time: {seconds} seconds\n"));
    }
}
