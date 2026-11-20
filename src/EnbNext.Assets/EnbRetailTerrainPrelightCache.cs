namespace EnbNext.Assets;

/// <summary>469BF4..469D06's present-PGM cache branch. File resolution and
/// prelight construction retain their caller-owned resource and random state.</summary>
public static class EnbRetailTerrainPrelightCache
{
    public static EnbRetailTerrainPrelight LoadOrBuild(int sampleCount,uint heightStamp,
        uint? cacheStamp,uint? colorStamp,uint? iniStamp,Func<byte[]> read,
        Func<EnbRetailTerrainPrelight> build,Action<byte[]> write)
    {
        if(EnbRetailTerrainPrelightCachePolicy.ShouldRead(heightStamp,cacheStamp,colorStamp,iniStamp))
        {
            try{return EnbRetailTerrainPrelight.Decode(read(),sampleCount);}
            catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                //46EB60 reports failed open/read/header checks to this branch.
            }
        }
        var result=build();
        byte[] bytes=result.Encode();
        try{write(bytes);}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {
            //46ECF0 returns no status; the generated planes remain in use.
        }
        return result;
    }
}
