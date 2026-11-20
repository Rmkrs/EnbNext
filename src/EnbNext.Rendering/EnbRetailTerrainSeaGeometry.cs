using System.Numerics;
using R=EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.Rendering;

/// <summary>Sea vertex order from 004751B5..0047679A and indices from 004767F3.</summary>
public sealed record EnbRetailTerrainSeaGeometry(Vector3[] Positions,uint[] Indices)
{
    /// <summary>00463E00 followed by the stores/scales at00476B11..00476B8C.</summary>
    public Vector2[] Coordinates(int width,int origin,float xScale,float yScale,float detailScale,
        EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(precision,true);
        ushort control=precision==EnbRetailLinePrecision.Single24?(ushort)0x7f:(ushort)0x27f;
        R F(float v)=>R.FromFloatBits(BitConverter.SingleToUInt32Bits(v));
        R Op(uint op,R a,R b)=>R.EvaluateControlled(op,a,b,control);
        float Store(R v)=>BitConverter.UInt32BitsToSingle(Op(3,v,default).RawLowWord);
        float inverseWidth=Store(Op(7,F(1),R.FromInteger64(width,control)));
        float Coordinate(float value,float scale)
        {
            float cell=Store(Op(4,Op(7,F(value),F(scale)),R.FromInteger64(origin,control)));
            float normalized=Store(Op(6,F(cell),F(inverseWidth)));
            return Store(Op(6,F(normalized),F(detailScale)));
        }
        return Positions.Select(p=>new Vector2(Coordinate(p.X,xScale),Coordinate(p.Y,yScale))).ToArray();
    }
    public static EnbRetailTerrainSeaGeometry Build(int width,int cells,int grid,
        float xScale,float yScale,float seaLevel,IReadOnlyList<EnbRetailTerrainPatch> patches,
        EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(precision,true);
        ushort control=precision==EnbRetailLinePrecision.Single24?(ushort)0x7f:(ushort)0x27f;
        R F(float v)=>R.FromFloatBits(BitConverter.SingleToUInt32Bits(v));
        R Integer(int v)=>R.FromInteger64(v,control);
        R Mul(R a,float b)=>R.EvaluateControlled(14,a,new R(BitConverter.SingleToUInt32Bits(b),0),control);
        R Add(R a,float b)=>R.EvaluateControlled(12,a,new R(BitConverter.SingleToUInt32Bits(b),0),control);
        float Store(R v)=>BitConverter.UInt32BitsToSingle(R.EvaluateControlled(3,v,default,control).RawLowWord);
        float dx=Store(Mul(F(Store(Integer(cells))),xScale));
        float dy=Store(Mul(F(Store(Integer(cells))),yScale));
        float half=Store(Mul(Integer(width>>1),xScale));
        float Offset(int i,float step,float origin)=>Store(Add(Mul(Integer(i),step),origin));
        var positions=new List<Vector3>();
        void Quad(float x,float y)
        {
            float right=Store(Add(F(x),dx)),top=Store(Add(F(y),dy));
            positions.Add(new(x,y,seaLevel));positions.Add(new(right,y,seaLevel));
            positions.Add(new(right,top,seaLevel));positions.Add(new(x,top,seaLevel));
        }
        for(int level=0;level<5;level++)
            foreach(var p in patches)
                if(p.Visible&&p.Level==level)Quad(p.Translation.X,p.Translation.Y);
        for(int ring=0;ring<10;ring++)
        {
            float top=Offset(ring,dy,half),right=Offset(ring,dx,half);
            float bottom=Store(Add(Mul(Integer(-(ring+1)),dy),-half));
            float left=Store(Add(Mul(Integer(-(ring+1)),dx),-half));
            for(int i=0;i<grid;i++)
            {
                float x=Offset(i,dx,-half),y=Offset(i,dy,-half);
                Quad(x,top);Quad(right,y);Quad(x,bottom);Quad(left,y);
            }
            for(int i=0;i<10;i++)
            {
                Quad(Offset(i-10,dx,-half),top);
                Quad(right,Offset(i,dy,half));
                Quad(Offset(i,dx,half),bottom);
                Quad(left,Store(Add(Mul(Integer(-(i+1)),dy),-half)));
            }
        }
        var indices=new uint[positions.Count/4*6];
        for(int q=0;q<positions.Count/4;q++)
        {
            uint a=(uint)q*4;int n=q*6;
            indices[n]=a;indices[n+1]=a+1;indices[n+2]=a+2;
            indices[n+3]=a;indices[n+4]=a+2;indices[n+5]=a+3;
        }
        return new(positions.ToArray(),indices);
    }
}
