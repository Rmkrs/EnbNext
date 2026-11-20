using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>46E120: SHADOW occluders raise the terrain samples they cover.</summary>
public static class EnbRetailTerrainShadowEffects
{
    /// <summary>The terrain height field as 46E120 addresses it: samples at
    /// ((y &amp; mask) &lt;&lt; shift) + (x &amp; mask), centred on width/2.</summary>
    public sealed class HeightField(ushort[] samples,int width,float xScale,float yScale,float heightScale,bool wrapping)
    {
        private readonly int mask=width-1,shift=32-BitOperations.LeadingZeroCount((uint)width-1),half=width/2;
        public int Width=>width;
        public bool Wrapping=>wrapping;
        // 463E00: world x/y to fractional grid coordinates.
        public (float X,float Y) ToGrid(Vector3 world)=>(world.X/xScale+half,world.Y/yScale+half);
        // 463E70
        public Vector3 Position(int x,int y)
        {
            int index=Index(x,y);
            return new((x-half)*xScale,(y-half)*yScale,((uint)index<(uint)samples.Length?samples[index]:0)*heightScale);
        }
        // 463D90: clamped to 65535 and truncated into the 16-bit sample.
        public void SetHeight(float height,int x,int y)
        {
            float value=height/heightScale;
            if(value>65535f)value=65535f;
            int index=Index(x,y);
            if((uint)index<(uint)samples.Length)samples[index]=unchecked((ushort)Truncate(value));
        }
        private int Index(int x,int y)=>((mask&y)<<shift)+(mask&x);
    }

    // CRT _ftol: chop to 64 bits, integer indefinite when out of range; callers keep the low word.
    private static uint Truncate(double value)
        =>double.IsNaN(value)||!(Math.Abs(value)<9223372036854775808d)?0u:unchecked((uint)(long)value);

    /// <summary>46E120: each occluder's world box limits a sample rectangle; a sample below the
    /// box top casts a vertical segment from the top down to it, and a hit raises the sample to
    /// the contact height and marks it covered. Wrapping terrains are left unchanged.</summary>
    public static void Apply(HeightField terrain,IReadOnlyList<EnbRetailShadowObject> objects,byte[] coverage,
        CancellationToken cancellation=default)
    {
        if(terrain.Wrapping)return;
        int width=terrain.Width;
        foreach(var owner in objects)
        {
            cancellation.ThrowIfCancellationRequested();
            var (center,extent)=owner.WorldBox();
            Vector3 low=new(center.X-extent.X,center.Y-extent.Y,center.Z-extent.Z);
            float top=0f>low.Z?0f:low.Z;
            var (x0,y0)=terrain.ToGrid(low);
            Vector3 high=new(center.X+extent.X,extent.Y+center.Y,extent.Z+center.Z);
            if(!(top>high.Z))top=high.Z;
            var (x1,y1)=terrain.ToGrid(high);
            if(!(x1>=x0)||!(y1>=y0))continue;
            int left=(int)Truncate(Math.Floor(x0)),right=(int)Truncate(Math.Ceiling(x1));
            int bottom=(int)Truncate(Math.Floor(y0)),upper=(int)Truncate(Math.Ceiling(y1));
            if(left<0)left=0;
            if(right>=width)right=width-1;
            if(bottom<0)bottom=0;
            if(upper>=width)upper=width-1;
            for(int y=bottom;y<=upper;y++)
                for(int x=left;x<=right;x++)
                {
                    var position=terrain.Position(x,y);
                    if(position.Z>=top)continue;
                    if(EnbRetailTerrainShadowObjectFactory.CastDown(owner,position,top) is not float contact)continue;
                    terrain.SetHeight(contact>position.Z?contact:position.Z,x,y);
                    coverage[y*width+x]=1;
                }
        }
    }
}
