namespace EnbNext.Rendering;

/// <summary>Retail grid-atlas initializer: a side of 2^width cells, the frame count defaulting to
/// every cell, the start offset wrapped to the frame count, and a frame period of 1000/|fps| ms;
/// negative rates run backward from the last frame.</summary>
public static class EnbCurrentRetailMapperAtlasRuntime
{
    public static EnbRetailAtlasInitialState Initialize(float fps,int width,int last,int offset)
    {
        uint side=1u<<width;
        uint count=last==0?side*side:unchecked((uint)last);
        // An overflowing side (retail divides by zero there) starts at the first frame.
        uint normalized=count==0?0:unchecked((uint)offset)%count;
        float cell=(float)(1.0/side);
        if(fps==0)return new(0,1,cell,width,count,normalized,normalized);
        uint period=Truncate((float)(1000.0/Math.Abs(fps)));
        return fps>0
            ?new(1,period,cell,width,count,normalized,normalized)
            :new(-1,period,cell,width,count,normalized,count-normalized-1);
    }

    // CRT ftol: truncation to a 64-bit integer, of which retail keeps the low 32 bits.
    private static uint Truncate(float value)
        =>Math.Abs((double)value)<9.2233720368547758E18?unchecked((uint)(long)value):0;
}

public readonly record struct EnbRetailAtlasInitialState(int Direction,uint MillisecondsPerFrame,
    float CellSize,int Log2Width,uint FrameCount,uint NormalizedOffset,uint CurrentFrame);
