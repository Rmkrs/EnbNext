using System.Numerics;
using R=EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.Rendering;

public static class EnbRetailTakeoffMotion
{
    //796A93..796B93, before the bounding-box/sweep consumer.
    public static (Vector3 Position,Vector3 Direction) Candidate(Matrix4x4 ship,Vector3 middle,float elevation,float yawOffset,
        EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(precision,true);
        R F(float value)=>R.FromFloatBits(BitConverter.SingleToUInt32Bits(value));
        ushort control=precision==EnbRetailLinePrecision.Single24?(ushort)0x7f:(ushort)0x27f;
        R heading=R.EvaluateControlled(33,F(ship.M11),F(ship.M12),control);
        float yaw=(float)(heading+F(yawOffset));
        float ce=(float)R.Cosine(F(elevation)),se=(float)R.Sine(F(elevation));
        float cy=(float)(R.Cosine(F(yaw))+F(0)),sy=(float)(F((float)R.Sine(F(yaw)))+F(0));
        Vector3 direction=new((float)(F(cy)*F(ce)+F(0)),(float)(F(sy)*F(ce)+F(0)),(float)(F(0)*F(ce)+F(se)));
        float x=(float)(F(direction.X)*F(5000)),y=(float)(F(direction.Y)*F(5000));
        return (new((float)(F(x)+F(middle.X)),(float)(F(y)+F(middle.Y)),(float)(F(direction.Z)*F(5000)+F(middle.Z))),direction);
    }
    //7964AA..79651E /79667C..796722. The fraction spills, but its square
    //uses the retained original register multiplied by the spilled fraction.
    public static (Matrix4x4 Transform,Quaternion Orientation) Sample(Vector3 start,Vector3 middle,Vector3 end,
        Quaternion first,Quaternion last,uint elapsed,EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(precision,true);
        R F(float value)=>R.FromFloatBits(BitConverter.SingleToUInt32Bits(value));
        bool rise=elapsed<2000;
        R fraction=(R)(double)(rise?elapsed:unchecked(elapsed-2000))*F(rise?.0005f:BitConverter.Int32BitsToSingle(0x39aec33e));
        float t=(float)fraction;
        var orientation=rise?EnbRetailDirectQuaternionInterpolation.Interpolate(first,last,(float)(fraction*F(t)),precision):last;
        var from=rise?start:middle;var to=rise?middle:end;
        float Axis(float a,float b)=>(float)((F(b)-F(a))*F(t)+F(a));
        var matrix=EnbRetailParticleHierarchyTransform.QuaternionBasis(orientation,precision);
        matrix.Translation=new(Axis(from.X,to.X),Axis(from.Y,to.Y),Axis(from.Z,to.Z));
        return (matrix,orientation);
    }
}
