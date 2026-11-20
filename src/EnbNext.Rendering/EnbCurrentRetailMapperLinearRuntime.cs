using System.Numerics;
using R=EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

/// <summary>Production binding of whole105 00932CE0, including retained V clamp
/// arithmetic and the actual floor/quiet-result/thread-data consumers.</summary>
public static class EnbCurrentRetailMapperLinearRuntime
{
    public static EnbW3dMapperRuntimeEvaluation Evaluate(EnbW3dMapperUniforms parameters,
        uint now,ref Vector2 offset,ref uint last,
        EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        // Retail spills U before its clamp but keeps V in the x87 register.
        using var arithmetic=new EnbRetailLineArithmetic.Scope(precision,true);
        R elapsed=(double)unchecked(now-last);
        float u=(float)(elapsed*parameters.ScrollPerMillisecond.X+offset.X);
        R v=elapsed*parameters.ScrollPerMillisecond.Y+offset.Y;
        if(parameters.ClampFix)
        {
            if(u < -parameters.Scale.X)u=-parameters.Scale.X;
            else if(u > parameters.Scale.X)u=parameters.Scale.X;
            if(v < -(R)parameters.Scale.Y)v=-parameters.Scale.Y;
            else if(v > parameters.Scale.Y)v=parameters.Scale.Y;
        }
        else
        {
            u=(float)((R)u-Math.Floor(u));
            float spilledV=(float)v;
            v=(R)spilledV-Math.Floor(spilledV);
        }
        offset=new(u,(float)v);last=now;
        return new(parameters.Scale,Vector2.Zero,offset,new(1,0,0,1),false);
    }
}
