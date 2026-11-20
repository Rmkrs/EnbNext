namespace EnbNext.Presentation;

/// <summary>Next multibox Focus mode: the main client fills a monitor area above a band of
/// thumbnails, one per other client, each with the monitor's aspect ratio. The band always
/// holds at least five slots (a full group), centred when fewer clients run.</summary>
public static class EnbFocusModePlacement
{
    public const int MinimumSlots=5;
    public readonly record struct Area(int X,int Y,int Width,int Height);
    public static Area Main(Area monitor,int thumbnails)
        => thumbnails<=0?monitor:monitor with {Height=monitor.Height-Band(monitor,thumbnails)};
    public static Area Thumbnail(Area monitor,int index,int thumbnails)
    {
        if((uint)index>=(uint)thumbnails)throw new ArgumentOutOfRangeException(nameof(index));
        double slot=monitor.Width/(double)Math.Max(MinimumSlots,thumbnails),start=monitor.X+(monitor.Width-slot*thumbnails)/2;
        int left=(int)Math.Round(start+slot*index),right=(int)Math.Round(start+slot*(index+1)),band=Band(monitor,thumbnails);
        return new(left,monitor.Y+monitor.Height-band,right-left,band);
    }
    private static int Band(Area monitor,int thumbnails)=>(int)Math.Round(monitor.Height/(double)Math.Max(MinimumSlots,thumbnails));
}
