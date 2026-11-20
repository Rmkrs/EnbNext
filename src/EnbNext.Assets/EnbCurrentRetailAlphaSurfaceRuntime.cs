using System.Numerics;

namespace EnbNext.Assets;

/// <summary>0099C510's format9 alpha-byte source to ARGB8 consumer.
/// Generic mip compaction stores the first converted blue byte into source9.</summary>
public static class EnbCurrentRetailAlphaSurfaceRuntime
{
    public static void ConvertToArgb(Span<uint> output,int width,int height,int outputPitch,
        Span<byte> input,int sourceWidth,int sourceHeight,int sourcePitch,bool generateMip,Vector3 shift)
    {
        if(width==0 || height==0)return;
        if(width<0 || height<0 || sourceWidth<=0 || sourceHeight<=0 || outputPitch<width || sourcePitch<sourceWidth ||
            output.Length<checked((height-1)*outputPitch+width) || input.Length<checked((sourceHeight-1)*sourcePitch+sourceWidth))
            throw new ArgumentException("Invalid alpha surface buffers.");
        uint Shift(byte alpha)=>EnbCurrentRetailHsvRuntime.ShiftArgb8((uint)alpha<<24,shift);
        if(width!=sourceWidth || height!=sourceHeight || !generateMip)
        {
            for(int y=0;y<height;y++)for(int x=0;x<width;x++)
                output[y*outputPitch+x]=Shift(input[(int)((long)y*sourceHeight/height)*sourcePitch+(int)((long)x*sourceWidth/width)]);
            return;
        }
        if(width==1){output[0]=Shift(input[0]);return;}
        for(int y=0;y<height>>1;y++)for(int x=0;x<width>>1;x++)
        {
            int source=y*2*sourcePitch+x*2,target=y*2*outputPitch+x*2;
            uint a=Shift(input[source]),b=Shift(input[source+1]);
            uint c=Shift(input[source+sourcePitch]),d=Shift(input[source+sourcePitch+1]);
            output[target]=a;output[target+1]=b;output[target+outputPitch]=c;output[target+outputPitch+1]=d;
            input[y*sourcePitch+x]=(byte)a;
        }
    }
}
