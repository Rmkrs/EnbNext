using System.Buffers.Binary;
using System.Numerics;
using R=EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.Rendering;

/// <summary>6F3ED0/6F4290: bank target from angular rate and retained turn goal.</summary>
public static class EnbRetailShipBankTarget
{

    public static float Evaluate(byte objectType, float angularRate, float headingTarget, Matrix4x4 matrix,
        EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(precision,true);
        float scaled=(float)((R)angularRate*-500f);
        if(objectType!=1 && (R.CompareFloatMemory(HeadingRemaining(angularRate,headingTarget,matrix,precision),0x3e32b8c3)&0x100)!=0)
            return 0;
        R target=(R)scaled*(objectType==1?1f:1.5f);
        float limit=BitConverter.UInt32BitsToSingle(0x3f20d97c);
        return (R.CompareFloatMemory(target,0xbf20d97c)&0x100)!=0?-limit:
            (R.CompareFloatMemory(target,0x3f20d97c)&0x4100)==0?limit:(float)target;
    }

    private static R HeadingRemaining(float rate,float goal,Matrix4x4 matrix,EnbRetailLinePrecision precision)
    {
        float tau=BitConverter.UInt32BitsToSingle(0x40c90fdb);
        if(goal<0)goal=(float)((R)goal+tau);
        if(rate==0)return (R)0;
        float x=matrix.M11,y=matrix.M12;
        float squared=(float)((R)y*y+(R)x*x);
        if(squared!=0)
        {
            R inverse=R.FromFloatBits(BitConverter.SingleToUInt32Bits(EnbRetailFloat.ReciprocalSquareRoot(squared)));
            x=(float)(inverse*x);y=(float)(inverse*y);
        }
        if((R.CompareFloatMemory((R)y*y+(R)x*x,0x38d1b717)&0x100)!=0){x=1;y=0;}
        x=Math.Clamp(x,-1,1);
        ushort control=precision==EnbRetailLinePrecision.Single24?(ushort)0x7f:(ushort)0x27f;
        // A26B20's finite [-1,1] branch: FSQRT then FPATAN, with
        // exact endpoint constants. 6F43C9 spills the result to float.
        float heading=x==1?0:x==-1?(float)R.EvaluateControlled(27,default,default,control):
            (float)R.EvaluateControlled(33,(R)x,R.Sqrt(((R)1+x)*((R)1-x)),control);
        if(y<0)heading=(float)((R)tau-heading);
        R difference=(R)heading-goal;
        float storedDifference=(float)difference;
        if((R.CompareDoubleMemory(R.Abs(difference),0x3f50624dd2f1a9fc,control)&0x4100)!=0)return (R)0;
        if(rate>0)return heading<=goal?(R)goal-heading:((R)goal+tau)-heading;
        return goal<=heading?(R)storedDifference:((R)heading+tau)-goal;
    }
}
