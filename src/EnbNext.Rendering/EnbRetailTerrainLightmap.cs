using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>0046DA70's manual lightmap update: initial full image, authored chunks,
/// then publication and capture of the next light state on a separate call.</summary>
public sealed class EnbRetailTerrainLightmap
{
    private readonly EnbRetailTerrain terrain;
    private readonly EnbRetailTerrainPrelight cache;
    private readonly EnbTextureImage color;
    private readonly bool alpha;
    private readonly int chunk;
    private readonly EnbRetailLinePrecision precision;
    private readonly EnbColor32[][] buffers;
    private EnbRetailTerrainLightState? retainedLight;
    private EnbRetailTerrainLighting.Prepared? prepared;
    public int VisibleBuffer {get;private set;}
    public int PendingBuffer {get;private set;}=1;
    public int CursorX {get;private set;}
    public int CursorY {get;private set;}
    public uint Generation {get;private set;}
    public EnbRetailTerrainLightState? VisibleLight {get;private set;}
    public ReadOnlyMemory<EnbColor32> Pixels=>buffers[VisibleBuffer];

    public EnbRetailTerrainLightmap(EnbRetailTerrain terrain,EnbRetailTerrainPrelight cache,
        EnbTextureImage color,bool colorAlphaIsIllumination,int chunk,
        EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        int count=checked(terrain.Width*terrain.Width);
        if(cache.NormalIndices.Length!=count||color.Width!=terrain.Width||color.Height!=terrain.Width)
            throw new InvalidDataException("Terrain lighting/color dimensions differ from the height field.");
        // Native advances by chunk and tests equality with width. Contain an
        // unsafe authored rectangle locally instead of indexing outside the map.
        if(chunk<=0||chunk>terrain.Width||terrain.Width%chunk!=0)
            throw new InvalidDataException("Terrain lightmap chunks exceed the image or cannot reach its boundary.");
        this.terrain=terrain;this.cache=cache;this.color=color;alpha=colorAlphaIsIllumination;
        this.chunk=chunk;this.precision=precision;
        buffers=[new EnbColor32[count],new EnbColor32[count]];
    }

    public EnbTextureImage CreateTexture(bool sixteenBit)
        =>sixteenBit?EnbTextureImage.CreateCurrentRetailRuntimeArgb4444(terrain.Width,terrain.Width,buffers[VisibleBuffer])
            :EnbTextureImage.CreateCurrentRetailRuntimeArgb8(terrain.Width,terrain.Width,buffers[VisibleBuffer]);

    /// <returns>True only when the visible texture changes.</returns>
    public bool Update(EnbRetailTerrainLightState light)
    {
        if(retainedLight is null)
        {
            Capture(light);
            EnbRetailTerrainLighting.ShadeRegion(terrain,cache,color,retainedLight!,alpha,prepared!,buffers[PendingBuffer],
                0,0,terrain.Width,terrain.Width,precision);
            Publish();return true;
        }
        if(CursorY==terrain.Width)
        {
            Publish();CursorX=CursorY=0;Capture(light);return true;
        }
        EnbRetailTerrainLighting.ShadeRegion(terrain,cache,color,retainedLight,alpha,prepared!,buffers[PendingBuffer],
            CursorX,CursorY,CursorX+chunk,CursorY+chunk,precision);
        CursorX+=chunk;
        if(CursorX==terrain.Width){CursorX=0;CursorY+=chunk;}
        return false;
    }

    private void Capture(EnbRetailTerrainLightState light)
    {
        retainedLight=light;prepared=EnbRetailTerrainLighting.Prepare(cache,light,precision);
    }
    private void Publish(){Generation++;(VisibleBuffer,PendingBuffer)=(PendingBuffer,VisibleBuffer);VisibleLight=retainedLight;}
}
