using System.Numerics;
using EnbNext.Assets;
using R = EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

// 00961D10 scans the active circular span before detail filtering. 0095EA10/80
// expose its local sphere/box; 00960DB0 transforms those cached local bounds.
public readonly record struct EnbRetailParticleBufferBounds(Vector3 Center,Vector3 Extent)
{
    // 0095F970 constructor +C0, followed by the signed scale store0095E93F/945.
    // Keys use the same millisecond compilation and slope spills as ScalarCurve.
    public static float MaximumSize(EnbCurrentRetailParticleScalarProperty size,long lifetimeMilliseconds,
        float storageScale=1,EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        if(size.Keys.Count==0)throw new ArgumentException("Particle size requires its initial key.",nameof(size));
        using var arithmetic=new EnbRetailLineArithmetic.Scope(precision);
        var keys=size.Keys;
        float random=MathF.Abs(size.Random);
        float maximum=keys[0].Value;
        if(keys.Count!=1 || !(random<BitConverter.Int32BitsToSingle(0x2b8cbccc)))
        {
            int last=0;
            while(last+1<keys.Count && EnbRetailParticleEmissionArithmetic.Milliseconds(keys[last+1].TimeSeconds,precision)<lifetimeMilliseconds)
            {last++;maximum=MathF.Max(maximum,keys[last].Value);}
            long start=EnbRetailParticleEmissionArithmetic.Milliseconds(keys[last].TimeSeconds,precision);
            float slope=0;
            if(last+1<keys.Count)
                slope=(float)(((R)keys[last+1].Value-keys[last].Value)/((R)keys[last+1].TimeSeconds*1000d-(double)start));
            R terminal=(R)slope*(double)(lifetimeMilliseconds-start)+keys[last].Value;
            R selected=terminal<maximum?(R)maximum:terminal;
            maximum=(float)(selected+random);
        }
        return (float)((R)maximum*storageScale);
    }

    public static EnbRetailParticleBufferBounds FromActivePositions(IReadOnlyList<Vector3> positions,float maximumSize)
    {
        if(positions.Count==0)return default; //00961D99..DBD: no padding when empty.
        using var arithmetic=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        Vector3 min=positions[0],max=min;
        for(int i=1;i<positions.Count;i++)
        {min=Vector3.Min(min,positions[i]);max=Vector3.Max(max,positions[i]);}
        return FromExtrema(min,max,maximumSize);
    }

    internal static EnbRetailParticleBufferBounds FromExtrema(Vector3 min,Vector3 max,float maximumSize)
    {
        using var arithmetic=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        (float Center,float Extent) Axis(float low,float high)
        {
            float upper=(float)((R)high+maximumSize),lower=(float)((R)low-maximumSize);
            return ((float)(((R)upper+lower)*.5),(float)(((R)upper-lower)*.5));
        }
        var x=Axis(min.X,max.X);var y=Axis(min.Y,max.Y);var z=Axis(min.Z,max.Z);
        return new(new(x.Center,y.Center,z.Center),new(x.Extent,y.Extent,z.Extent));
    }

    public EnbW3dWorldBounds LocalSphere
    {
        get
        {
            using var arithmetic=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
            float squared=(float)(((R)Extent.X*Extent.X+(R)Extent.Y*Extent.Y)+(R)Extent.Z*Extent.Z);
            return new(Center,(float)R.Sqrt(squared));
        }
    }

    // World-space births use an identity buffer transform. Local-space buffers
    // receive the emitter's full matrix via0097DD90/DDC0. Radius is not scaled.
    public EnbW3dWorldBounds WorldSphere(Matrix4x4 bufferWorld)
        =>new(Vector3.Transform(Center,bufferWorld),LocalSphere.Radius);
}
