using System.Numerics;
using EnbNext.Rendering;
using R=EnbNext.Rendering.EnbRetailLineArithmetic.Register;
namespace EnbNext.Net7.Tests;
[Collection(LoadSensitiveCollection.Name)]
public sealed class EnbLineProductAllocationTests
{
    [Fact]
    public void FixedWidthProductMatchesArbitraryPrecisionReference()
    {
        using var scope=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        var random=new Random(106);
        for(int i=0;i<30000;i++)
        {
            double Next()=>BitConverter.UInt64BitsToDouble((ulong)random.NextInt64() | ((ulong)random.Next(2)<<63));
            double a=Next(),b=Next();
            if(i%3==0)
            {
                const ulong mask=~((1UL<<29)-1);
                a=BitConverter.UInt64BitsToDouble(BitConverter.DoubleToUInt64Bits(a)&mask);
                b=BitConverter.UInt64BitsToDouble(BitConverter.DoubleToUInt64Bits(b)&mask);
            }
            if(!double.IsFinite(a)||!double.IsFinite(b)||a==0||b==0)continue;
            Assert.Equal(BitConverter.DoubleToUInt64Bits(Reference(a,b)),BitConverter.DoubleToUInt64Bits((double)((R)a*(R)b)));
        }
        static double Reference(double a,double b)
        {
            static (ulong S,int E) Split(double v)
            {
                ulong bits=BitConverter.DoubleToUInt64Bits(v)&0x7fffffffffffffff;
                int e=(int)(bits>>52);
                return(e==0?bits&0xfffffffffffff:(bits&0xfffffffffffff)|(1UL<<52),e==0?-1074:e-1075);
            }
            var l=Split(a);var r=Split(b);
            BigInteger p=(BigInteger)l.S*r.S;
            int shift=(int)p.GetBitLength()-24;
            if(shift>0)
            {
                var kept=p>>shift;var rem=p-(kept<<shift);var half=BigInteger.One<<(shift-1);
                if(rem>half||(rem==half&&!kept.IsEven))kept++;
                p=kept;
            }
            else shift=0;
            double result=Math.ScaleB((double)p,l.E+r.E+shift);
            return (BitConverter.DoubleToUInt64Bits(a)^BitConverter.DoubleToUInt64Bits(b))>>63!=0?-result:result;
        }
    }
    [Fact]
    public void FullPrecisionProductsAllocateNoManagedStorage()
    {
        using var scope=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        double value=0;
        for(int i=0;i<1000;i++)value=(double)((R)Math.PI*(R)Math.E);
        long before=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<10000;i++)value=(double)((R)Math.PI*(R)Math.E);
        long bytes=GC.GetAllocatedBytesForCurrentThread()-before;
        Assert.Equal(0,bytes);Assert.True(value>8);
    }
}
