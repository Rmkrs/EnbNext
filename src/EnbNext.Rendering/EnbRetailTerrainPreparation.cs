using System.Security.Cryptography;
using System.Text;
using EnbNext.Assets;

namespace EnbNext.Rendering;

public sealed record EnbRetailPreparedTerrain(EnbRetailTerrain Terrain,EnbRetailTerrainPrelight Prelight,
    EnbTextureImage Color,bool ColorAlphaIsIllumination,bool Regenerated)
{
    public EnbRetailTerrainDecorations? Decorations {get;init;}
    public string? DecorationDiagnostic {get;init;}
}

/// <summary>CPU-only preparation for the terrain publication boundary. Inputs,
/// private shadow objects and lighting planes remain owned by this invocation.</summary>
public static class EnbRetailTerrainPreparation
{
    public static EnbRetailPreparedTerrain Prepare(uint sector,EnbRetailFileResolver files,
        EnbMixResourceCatalog catalog,EnbW3dRenderResourceCache prototypes,uint constructionClock,
        int maximumWidth,string cacheDirectory,CancellationToken cancellation=default)
    {
        var resolved=new Dictionary<string,(byte[] Bytes,uint? Stamp)>(StringComparer.Ordinal);
        bool TryRead(string name,out byte[] bytes,out uint? stamp)
        {
            cancellation.ThrowIfCancellationRequested();
            if(resolved.TryGetValue(name,out var existing)){bytes=existing.Bytes;stamp=existing.Stamp;return true;}
            if(!files.TryRead(name,out bytes,out var package,out var loose)){stamp=null;return false;}
            stamp=EnbCurrentRetailFileStamp.TryRead(loose??package!.ArchivePath,out uint value)?value:null;
            resolved.Add(name,(bytes,stamp));return true;
        }
        byte[] Read(string name)=>TryRead(name,out var bytes,out _)?bytes:throw new FileNotFoundException($"Terrain resource missing: {name}");
        string iniName=$"land{sector:D4}.ini";
        using var ini=new EnbRetailTerrainOwnedIni(Read(iniName));
        var terrain=EnbRetailTerrainLoader.Load(sector,ini,Read,maximumWidth);
        // Existing production renderer admission; do not generate unusable inputs.
        if(terrain.Width<2||terrain.Width>4096||!System.Numerics.BitOperations.IsPow2(terrain.Width))
            throw new InvalidDataException("Terrain height map dimensions are not supported by the renderer.");
        if(!float.IsFinite(terrain.HorizontalScale)||terrain.HorizontalScale<=0||!float.IsFinite(terrain.VerticalScale)||
            terrain.PatchGridWidth<1||terrain.PatchGridWidth>terrain.Width||!System.Numerics.BitOperations.IsPow2(terrain.PatchGridWidth))
            throw new InvalidDataException("Terrain scale or patch grid is invalid.");
        byte[] colorBytes=Read(terrain.ColorMap);
        if(!EnbRetailTextureDecoder.TryDecode(colorBytes,terrain.ColorMap,out var color))
            throw new InvalidDataException("Terrain color TGA could not be decoded.");
        color=EnbRetailTerrainColorMap.Reduce(color,terrain.Width,terrain.FlipColorMap);
        bool named=EnbRetailTerrainPrelightCachePolicy.TryGetName(terrain.HeightMap,out string cacheName);
        // Modern cache ownership: generated data never overwrites the retail installation.
        // Sector and decoded width prevent sharing incompatible caches across land INIs/device limits.
        string localName=$"{sector}-{terrain.Width}-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cacheName)))+".pld";
        string localPath=Path.Combine(cacheDirectory,localName);
        byte[]? cached=null;uint? cacheStamp=null;
        if(named)
        {
            try
            {
                cached=File.ReadAllBytes(EnbNext.Installation.EnbPathCase.Resolve(localPath));
                if(EnbCurrentRetailFileStamp.TryRead(localPath,out uint stamp))cacheStamp=stamp;
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){ }
            if(cached is null&&TryRead(cacheName,out var installed,out var installedStamp)){cached=installed;cacheStamp=installedStamp;}
        }
        bool regenerated=false;
        EnbRetailTerrainPrelight Build()
        {
            regenerated=true;cancellation.ThrowIfCancellationRequested();
            byte[] bgr=EnbRetailTerrainColorMap.CreatePrelightPlane(color,terrain.Width,terrain.FlipColorMap);
            float spacing=terrain.HorizontalScale/terrain.Width;
            if(!ini.HasSection("SHADOW0"))
                return EnbRetailTerrainPrelightBuilder.Build(terrain.Width,terrain.Heights,spacing,spacing,terrain.VerticalScale,
                    terrain.Wrapping,bgr,[],terrain.OriginalMaximum,cancellation:cancellation);
            var random=EnbRetailCrtRandomSource.Current;
            using var factory=new EnbRetailTerrainShadowObjectFactory(catalog,prototypes,constructionClock,random.State,files);
            try
            {
                return EnbRetailTerrainPrelightBuilder.BuildFromIni(factory,ini,terrain.Width,terrain.Heights,
                    spacing,spacing,terrain.VerticalScale,terrain.Wrapping,bgr,terrain.OriginalMaximum,cancellation);
            }
            finally{random.State=factory.CrtThreadSeed;}
        }
        void Write(byte[] bytes)
        {
            if(!named)return;
            cancellation.ThrowIfCancellationRequested();
            Directory.CreateDirectory(cacheDirectory);
            string temporary=localPath+"."+Guid.NewGuid().ToString("N")+".tmp";
            try{File.WriteAllBytes(temporary,bytes);cancellation.ThrowIfCancellationRequested();File.Move(temporary,EnbNext.Installation.EnbPathCase.Resolve(localPath),true);}
            finally{try{File.Delete(temporary);}catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){ }}
        }
        TryRead(terrain.HeightMap,out _,out uint? heightStamp);
        TryRead(terrain.ColorMap,out _,out uint? colorStamp);
        TryRead(iniName,out _,out uint? iniStamp);
        // Unknown file metadata cannot establish cache freshness; regenerate from authored inputs.
        var prelight=heightStamp is uint height
            ?EnbRetailTerrainPrelightCache.LoadOrBuild(terrain.Width*terrain.Width,height,cacheStamp,colorStamp,iniStamp,
                ()=>cached??throw new FileNotFoundException(cacheName),Build,Write)
            :Build();
        cancellation.ThrowIfCancellationRequested();
        EnbRetailTerrainDecorations? decorations=null;string? decorationDiagnostic=null;
        try
        {
            decorations=EnbRetailTerrainDecorations.Load(sector,Read,
                ()=>EnbRetailTerrainDecorationGenerator.Generate(terrain,Read("decoration.ini"),cancellation));
        }
        catch(Exception ex) when(ex is not OperationCanceledException){decorationDiagnostic=ex.Message;}
        cancellation.ThrowIfCancellationRequested();
        return new(terrain,prelight,color,colorBytes.Length>=18&&colorBytes[16]==32,regenerated)
            {Decorations=decorations,DecorationDiagnostic=decorationDiagnostic};
    }
}
