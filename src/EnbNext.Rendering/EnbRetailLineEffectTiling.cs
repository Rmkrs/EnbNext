using System.Numerics;
using EnbNext.Assets;
using NativeReal = EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

//006E5040 captures original tile/subdivision values;006E5AA4..006E5AFF
//changes them only while the post-offset/post-timing length is below the limit.
public sealed class EnbRetailLineEffectTiling
{
    private readonly float _minimumLength, _reciprocal, _originalTile;
    private readonly int _originalSubdivisions;
    public float Tile { get; private set; }
    public int Subdivisions { get; private set; }

    public EnbRetailLineEffectTiling(EnbEffectDefinition definition,float tile,int subdivisions)
    {
        float minimum=0;
        if(definition.Properties.ContainsKey("TilingMinLength") &&
            (!EnbRetailIniDocument.TryReadFloat(definition.Properties,"TilingMinLength",out minimum) || !float.IsFinite(minimum)))
            throw new InvalidDataException("Invalid line TilingMinLength");
        _minimumLength=Math.Max(0,minimum);
        _reciprocal=_minimumLength>0?1f/_minimumLength:0;
        _originalTile=Tile=tile;
        _originalSubdivisions=Subdivisions=subdivisions;
    }

    public void Update(Vector3 start,Vector3 end)
    {
        using var arithmetic=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        NativeReal x=(NativeReal)end.X-start.X,y=(NativeReal)end.Y-start.Y,z=(NativeReal)end.Z-start.Z;
        float square=(float)(z*z+y*y+x*x);
        float length=(float)NativeReal.Sqrt(square);
        if(!(length<_minimumLength))return;
        NativeReal ratio=(NativeReal)length*_reciprocal;
        float tile=(float)(ratio*_originalTile);
        //009BEAB0 maps unordered/nonpositive values to zero as well.
        Tile=tile>256?256:tile>0?tile:0;
        int subdivisions=_originalSubdivisions;
        while(subdivisions>0 && !(ratio>=.5)){subdivisions--;ratio=ratio+ratio;}
        Subdivisions=subdivisions;
    }
}
