namespace EnbNext.Rendering;

/// <summary>006D8B90/006D8DC0 text-atlas ownership and 006D64C0 chunk geometry.</summary>
public static class EnbGalaxyMapTextLayout
{
    // 006D8270 calls A26D75 (floor), adds double .5, and uploads Z=.001.
    // Reference-resolution divisors are one for the galaxy owners.
    public static System.Numerics.Vector3 RenderOrigin(System.Numerics.Vector3 position,float pixelWidth,float pixelHeight)
        =>new((float)((Math.Floor(position.X/pixelWidth)+.5)*pixelWidth),
            (float)((Math.Floor(position.Y/pixelHeight)+.5)*pixelHeight),.001f);

    public static int AtlasSize(int width,int height,int previous=32)
    {
        if(width<1 || height<1 || previous<1) throw new ArgumentOutOfRangeException();
        int required=(int)Math.Ceiling(Math.Sqrt((double)width*height));
        int size=1;
        while(size<required) size=checked(size*2);
        if((size/height)*size<width) size=checked(size*2);
        while(size<height) size=checked(size*2);
        return Math.Max(previous,size);
    }

    public static IEnumerable<(int PixelOffset,int PixelWidth,float Left,float Width)> Chunks(int width,int size,float scale,float left)
    {
        for(int offset=0;offset<width;offset+=size)
        {
            int pixels=Math.Min(size,width-offset);
            yield return (offset,pixels,left+offset*scale,Math.Max(.5f,pixels*scale));
        }
    }
}
