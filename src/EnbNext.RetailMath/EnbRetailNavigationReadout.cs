using System.Numerics;
using System.Globalization;
using EnbNext.Rendering;
using R = EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.RetailMath;

public static class EnbRetailNavigationReadout
{
    public static string FormatDirection(int degrees) => degrees<0
        ? "< "+(-degrees).ToString(CultureInfo.InvariantCulture)+" "
        : degrees>0 ? " "+degrees.ToString(CultureInfo.InvariantCulture)+" >" : "< 0 >";

    public static string FormatEta(int seconds) => seconds == -1 ? "???"
        : seconds / 60 == 0 ? seconds.ToString(CultureInfo.InvariantCulture)+"s"
        : seconds / 60 < 60 ? (seconds/60).ToString(CultureInfo.InvariantCulture)+"m"+(seconds%60).ToString(CultureInfo.InvariantCulture)+"s"
        : "+1hr";

    public static float Distance(Vector3 localPosition,Vector3 targetPosition,float localRadius,float targetRadius)
    {
        using var scope = new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24,true);
        R x=(R)localPosition.X-targetPosition.X,y=(R)localPosition.Y-targetPosition.Y,z=(R)localPosition.Z-targetPosition.Z;
        float squared=(float)(z*z+x*x+y*y); // 831EA9..831EB9 differs from ETA.
        float center=(float)R.Sqrt((R)squared);
        R result=(R)center-((R)targetRadius+localRadius);
        return (R.CompareFloatMemory(result,0)&0x4100)!=0?0:(float)result;
    }

    public static string FormatDistance(float distance)
    {
        using var scope = new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24,true);
        double kilometers=(double)((R)distance*BitConverter.UInt32BitsToSingle(0x3a83126f));
        return Math.Round(kilometers,2,MidpointRounding.AwayFromZero).ToString("F2",CultureInfo.InvariantCulture)+"k";
    }

    // Whole 5BDC20: the radius sum is subtracted once; speed uses X/Y/Z order.
    public static int Eta(Vector3 localPosition,Vector3 targetPosition,float localRadius,float targetRadius,Vector3 velocity)
    {
        using var scope = new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24,true);
        R x=(R)targetPosition.X-localPosition.X,y=(R)targetPosition.Y-localPosition.Y,z=(R)targetPosition.Z-localPosition.Z;
        float squared=(float)(z*z+y*y+x*x);
        float center=(float)R.Sqrt((R)squared);
        float distance=(float)((R)center-((R)targetRadius+localRadius));
        if(distance<0)distance=0;
        float speedSquared=(float)((R)velocity.X*velocity.X+(R)velocity.Y*velocity.Y+(R)velocity.Z*velocity.Z);
        float speed=(float)R.Sqrt((R)speedSquared);
        if(speed<BitConverter.UInt64BitsToDouble(0x3f50624dd2f1a9fc))return -1;
        return Truncate((R)distance/speed*BitConverter.UInt32BitsToSingle(0x3a83126f));
    }
    private static readonly double Degrees = BitConverter.UInt64BitsToDouble(0x404ca5dc0d03cf25);
    // FPATAN consumes matrix-column X/Y, not a camera-relative three-dimensional angle.
    private static R Bearing(Vector3 column) => R.EvaluateControlled(33,(R)column.Y,(R)column.X,0x7f);
    private static int Truncate(R value) => unchecked((int)R.TruncateInteger64(value));
    private static int HeadingCore(Vector3 column) => Truncate(Bearing(column)*Degrees+360d)%360;

    // 8C26B0 ->7C07A7: map glyphs use atan2(Y,X) and the authored float pi/2.
    public static float MapHeading(Vector3 column)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24,true);
        return (float)(R.EvaluateControlled(33,(R)column.X,(R)column.Y,0x7f)-BitConverter.UInt32BitsToSingle(0x3fc90fdb));
    }

    public static int Heading(Vector3 column)
    {
        using var scope = new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24,true);
        return HeadingCore(column);
    }

    public static int SameDirection(Vector3 localColumn,Vector3 targetColumn)
    {
        using var scope = new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24,true);
        return (HeadingCore(targetColumn)-HeadingCore(localColumn)+540)%360-180;
    }

    public static int TurnTo(Vector3 localColumn,Vector3 localPosition,Vector3 targetPosition)
    {
        using var scope = new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24,true);
        Vector3 delta=new((float)((R)targetPosition.X-localPosition.X),(float)((R)targetPosition.Y-localPosition.Y),(float)((R)targetPosition.Z-localPosition.Z));
        float angle=(float)Bearing(delta); // 5BD5FE spills before the heading helper.
        return Truncate((R)angle*Degrees-R.FromInteger64(HeadingCore(localColumn)-180,0x7f)+1440d)%360-180;
    }

    // 5BD683..5BD6B6: provider velocity is distance per millisecond.
    // Preserve the Y/Z/X addition order, both float spills, and final __ftol.
    public static int Velocity(Vector3 velocity)
    {
        using var scope = new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24, true);
        float squared = (float)((R)velocity.Y * velocity.Y + (R)velocity.Z * velocity.Z + (R)velocity.X * velocity.X);
        float length = (float)R.Sqrt((R)squared);
        return unchecked((int)R.TruncateInteger64((R)length * 1000f));
    }
}
