using System.Numerics;
using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>Terrain height, normal, decoration patch and segment queries for one terrain instance,
/// held in the terrain's render-prepared slot. See <see cref="EnbTerrainHeightField"/>.</summary>
public sealed class EnbRetailTerrainSampler : EnbRenderPrepared
{
    public static EnbRetailTerrainSampler For(EnbRetailTerrain terrain)=>(EnbRetailTerrainSampler)OwnerCache.Get(ref terrain.RenderPrepared,terrain,
        static t=>(EnbRenderPrepared)new EnbRetailTerrainSampler(t.Width,t.Heights,t.HorizontalScale/t.Width,t.HorizontalScale/t.Width,t.VerticalScale,wrapping:t.Wrapping));
    private readonly EnbTerrainHeightField field;

    public EnbRetailTerrainSampler(int width,ReadOnlySpan<ushort> heights,float xScale,float yScale,float heightScale,
        EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24,bool wrapping=false)
    {
        if(width<=0||!BitOperations.IsPow2(width)||heights.Length!=checked(width*width))
            throw new ArgumentException("Terrain sampler requires a square power-of-two height field.");
        field=new(width,heights,xScale,yScale,heightScale,wrapping);
    }
    /// <summary>Segment against the surface: first hit fraction and the start-under-surface flag.</summary>
    public (float Fraction,bool StartInside) CastSegment(Vector3 start,Vector3 end,float fraction=1,bool startInside=false)
        =>field.CastSegment(start,end,fraction,startInside);
    public float Height(Vector2 location)=>field.Height(location);
    public Vector3 Normal(Vector2 location)=>field.Normal(location);
    public (float Height,Vector3 Normal) Sample(Vector2 location)=>(field.Height(location),field.Normal(location));
    public int DecorationPatch(Vector2 location,int gridWidth)=>field.Patch(location,gridWidth);
}
