using System.Numerics;
using System.Text;
using EnbNext.Assets;
using R=EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.Rendering;

/// <summary>Authored inputs consumed by4634F0 and4691D0. Renderer admission
/// remains a caller decision; this binding retains the retail values.</summary>
public static class EnbRetailTerrainLoader
{
    public sealed class MissingHeightMapException(string name) : FileNotFoundException("Terrain height map absent: " + name, name);

    public static EnbRetailTerrain Load(uint sector,EnbRetailTerrainOwnedIni ini,
        Func<string,byte[]> readResource,int maximumWidth)
    {
        if(!ini.LoadSucceeded)throw new InvalidDataException("Terrain INI did not load.");
        string Text(string section,string key,string fallback="")
            =>Encoding.Latin1.GetString(ini.ReadText(section,key,100,fallback));
        string heightName=Text("HEIGHTMAP","HEIGHT_MAP");
        float horizontal=ini.ReadFloat("HEIGHTMAP","HSCALE",1),vertical=ini.ReadFloat("HEIGHTMAP","VSCALE",1);
        bool wrapping=ini.ReadBoolean("HEIGHTMAP","WRAPPING",false),flip=ini.ReadBoolean("HEIGHTMAP","FLIP_PGM",true);
        byte[] heightBytes;
        try { heightBytes=readResource(heightName); }
        catch(FileNotFoundException) { throw new MissingHeightMapException(heightName); }
        var field=EnbRetailTerrainHeightField.Decode(heightBytes,flip,maximumWidth);
        using var scope=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24,true);
        float heightScale=(float)R.EvaluateControlled(7,(R)(double)vertical,(R)(double)field.OriginalMaximum,0x7f);
        bool sea=ini.ReadBoolean("GENERAL","HASSEA",false);
        string color=Text("LAND","COLOR_MAP"),detail=Text("LAND","DETAIL_MAP");
        var detailScale=new Vector2(ini.ReadFloat("LAND","XDETAIL_SCALE",1),ini.ReadFloat("LAND","YDETAIL_SCALE",1));
        bool flipColor=ini.ReadBoolean("LAND","FLIP_TGA",false);
        int patches=ini.ReadInteger("LAND","Patch_Grid_Width",16),chunk=ini.ReadInteger("LAND","LIGHTMAP_CHUNK",8);
        return new(sector,field.Width,field.Samples,horizontal,heightScale,color,detail,detailScale,flipColor,patches,
            sea,sea?ini.ReadFloat("SEA","SEA_LEVEL",0):0,sea?Text("SEA","SWATCH","water_swatch"):"water_swatch")
        {
            HeightMap=heightName,OriginalMaximum=field.OriginalMaximum,Wrapping=wrapping,LightmapChunk=chunk,
            SeaDetailScales=sea?new(ini.ReadFloat("SEA","DETAIL_SCALE",1),ini.ReadFloat("SEA","DETAIL_SCALE2",1),
                ini.ReadFloat("SEA","DETAIL_SCALE3",1),ini.ReadFloat("SEA","DETAIL_SCALE4",1)):Vector4.One,
        };
    }
}
