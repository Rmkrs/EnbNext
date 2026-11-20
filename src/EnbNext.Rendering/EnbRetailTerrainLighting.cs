using System.Numerics;
using EnbNext.Assets;
using R=EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.Rendering;

/// <summary>0046C730/0046CAE0/0046D410: PLD illumination multiplied into the color map.</summary>
public static class EnbRetailTerrainLighting
{

    internal static void ShadeRegion(EnbRetailTerrain terrain,EnbRetailTerrainPrelight cache,
        EnbTextureImage color,EnbRetailTerrainLightState light,bool colorAlphaIsIllumination,Prepared prepared,
        EnbColor32[] pixels,int left,int top,int right,int bottom,EnbRetailLinePrecision precision)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(precision,true);
        ushort control=precision==EnbRetailLinePrecision.Single24?(ushort)0x7f:(ushort)0x27f;
        static int Channel(uint packed,int shift)=>(int)((packed>>shift)&255);
        var horizons = light.Direction.X < 0 ? cache.NegativeHorizons : cache.PositiveHorizons;
        for (int y = top; y < bottom; y++)
        for (int x = left; x < right; x++)
        {
            int i = y * terrain.Width + x;
            bool lit=light.Enabled&&horizons[i]<prepared.Upper;
            bool full=lit&&horizons[i]<prepared.Lower;
            R visibility=lit&&!full?R.EvaluateControlled(14,R.FromInteger64(prepared.Upper-horizons[i],control),
                new R(BitConverter.SingleToUInt32Bits(prepared.Penumbra),0),control):default;
            int Illuminate(byte ambient,int shift)
            {
                int value=(Channel(prepared.Ambient,shift)*ambient)>>8;
                if(!lit)return value;
                int diffuse=Channel(prepared.Diffuse[cache.NormalIndices[i]],shift);
                if(full)return Math.Min(255,value+diffuse);
                R d=R.EvaluateControlled(6,R.FromInteger64(diffuse,control),visibility,control);
                R sum=R.EvaluateControlled(4,d,R.FromInteger64(value,control),control);
                return (int)Math.Min(255u,(uint)R.TruncateInteger64(sum));
            }
            int r=Illuminate(cache.AmbientRed[i],16),g=Illuminate(cache.AmbientGreen[i],8),b=Illuminate(cache.AmbientBlue[i],0);
            // 009D2C10 normalizes retail TGA rows to bottom origin; our decoder
            // normalizes to top origin. FLIP_TGA preserves retail's row order.
            int row = terrain.FlipColorMap ? terrain.Width - 1 - y : y;
            EnbColor32 c = color.GetPixel(x, row);
            // 0046D521..0046D610 adds the 32-bit TGA's separate alpha plane
            // to illumination before color multiplication, then saturates.
            int added = colorAlphaIsIllumination ? c.A : 0;
            pixels[row * terrain.Width + x] = new((byte)Math.Min(255,(c.R * (r + added)) >> 8),
                (byte)Math.Min(255,(c.G * (g + added)) >> 8), (byte)Math.Min(255,(c.B * (b + added)) >> 8),
                light.Enabled?(byte)255:(byte)(prepared.Ambient>>24));
        }
    }

    internal sealed record Prepared(ushort Lower,ushort Upper,float Penumbra,uint Ambient,uint[] Diffuse);

    internal static Prepared Prepare(
        EnbRetailTerrainPrelight cache,EnbRetailTerrainLightState light,EnbRetailLinePrecision precision)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(precision,true);
        ushort control=precision==EnbRetailLinePrecision.Single24?(ushort)0x7f:(ushort)0x27f;
        R Float(float f)=>R.FromFloatBits(BitConverter.SingleToUInt32Bits(f));
        R Op(uint op,R a,R b)=>R.EvaluateControlled(op,a,b,control);
        R Mul(R a,float b)=>Op(14,a,new R(BitConverter.SingleToUInt32Bits(b),0));
        float Store(R a)=>BitConverter.UInt32BitsToSingle(R.EvaluateControlled(3,a,default,control).RawLowWord);
        uint Pack(Vector3 rgb)
        {
            uint Channel(float value)
            {
                ushort truncate=(ushort)(control|0xc00);
                R product=R.EvaluateControlled(14,Float(value),new R(BitConverter.SingleToUInt32Bits(255),0),truncate);
                return R.EvaluateControlled(26,product,default,truncate).RawLowWord;
            }
            return Channel(rgb.Z)|(Channel(rgb.Y)<<8)|(Channel(rgb.X)<<16);
        }
        uint ambient=Pack(light.Ambient);var diffuse=new uint[256];
        if(!light.Enabled)return new(0,0,0,ambient,diffuse);
        const float halfPi=1.5707964f;
        R angleScale=Op(7,Float(65535),Float(halfPi));
        R Clamp(R angle)=>(double)angle<0?Float(0):(double)angle>halfPi?Float(halfPi):angle;
        ushort lower=unchecked((ushort)R.TruncateInteger64(Op(6,Clamp(Op(5,Float(light.Elevation),Float(light.Radius))),angleScale)));
        ushort upper=unchecked((ushort)R.TruncateInteger64(Op(6,Clamp(Op(4,Float(light.Elevation),Float(light.Radius))),angleScale)));
        // 0046C8B4 spills to double before the A27827 ceiling wrapper.
        double radius=BitConverter.UInt64BitsToDouble(R.DoubleBits(Mul(angleScale,light.Radius),control));
        R ceiling=R.FromDoubleBits(BitConverter.DoubleToUInt64Bits(Math.Ceiling(radius)),control);
        float penumbra=Store(Op(7,Float(1),Op(4,ceiling,ceiling)));
        for(int i=0;i<diffuse.Length;i++)
        {
            Vector3 n=cache.Normals[i];
            R dot=Op(4,Op(4,Mul(Float(n.X),light.Direction.X),Mul(Float(n.Z),light.Direction.Z)),Mul(Float(n.Y),light.Direction.Y));
            diffuse[i]=(double)dot>0?Pack(new(Store(Mul(dot,light.Diffuse.X)),Store(Mul(dot,light.Diffuse.Y)),Store(Mul(dot,light.Diffuse.Z)))):0;
        }
        return new(lower,upper,penumbra,ambient,diffuse);
    }
}
