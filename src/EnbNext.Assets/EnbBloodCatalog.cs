using System.Globalization;
using System.Numerics;

namespace EnbNext.Assets;

public sealed record EnbBloodDefinition(string Section, string[] Levels, Vector3 Hsv, float Scale)
{
    public string Sound { get; init; } = "";
    public string LoopingSound { get; init; } = "";
}

/// <summary>Installed blood.ini selection from retail 008117D0.</summary>
public sealed class EnbBloodCatalog(EnbRetailIniDocument document)
{
    public EnbBloodDefinition Resolve(int baseAsset, bool organic)
    {
        string section=baseAsset.ToString(CultureInfo.InvariantCulture);
        if(document.TryGetSection(section,out var entry))
        {
            int type=EnbRetailIniDocument.TryReadInt(entry,"BloodType",out var parsed)?parsed:0;
            section=$"Blood{type}";
        }
        if(!document.TryGetSection(section,out entry))section=organic?"Blood1":"Blood0";
        if(!document.TryGetSection(section,out entry))throw new InvalidDataException($"blood.ini missing {section}");
        float Read(string key,float fallback)=>entry.ContainsKey(key)
            ? EnbRetailIniDocument.TryReadFloat(entry,key,out var value)&&float.IsFinite(value)?value:throw new InvalidDataException($"Invalid blood.ini {section}/{key}")
            :fallback;
        return new(section,Enumerable.Range(0,3).Select(i=>entry.GetValueOrDefault($"Level{i}","mobdamred1")).ToArray(),
            new(Read("Hue",0),Read("Sat",0),Read("Val",0)),Read("Scale",1))
            {Sound=entry.GetValueOrDefault("Sound",""),LoopingSound=entry.GetValueOrDefault("LoopingSound","")};
    }

    // 006FE1E0: organic ShieldPercent; thresholds are damage, not remaining health.
    public static int DamageLevel(float? remaining)
    {
        float damage=remaining is float value && float.IsFinite(value)?Math.Clamp(1-value,0,1):0;
        return damage<=0?0:damage<.25f?1:damage<.5f?2:3;
    }

    //811C1E-811E43 acquires and attaches every selection in order. A later
    //selection may remove the same bone's child, but cannot undo construction.
    public static IReadOnlyList<(int Bone,int Tier)> SelectAttachments(uint seed,int count,int level)
    {
        var result=new List<(int,int)>();
        if(count<=0)return result;
        uint max=(uint)(count-1),mask=1;
        while(mask<max)mask=(mask<<1)|1;
        for(int tier=0;tier<level;tier++)for(int copy=0;copy<2;copy++)
        {
            uint bone=0;
            if(max!=0)do{seed=unchecked(seed*0x41c64e6d+0x3039);bone=(seed>>10)&mask&0x7fff;}while(bone>max);
            result.Add(((int)bone,tier));
        }
        return result;
    }
}
