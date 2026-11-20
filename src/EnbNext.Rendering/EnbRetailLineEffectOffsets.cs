using System.Numerics;
using EnbNext.Assets;
using R=EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

public static class EnbRetailLineEffectOffsets
{
    // 006EAC50 scales authored offsets; 006E5BB0 applies each object's radius
    // along the original line direction. 006E59B0 does this before growth/shrink.
    public static void Apply(EnbEffectDefinition definition, float scale,
        float? sourceRadius, float? targetRadius, ref Vector3 start, ref Vector3 end,
        bool sourceDetached = false, bool targetDetached = false)
    {
        using var arithmetic=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        float AuthoredOffset(string key)
        {
            if (!definition.Properties.ContainsKey(key)) return 0;
            if (!EnbRetailIniDocument.TryReadFloat(definition.Properties, key, out var value) || !float.IsFinite(value))
                throw new InvalidDataException($"Invalid line {key}");
            return (float)((R)value*scale);
        }
        static R RadiusOffset(string key,float authored,float? radius)
        {
            if (radius is not float r || !float.IsFinite(r) || r < 0)
                throw new InvalidDataException($"Line {key} radius unavailable");
            return (R)r*authored;
        }

        if (!float.IsFinite(scale) || scale <= 0) throw new InvalidDataException("Invalid line scale");
        //006E5BE8 skips both zero authored offsets.006E5C9C/006E5D06
        //also skip each individual write unless its scaled offset and owner
        //are present; adding a computed zero is not equivalent to no write.
        float sourceAuthored=sourceDetached?0:AuthoredOffset("StartOffset");
        float targetAuthored=targetDetached?0:AuthoredOffset("EndOffset");
        if(sourceAuthored==0 && targetAuthored==0)return;
        R sourceOffset=sourceAuthored==0?0:RadiusOffset("StartOffset",sourceAuthored,sourceRadius);
        R targetOffset=targetAuthored==0?0:RadiusOffset("EndOffset",targetAuthored,targetRadius);
        var direction=new Vector3((float)((R)end.X-start.X),(float)((R)end.Y-start.Y),(float)((R)end.Z-start.Z));
        R x=direction.X,y=direction.Y,z=direction.Z;
        float squareX=(float)(x*x);
        float square=(float)(z*z+y*y+squareX);
        if(square!=0)
        {
            R reciprocal=EnbRetailLineCoalescing.ReciprocalSquareRoot(square);
            direction=new((float)(x*reciprocal),(float)(y*reciprocal),(float)(z*reciprocal));
        }
        //006E5CBE/006E5D28 retain the radius product; X/Y contributions
        //spill before addition/subtraction while Z remains in an x87 register.
        // Signed offsets and crossing endpoints are intentional retail behavior.
        if(sourceAuthored!=0)
        {
            float sx=(float)((R)direction.X*sourceOffset),sy=(float)((R)direction.Y*sourceOffset);
            start=new((float)((R)start.X+sx),(float)((R)start.Y+sy),(float)((R)direction.Z*sourceOffset+start.Z));
        }
        if(targetAuthored!=0)
        {
            float tx=(float)((R)direction.X*targetOffset),ty=(float)((R)direction.Y*targetOffset);
            end=new((float)((R)end.X-tx),(float)((R)end.Y-ty),(float)((R)end.Z-targetOffset*direction.Z));
        }
    }
}
