using System.Globalization;
using System.Numerics;
namespace EnbNext.Assets;

public sealed record EnbRetailLandingPattern(Vector3 StartOffset,Vector3 InitialUp,float SpeedForward,float SpeedDown)
{
    public static EnbRetailLandingPattern Resolve(EnbRetailIniDocument ini,uint sector)
    {
        if(!ini.TryGetSection(sector.ToString(CultureInfo.InvariantCulture),out var section))
            ini.TryGetSection("Default",out section);
        float Read(string key,float fallback)=>section is not null && section.TryGetValue(key,out var text) &&
            float.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out float value) && float.IsFinite(value)?value:fallback;
        return new(new(Read("SX",-1000),Read("SY",-1000),Read("SZ",5000)),
            new(Read("UX",0),Read("UY",0),Read("UZ",1)),Read("SpeedForward",100),Read("SpeedDown",100));
    }
}
