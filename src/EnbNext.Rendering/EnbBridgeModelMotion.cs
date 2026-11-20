using System.Numerics;
using R=EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.Rendering;

/// <summary>Bridge model reaction, 0053C0CC–0053C43F. Camera look is separate.</summary>
public sealed class EnbBridgeModelMotion
{
    public Vector3 SmoothedAngularRate { get; private set; }
    public void Reset() => SmoothedAngularRate = Vector3.Zero;

    public Matrix4x4 Update(Vector3 normalizedAngularRate, int bridgeState,
        EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        if (!float.IsFinite(normalizedAngularRate.X) || !float.IsFinite(normalizedAngularRate.Y) ||
            !float.IsFinite(normalizedAngularRate.Z)) throw new ArgumentException("Non-finite bridge angular rate.");
        using var scope=new EnbRetailLineArithmetic.Scope(precision,true);
        float Smooth(float input,float previous)
        {
            float target=bridgeState==0?(float)((R)input*(R)50f):0;
            return (float)(((R)target-(R)previous)*(R).2f+(R)previous);
        }
        SmoothedAngularRate=new(Smooth(normalizedAngularRate.X,SmoothedAngularRate.X),
            Smooth(normalizedAngularRate.Y,SmoothedAngularRate.Y),Smooth(normalizedAngularRate.Z,SmoothedAngularRate.Z));
        // 53C166..53C43F: native 3x4 rows, explicit float spills and retained
        // cosine/translation intermediates. Framework matrix products and
        // trigonometry do not preserve these operations.
        R cy=R.Cosine((R)SmoothedAngularRate.Y);
        float sy=(float)R.Sine((R)SmoothedAngularRate.Y);
        float[] a=new float[12];
        a[4]=(float)((R)0f-(R)0f);a[8]=a[4];
        a[9]=(float)((R)sy+(R)0f);a[5]=(float)(cy-(R)0f);
        a[6]=(float)((R)0f-(R)sy);
        R last=cy+(R)0f;
        float cz=(float)R.Cosine(-(R)SmoothedAngularRate.Z);
        R sine=R.Sine(-(R)SmoothedAngularRate.Z);
        float sz=(float)sine;
        a[0]=(float)(sine*(R)a[8]+(R)cz);
        a[8]=(float)((R)cz*(R)a[8]-(R)sz);
        a[1]=(float)((R)sz*(R)a[9]+(R)0f);
        a[9]=(float)((R)cz*(R)a[9]-(R)0f);
        a[2]=(float)((R)sz*last+(R)0f);
        last=last*(R)cz-(R)0f;
        R translation=(R)sz*(R)0f+(R)0f;
        a[11]=(float)((R)cz*(R)0f-(R)0f);
        cz=(float)R.Cosine((R)SmoothedAngularRate.Z);
        sz=(float)R.Sine((R)SmoothedAngularRate.Z);
        for(int column=0;column<3;column++)
        {
            float old=a[column];
            a[column]=(float)((R)cz*(R)old-(R)a[column+4]*(R)sz);
            a[column+4]=(float)((R)a[column+4]*(R)cz+(R)old*(R)sz);
        }
        float storedTranslation=(float)translation;
        a[3]=(float)(translation*(R)cz-(R)sz*(R)a[7]);
        a[7]=(float)((R)cz*(R)a[7]+(R)storedTranslation*(R)sz);
        R rootSine=R.Sine((R)(-1.5707963705062866));
        R rootCosine=R.Cosine((R)(-1.5707963705062866));
        for(int row=0;row<3;row++)
        {
            int offset=row*4;
            R z=row==2?last:(R)a[offset+2];
            float y=a[offset+1];
            a[offset+1]=(float)((R)y*rootCosine+z*rootSine);
            a[offset+2]=(float)(z*rootCosine-(R)y*rootSine);
        }
        rootCosine=R.Cosine((R)1.5707963705062866);
        rootSine=R.Sine((R)1.5707963705062866);
        for(int row=0;row<3;row++)
        {
            int offset=row*4;float x=a[offset],y=a[offset+1];
            a[offset]=(float)((R)y*rootSine+rootCosine*(R)x);
            a[offset+1]=(float)((R)y*rootCosine-(R)x*rootSine);
        }
        return new(a[0],a[4],a[8],0,a[1],a[5],a[9],0,a[2],a[6],a[10],0,a[3],a[7],a[11],1);
    }
}
