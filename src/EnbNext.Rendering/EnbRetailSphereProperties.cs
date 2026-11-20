using System.Numerics;
using EnbNext.Assets;

namespace EnbNext.Rendering;

public readonly record struct EnbRetailSphereValues(Vector3 Color,float Opacity,Vector3 Scale,Quaternion Orientation,float Strength);

// Bounded property sampler. Does not admit a sphere resource or drive its clock.
public sealed class EnbRetailSphereProperties
{
    private readonly EnbRetailSphereValues _initial;
    public EnbRetailSphereValues InitialValues=>_initial;
    private readonly Dictionary<uint,EnbCurrentRetailEffectPropertyKey[]> _tracks=new();

    private EnbRetailSphereProperties(EnbRetailSphereValues initial,Dictionary<uint,EnbCurrentRetailEffectPropertyKey[]> tracks)
    { _initial=initial;_tracks=tracks; }

    //0090D340 changes the retained scale and every authored scale key before
    //the0090D6E0 sampler runs. Scaling its sampled result can round differently.
    public EnbRetailSphereProperties Scale(float factor)
    {
        if(factor==1)return this;
        var tracks=_tracks.ToDictionary(p=>p.Key,p=>p.Value);
        if(tracks.TryGetValue(4,out var scales))
            tracks[4]=scales.Select(k=>new EnbCurrentRetailEffectPropertyKey(
                k.Value.Select(v=>v*factor).ToArray(),k.Time)).ToArray();
        return new(_initial with {Scale=_initial.Scale*factor},tracks);
    }

    // 0048DD00 modifies keyed RGB before sampling; only the keyless branch
    // rewrites the static color. Private unchanged tracks can safely be shared.
    public EnbRetailSphereProperties ShiftColors(Vector3 shift)
    {
        if(shift==Vector3.Zero)return this;
        var initial=_initial;
        var tracks=_tracks.ToDictionary(p=>p.Key,p=>p.Value);
        if(tracks.TryGetValue(2,out var colors)&&colors.Length>0)
            tracks[2]=colors.Select(k=>
            {
                var rgb=EnbCurrentRetailHsvRuntime.ShiftRgb(new(k.Value),shift);
                return new EnbCurrentRetailEffectPropertyKey([rgb.X,rgb.Y,rgb.Z],k.Time);
            }).ToArray();
        else initial=initial with {Color=EnbCurrentRetailHsvRuntime.ShiftRgb(initial.Color,shift)};
        return new(initial,tracks);
    }

    public EnbRetailSphereProperties(EnbCurrentRetailSphereHeader header,
        IReadOnlyDictionary<uint,IReadOnlyList<EnbCurrentRetailEffectPropertyKey>> tracks)
    {
        EnbCurrentRetailSphereDefinition.ValidateProperties(header,tracks);
        var p=header.Parameters60;
        _initial=new(header.Color,header.Opacity,header.Parameter54,new(p[0],p[1],p[2],p[3]),p[4]);
        foreach(var (id,keys) in tracks)
            _tracks[id]=keys.Select(k=>new EnbCurrentRetailEffectPropertyKey((float[])k.Value.Clone(),k.Time)).ToArray();
    }

    public EnbRetailSphereValues Evaluate(float progress)
    {
        if(!float.IsFinite(progress)||progress<0)throw new ArgumentOutOfRangeException(nameof(progress));
        var result=_initial;
        foreach(var (id,keys) in _tracks)
        {
            if(keys.Length==0)continue;
            float[] value;
            if(progress>=keys[^1].Time)value=keys[^1].Value;
            else
            {
                int left=0;
                while(left+1<keys.Length&&keys[left+1].Time<=progress)left++;
                var a=keys[left];var b=keys[left+1];
                double ratio=((double)progress-a.Time)/((double)b.Time-a.Time);
                value=new float[a.Value.Length];
                if(id==5)
                {
                    // 0090ADF0 stores fraction as float before 008D2030.
                    float fraction=(float)ratio;
                    var q=EnbW3dAnimationSampler.InterpolateQuaternionRetail(
                        new(a.Value[0],a.Value[1],a.Value[2],a.Value[3]),
                        new(b.Value[0],b.Value[1],b.Value[2],b.Value[3]),fraction);
                    value[0]=q.X;value[1]=q.Y;value[2]=q.Z;value[3]=q.W;
                    value[4]=(float)(a.Value[4]+fraction*((double)b.Value[4]-a.Value[4]));
                }
                else for(int i=0;i<value.Length;i++)value[i]=(float)(a.Value[i]+ratio*((double)b.Value[i]-a.Value[i]));
            }
            result=id switch
            {
                2=>result with {Color=new(value[0],value[1],value[2])},
                3=>result with {Opacity=value[0]},
                4=>result with {Scale=new(value[0],value[1],value[2])},
                5=>result with {Orientation=new(value[0],value[1],value[2],value[3]),Strength=value[4]},
                _=>result
            };
        }
        return result;
    }
}
