using System.Numerics;
using R=EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.Rendering;

/// <summary>FVF202 patch-local vertex words and midpoint written by00474790.</summary>
public sealed record EnbRetailTerrainVertices(Vector3[] Positions, Vector2[] ColorCoordinates,
    Vector2[] DetailCoordinates, float Midpoint)
{
    public static EnbRetailTerrainVertices Build(int side, int xOrigin, int yOrigin,
        int width, ReadOnlySpan<ushort> heights, float heightScale, Vector2 detailScale,
        EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(precision,true);
        ushort control=precision==EnbRetailLinePrecision.Single24?(ushort)0x7f:(ushort)0x27f;
        R Float(float value)=>R.FromFloatBits(BitConverter.SingleToUInt32Bits(value));
        R Multiply(R value,float factor)=>R.EvaluateControlled(14,value,new R(BitConverter.SingleToUInt32Bits(factor),0),control);
        float Store(R value)=>BitConverter.UInt32BitsToSingle(R.EvaluateControlled(3,value,default,control).RawLowWord);
        float inverseWidth=Store(R.EvaluateControlled(7,Float(1),R.FromInteger64(width,control),control));
        float inverseHeightScale=Store(R.EvaluateControlled(7,Float(1),Float(heightScale),control));
        var positions=new Vector3[checked(side*side)];
        var color=new Vector2[positions.Length];var detail=new Vector2[positions.Length];
        float minimum=2,maximum=-1;
        for(int y=0;y<side;y++)
        {
            int gy=yOrigin+y;
            R v=Multiply(R.FromInteger64(gy,control),inverseWidth);
            float v0=Store(v),v1=Store(Multiply(v,detailScale.Y));
            for(int x=0;x<side;x++)
            {
                int gx=xOrigin+x,index=y*side+x;
                ushort sample=heights[(gy&(width-1))*width+(gx&(width-1))];
                R z=Multiply(Multiply(R.FromInteger64(sample,control),heightScale),inverseHeightScale);
                if(!((double)Float(minimum)<(double)z))minimum=Store(z);
                if(!((double)Float(maximum)>(double)z))maximum=Store(z);
                positions[index]=new(x,y,Store(z));
                R u=Multiply(R.FromInteger64(gx,control),inverseWidth);
                color[index]=new(Store(u),v0);
                detail[index]=new(Store(Multiply(u,detailScale.X)),v1);
            }
        }
        R sum=R.EvaluateControlled(12,Float(maximum),new R(BitConverter.SingleToUInt32Bits(minimum),0),control);
        return new(positions,color,detail,Store(Multiply(sum,0.5f)));
    }
}
