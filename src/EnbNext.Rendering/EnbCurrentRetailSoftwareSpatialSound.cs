using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>client.exe 009FFCC0/009FFBF0/009FFDF0 software spatial voice;
/// installed Mss32.dll 21114100 publishes the two channel coefficients.</summary>
public static class EnbCurrentRetailSoftwareSpatialSound
{
    public static (float Left,float Right) Gains(Vector3 worldDelta,Vector3 canonicalViewDelta,
        float volume,float fullVolumeRadius,float dropOffRadius)
    {
        float largest=MathF.Abs(worldDelta.X),middle=MathF.Abs(worldDelta.Y),remaining=MathF.Abs(worldDelta.Z);
        if(largest<middle)(largest,middle)=(middle,largest);
        if(largest<remaining)(largest,remaining)=(remaining,largest);
        if(middle<remaining)middle=remaining;
        float distance=(float)((double)remaining*.25+middle*.34375+largest);
        double attenuation=distance<=fullVolumeRadius?1:Math.Clamp(1-((double)distance-fullVolumeRadius)/(dropOffRadius-fullVolumeRadius),0,1);
        int level=Math.Clamp((int)(attenuation*volume*127),0,127);
        // Retail listener X is forward and Y is left. Canonical view X is
        // right, Z is back. The native sine table is interpolated after atan2.
        float phase=(float)(Math.Atan2(-canonicalViewDelta.X,-canonicalViewDelta.Z)*BitConverter.UInt32BitsToSingle(0x4322F983));
        int index=(int)Math.Floor(phase);double fraction=(double)phase-index;
        double sine=(1-fraction)*EnbCurrentRetailTrigonometricTables.SineEntry(index&1023)+
            fraction*EnbCurrentRetailTrigonometricTables.SineEntry((index+1)&1023);
        int pan=Math.Clamp((int)((.5-sine*.5)*127),0,127);
        // 2114C374: 0,2,...,124,128,128,...; coefficient saturates to 2047.
        static float Channel(int position,int level)
        {
            int coefficient=((position>=63?128:position*2)*level)>>3;
            return (coefficient>2031?2047:coefficient)/2048f;
        }
        return(Channel(127-pan,level),Channel(pan,level));
    }
}
