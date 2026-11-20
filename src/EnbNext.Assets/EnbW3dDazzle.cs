using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Globalization;
namespace EnbNext.Assets;

public sealed record EnbW3dDazzle(string Name, string Type)
{
    public static IReadOnlyList<EnbW3dDazzle> Decode(ReadOnlySpan<byte> bytes)
    {
        var result = new List<EnbW3dDazzle>();
        for(int at=0; at+8<=bytes.Length;)
        {
            uint kind=BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);
            int size=(int)(BinaryPrimitives.ReadUInt32LittleEndian(bytes[(at+4)..])&0x7fffffff);
            if(size>bytes.Length-at-8)throw new InvalidDataException("Truncated W3D chunk");
            if(kind==0x900)
            {
                string? name=null,type=null;
                for(int p=at+8,end=p+size;p<end;)
                {
                    if(end-p<8)throw new InvalidDataException("Truncated dazzle header");
                    uint k=BinaryPrimitives.ReadUInt32LittleEndian(bytes[p..]);
                    int n=(int)(BinaryPrimitives.ReadUInt32LittleEndian(bytes[(p+4)..])&0x7fffffff);
                    if(n<1||n>end-p-8||bytes[p+8+n-1]!=0)throw new InvalidDataException("Invalid dazzle string");
                    string value=Encoding.ASCII.GetString(bytes.Slice(p+8,n-1));
                    if(k==0x901)name=value; else if(k==0x902)type=value;
                    p+=8+n;
                }
                if(string.IsNullOrWhiteSpace(name)||string.IsNullOrWhiteSpace(type))throw new InvalidDataException("Incomplete dazzle");
                result.Add(new(name,type));
            }
            at+=8+size;
        }
        return result;
    }
}

public sealed record EnbDazzleFlare(float Location,float Size,Vector4 Uv)
{
    public Vector3 Color { get; init; } = Vector3.One;
}
public sealed record EnbDazzleDefinition(string Type,float Radius,string Texture,string HaloTexture,string FlareTexture,
    float Area,float SizePower,float IntensityPower,float Intensity,float HaloIntensity,float HaloPower,
    Vector2 Scale,Vector2 HaloScale,Vector3 Color,Vector3 HaloColor,IReadOnlyList<EnbDazzleFlare> Flares)
{
    public bool UseCameraTranslation { get; init; }
    public float DirectionArea { get; init; }
    public Vector3 Direction { get; init; }
    public float HistoryWeight { get; init; }
    public float BlinkPeriod { get; init; }
    public float BlinkOnTime { get; init; }
    public float FadeoutStart { get; init; } = 25;
    public float FadeoutEnd { get; init; } = 50;

    // Current retail 0096AA9D/0096ABED: squared comparisons, linear distance fade.
    public float DistanceIntensityFactor(float distanceSquared)
    {
        if (!UseCameraTranslation || distanceSquared <= FadeoutStart * FadeoutStart) return 1;
        if (distanceSquared > FadeoutEnd * FadeoutEnd) return 0;
        return 1 - (MathF.Sqrt(distanceSquared) - FadeoutStart) / (FadeoutEnd - FadeoutStart);
    }

    // 0096AC30 defaults.
    public static EnbDazzleDefinition Load(EnbRetailIniDocument ini,string type)
        => LoadDefinition(ini,type,false);

    /// <summary>0096ED30 searches registered names byte-for-byte and replaces
    /// an unmatched type with index zero. INI sections alone are not the registry.</summary>
    public static EnbDazzleDefinition Resolve(EnbRetailIniDocument ini,string type)
    {
        var names=RegisteredNames(ini,"Dazzles_List");
        if(names.Length==0)throw new InvalidDataException("No registered dazzle definitions");
        string selected=names.FirstOrDefault(name=>string.Equals(name,type,StringComparison.Ordinal))??names[0];
        return LoadDefinition(ini,selected,true);
    }

    private static string[] RegisteredNames(EnbRetailIniDocument ini,string section)
    {
        if(!ini.TryGetSection(section,out var entries))return [];
        // 0096AC7E/0096B018 use EntryName(index), then read that entry's value;
        // numeric-looking keys do not determine the registry order.
        return entries.Values.Select(value=>value.Trim().Trim('"')).ToArray();
    }

    private static EnbDazzleDefinition LoadDefinition(EnbRetailIniDocument ini,string type,bool registered)
    {
        if(!ini.TryGetSection(type,out var s))throw new InvalidDataException("Missing dazzle "+type);
        float F(string key,float fallback)=>EnbRetailIniDocument.TryReadFloat(s,key,out float v)&&float.IsFinite(v)?v:fallback;
        string S(string key)=>s.TryGetValue(key,out var v)?v.Trim().Trim('"'):"";
        bool translated = F("UseCameraTranslation",1)!=0;
        float fadeStart = F("FadeoutStart",25), fadeEnd = F("FadeoutEnd",50);
        if(translated && (fadeStart < 0 || fadeEnd <= fadeStart || !float.IsFinite(fadeEnd * fadeEnd)))
            throw new InvalidDataException("Dazzle requires unsupported fade semantics: "+type);
        float[] V(IReadOnlyDictionary<string,string> section,string key,int count,float[] fallback)
        {
            if(!section.TryGetValue(key,out var text))return fallback;
            var values=text.Split(',').Select(x=>float.Parse(x,CultureInfo.InvariantCulture)).ToArray();
            if(values.Length!=count||values.Any(x=>!float.IsFinite(x)))throw new InvalidDataException("Invalid "+key);
            return values;
        }
        Vector3 C(string key){var v=V(s,key,3,[1,1,1]);return new(v[0],v[1],v[2]);}
        var direction=V(s,"DazzleDirection",3,[0,0,0]);
        string lensName=S("LensflareName");
        IReadOnlyDictionary<string,string>? lens=null;
        // 0096A84A stores FFFFFFFF when no registered lens name matches.
        // 0096D20B rejects that index and still emits halo/dazzle quads.
        if(!string.IsNullOrWhiteSpace(lensName)&&(!registered||RegisteredNames(ini,"Lensflares_List").Contains(lensName,StringComparer.Ordinal)))
            ini.TryGetSection(lensName,out lens);
        float L(string key,float fallback)=>lens is not null&&EnbRetailIniDocument.TryReadFloat(lens,key,out var v)?v:fallback;
        int count=(int)L("FlareCount",0);
        if(count<0||count>64)throw new InvalidDataException("Invalid flare count");
        var flares=new List<EnbDazzleFlare>();
        for(int i=1;i<=count;i++)
        {
            var uv=V(lens!,"FlareUV"+i,4,[0,0,1,1]);
            var color=V(lens!,"FlareColor"+i,3,[1,1,1]);
            flares.Add(new(L("FlareLocation"+i,0),L("FlareSize"+i,1),
                new(uv[0],uv[1],uv[2],uv[3])) { Color=new(color[0],color[1],color[2]) });
        }
        return new(type,F("Radius",1),S("DazzleTextureName"),S("HaloTextureName"),lens?.GetValueOrDefault("TextureName","")??"",
            F("DazzleArea",.05f),F("DazzleSizePow",.9f),F("DazzleIntensityPow",.9f),F("DazzleIntensity",.9f),
            F("HaloIntensity",.95f),F("HaloIntensityPow",0),new(F("DazzleScaleX",100),F("DazzleScaleY",25)),
            new(F("HaloScaleX",2),F("HaloScaleY",2)),C("DazzleColor"),C("HaloColor"),flares)
            { UseCameraTranslation = translated, FadeoutStart = fadeStart, FadeoutEnd = fadeEnd, HistoryWeight=F("HistoryWeight",.5f),
                BlinkPeriod=F("BlinkPeriod",0),BlinkOnTime=F("BlinkOnTime",0),
                DirectionArea=F("DazzleDirectionArea",.5f),Direction=new(direction[0],direction[1],direction[2]) };
    }
}
