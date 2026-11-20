using System.Globalization;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.RetailMath;
using R = EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.Rendering;

/// <summary>Whole 7C1C50 coordinate selection, arithmetic and formatted labels.</summary>
public readonly record struct EnbRetailMapCoordinates(string X,string Y,string Z,EnbRgb Color)
{
    public static EnbRetailMapCoordinates? Resolve(int mode,bool pointerInside,Vector3? hovered,
        Vector3? selectedHovered,Vector3? locked,Vector2 pointerNormalized,Vector2 viewport,Vector2 scaledCenter,float scale)
    {
        if(mode==0)return null;
        using var scope=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24,true);
        R x;float y,z;EnbRgb color;
        if(pointerInside && (hovered ?? selectedHovered) is { } objectPosition)
        {
            x=(R)objectPosition.X;y=objectPosition.Y;z=objectPosition.Z;
            color=hovered is not null ? new(1,1,0) : new(1,.1f,.1f);
        }
        else if(pointerInside && mode==2)
        {
            x=((R)pointerNormalized.X-.5f)*viewport.X+scaledCenter.X;
            y=(float)(-(((R)pointerNormalized.Y-.5f)*viewport.Y));
            y=(float)((R)y+scaledCenter.Y);
            float inverse=(float)((R)1f/scale);
            x*=inverse;y=(float)((R)y*inverse);z=0;color=new(.1f,1,.1f);
        }
        else if(locked is { } lockedPosition)
        {
            x=(R)lockedPosition.X;y=lockedPosition.Y;z=lockedPosition.Z;color=new(0,.5f,1);
        }
        else if(mode==2)
        {
            if(scale<.0001f)scale=1; // 7C1D96 guard before center division.
            x=(R)scaledCenter.X/scale;y=(float)((R)scaledCenter.Y/scale);z=0;color=new(0,.5f,1);
        }
        else return null;
        return new("X: "+Format(x),"Y: "+Format((R)y),"Z: "+Format((R)z),color);
    }

    private static string Format(R value)
    {
        double kilometers=(double)(value*BitConverter.UInt32BitsToSingle(0x3a83126f));
        // Retail CRT suppresses exact negative zero, but retains the sign of a negative value rounded to zero.
        if(kilometers==0)kilometers=0;
        return Math.Round(kilometers,2,MidpointRounding.AwayFromZero).ToString("F2",CultureInfo.InvariantCulture).PadLeft(5);
    }
}
