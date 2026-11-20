using System.Buffers.Binary;
using System.Numerics;
using R=EnbNext.Rendering.EnbRetailExtendedX87Register;
namespace EnbNext.Rendering;

/// <summary>79BFC0 normal-camera matrix composition, through8D2710/8D1C20.
/// Input is the physical provider matrix requested with visual banking disabled.</summary>
public static class EnbRetailNormalCameraPose
{
    public readonly record struct Result(Vector3 Position,Quaternion Orientation);
    public static Result Evaluate(Matrix4x4 provider,float bank,float heading,float distance,
        float tilt,float elevation,bool follow=true,float scale=75,
        EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(precision,true);
        ushort control=precision==EnbRetailLinePrecision.Single24?(ushort)0x7f:(ushort)0x27f;
        float[] m=[provider.M11,provider.M21,provider.M31,provider.M41,
            provider.M12,provider.M22,provider.M32,provider.M42,
            provider.M13,provider.M23,provider.M33,provider.M43];
        Rotate(1,2,(float)R.Sine((R)bank),R.Cosine((R)bank),true);
        if(!follow)
        {
            float xx=(float)((R)m[0]*m[0]);
            R sum=(R)m[4]*m[4]+xx;
            if((float)sum!=0)
            {
                R inverse=Inverse((float)sum);
                m[0]=(float)(inverse*m[0]);m[4]=(float)(inverse*m[4]);
            }
            m[1]=-m[4];m[2]=0;m[5]=m[0];m[6]=0;m[8]=0;m[9]=0;m[10]=1;
        }
        R axis=R.FromDoubleBits(0xbff921fb60000000,control);
        Rotate(0,2,R.Sine(axis),R.Cosine(axis),false);
        Rotate(0,1,R.Sine((R)(-1.5707964f)),R.Cosine((R)(-1.5707964f)),true);
        Rotate(0,2,R.Sine((R)heading),R.Cosine((R)heading),false);
        Rotate(1,2,R.Sine((R)(-tilt)),R.Cosine((R)(-tilt)),true);
        R length=(R)scale*distance;
        for(int row=0;row<3;row++)m[row*4+3]=(float)((R)m[row*4+2]*length+m[row*4+3]);
        Rotate(1,2,R.Sine((R)elevation),R.Cosine((R)elevation),true);
        Quaternion q=EnbRetailQuaternionConversions.ExtractOrientation(EnbRetailPointGroupTransform.FromRows(m),precision);
        float square=(float)((R)q.X*q.X+(R)q.Y*q.Y+(R)q.Z*q.Z+(R)q.W*q.W);
        if(square!=0)
        {
            R inverse=Inverse(square);
            q=new((float)(inverse*q.X),(float)(inverse*q.Y),(float)(inverse*q.Z),(float)(inverse*q.W));
        }
        return new(new(m[3],m[7],m[11]),q);

        void Rotate(int a,int b,R sin,R cos,bool positive)
        {
            for(int row=0;row<3;row++)
            {
                int i=row*4+a,j=row*4+b;float x=m[i],y=m[j];
                m[i]=(float)(positive?(R)x*cos+(R)y*sin:(R)x*cos-(R)y*sin);
                m[j]=(float)(positive?(R)y*cos-(R)x*sin:(R)y*cos+(R)x*sin);
            }
        }
        R Inverse(float value)=>R.FromFloatBits(BitConverter.SingleToUInt32Bits(EnbRetailFloat.ReciprocalSquareRoot(value)));
    }
}
