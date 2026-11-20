using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>Retail camera bounds arithmetic (009447A0, 00943CF0, 00945970) as
/// ordinary managed maths on every platform. Formerly emitted as x87 machine
/// code at runtime on Windows only; look and feel matter here, not
/// bit-exactness.</summary>
public static class EnbCurrentRetailCameraFieldOfView
{

    /// <summary>00945970/00945990: twice the arctangent of half the bound span.</summary>
    public static bool TryGet(float lower,float upper,EnbRetailLinePrecision precision,out double result)
    {
        double span=Round((double)upper-lower,precision);
        result=Round(2*Math.Atan2(span,2.0),precision);
        return true;
    }

    /// <summary>009447A0: half-angle tangents. A vertical angle of -1 means
    /// "derive from aspect"; otherwise the aspect is replaced by the ratio.</summary>
    public static bool TryCompute(float horizontal,float vertical,float incomingAspect,out Vector4 bounds,out float aspect)
    {
        double horizontalTangent=Math.Tan(horizontal*0.5);
        double verticalTangent;
        aspect=incomingAspect;
        if(vertical==-1f)verticalTangent=(float)(1.0/incomingAspect)*horizontalTangent;
        else
        {
            verticalTangent=Math.Tan(vertical*0.5);
            aspect=(float)(horizontalTangent/verticalTangent);
        }
        float h=(float)horizontalTangent,v=(float)verticalTangent;
        bounds=new(-h,-v,h,v);
        return true;
    }

    private static double Round(double value,EnbRetailLinePrecision precision)=>
        precision==EnbRetailLinePrecision.Single24?(float)value:value;
}
