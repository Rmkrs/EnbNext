using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>Retail vertical environment scroll: the V offset advances by VPerSec and wraps to its
/// fraction; the generated coordinates use a fixed 0.5 scale and U offset.</summary>
public static class EnbCurrentRetailMapperEnvironmentRuntime
{
    public static EnbW3dMapperRuntimeEvaluation Evaluate(float perSecond,uint now,ref float offset,ref uint last)
    {
        uint delta=unchecked(now-last);last=now;
        offset=(float)((double)delta*0.001f)*perSecond+offset;
        offset=offset-(float)Math.Floor(offset);
        // The application consumes m02/m03 and m13 for the generated Z/V.
        return new(new(0.5f,0),Vector2.Zero,new(0.5f,offset),new(1,0,0,1),false);
    }
}
