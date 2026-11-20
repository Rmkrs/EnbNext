using System.Numerics;
using EnbNext.Assets;

namespace EnbNext.Rendering;

public sealed record EnbRetailRingSample(EnbRetailRingGeometry Geometry, Vector3 Color, float Opacity)
{
    public Vector2 SecondRadii { get; init; }
}

public readonly record struct EnbRetailRingValues(Vector3 Color,float Opacity,Vector2 FirstRadii,Vector2 SecondRadii,int Repeats)
{
    public EnbRetailRingSample? Sample(int detailLevel)
    {
        int level=EnbSphereRingDetailBudgetSemantics.ClampRingLevel(detailLevel);
        if(level==0)return null;
        int segments=(int)EnbSphereRingDetailBudgetSemantics.RingLevelCosts[level]/2;
        return new(EnbRetailRingGeometry.Build(segments,FirstRadii,SecondRadii,Repeats),Color,Opacity){SecondRadii=SecondRadii};
    }
}

// Ring resource shared by standalone and aggregate effect owners.
// Sampling takes native normalized progress; lifecycle/looping is owned by its caller.
public sealed class EnbRetailRingAsset
{
    public EnbCurrentRetailRingHeader Header { get; }
    public IReadOnlyDictionary<uint,IReadOnlyList<EnbCurrentRetailEffectPropertyKey>> Tracks { get; }

    private EnbRetailRingAsset(EnbCurrentRetailRingHeader header,
        Dictionary<uint,IReadOnlyList<EnbCurrentRetailEffectPropertyKey>> tracks)
    { Header=header;Tracks=tracks; }

    // Ring virtual +164, 00912480: scale current multipliers and float2 keys,
    // not the authored radii or object transform. Keep the shared source immutable.
    public EnbRetailRingAsset ScaleRadiusMultipliers(float scale)
    {
        if(!float.IsFinite(scale))throw new ArgumentOutOfRangeException(nameof(scale));
        if(scale==1)return this;
        var tracks=Tracks.ToDictionary(p=>p.Key,p=>p.Value);
        foreach(uint id in new uint[]{4,5})
            if(tracks.TryGetValue(id,out var keys))
                tracks[id]=keys.Select(k=>new EnbCurrentRetailEffectPropertyKey(k.Value.Select(v=>v*scale).ToArray(),k.Time)).ToArray();
        return new(Header with {FirstRadiusMultiplier=Header.FirstRadiusMultiplier*scale,
            SecondRadiusMultiplier=Header.SecondRadiusMultiplier*scale},tracks){Definition=Definition};
    }

    public EnbRetailRingValues InitialValues=>new(Header.Color,Header.Opacity,
        Header.FirstRadii*Header.FirstRadiusMultiplier,Header.SecondRadii*Header.SecondRadiusMultiplier,Header.Repeats);

    // 0048E0F0 adjusts color keys before interpolation, or the static color
    // when no keys exist. Texture replacement remains the binding resolver's job.
    // The factory must apply its existing prototype threshold before calling.
    public EnbRetailRingAsset ShiftColors(Vector3 shift)
    {
        if(shift==Vector3.Zero)return this;
        var header=Header;
        var tracks=Tracks.ToDictionary(p=>p.Key,p=>p.Value);
        if(tracks.TryGetValue(2,out var colors) && colors.Count>0)
            tracks[2]=colors.Select(key=>
            {
                var rgb=EnbCurrentRetailHsvRuntime.ShiftRgb(new(key.Value),shift);
                return new EnbCurrentRetailEffectPropertyKey([rgb.X,rgb.Y,rgb.Z],key.Time);
            }).ToArray();
        else header=header with {Color=EnbCurrentRetailHsvRuntime.ShiftRgb(header.Color,shift)};
        return new(header,tracks){Definition=Definition};
    }

    internal EnbRetailRingAsset WithName(string name)
        => new(Header with {Name=name},Tracks.ToDictionary(p=>p.Key,p=>p.Value)){Definition=Definition};

    public EnbCurrentRetailRingDefinition? Definition {get;private init;}

    public static EnbRetailRingAsset Decode(ReadOnlySpan<byte> bytes)
        => FromDefinition(EnbCurrentRetailRingDefinition.Decode(bytes));

    public static EnbRetailRingAsset FromDefinition(EnbCurrentRetailRingDefinition definition)
        => new(definition.Header,definition.Tracks.ToDictionary(p=>p.Key,p=>p.Value)){Definition=definition};

    public EnbRetailRingSample? Sample(float progress,int detailLevel)
        => EvaluateValues(progress).Sample(detailLevel);

    public EnbRetailRingValues EvaluateValues(float progress)
    {
        if(!float.IsFinite(progress) || progress<0)throw new ArgumentOutOfRangeException(nameof(progress));
        float[] color=Evaluate(2,progress,[Header.Color.X,Header.Color.Y,Header.Color.Z]);
        float opacity=Evaluate(3,progress,[Header.Opacity])[0];
        float[] first=Evaluate(4,progress,[Header.FirstRadiusMultiplier.X,Header.FirstRadiusMultiplier.Y]);
        float[] second=Evaluate(5,progress,[Header.SecondRadiusMultiplier.X,Header.SecondRadiusMultiplier.Y]);
        return new(new(color[0],color[1],color[2]),opacity,Header.FirstRadii*new Vector2(first[0],first[1]),
            Header.SecondRadii*new Vector2(second[0],second[1]),Header.Repeats);
    }

    private float[] Evaluate(uint id,float time,float[] fallback)
    {
        if(!Tracks.TryGetValue(id,out var keys) || keys.Count==0)return fallback;
        if(time>=keys[^1].Time)return (float[])keys[^1].Value.Clone();
        int left=0;
        while(left+1<keys.Count && keys[left+1].Time<=time)left++;
        var a=keys[left];var b=keys[left+1];
        double fraction=((double)time-a.Time)/((double)b.Time-a.Time);
        var values=new float[a.Value.Length];
        for(int i=0;i<values.Length;i++)values[i]=(float)(a.Value[i]+fraction*((double)b.Value[i]-a.Value[i]));
        return values;
    }

}
