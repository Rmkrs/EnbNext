using EnbNext.Rendering;
using R=EnbNext.Rendering.EnbRetailLineArithmetic.Register;
using X87=EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.Net7.Tests;

public sealed class EnbPc24RoundingHardwareTests
{
    [Fact] public void NormalRangeAndBoundaryRoundingMatchesActualX87Instruction()
    {
        var random=new Random(0x24c0);
        using var scope=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        void Check(double value)
        {
            var input=X87.FromDoubleBits(BitConverter.DoubleToUInt64Bits(value),0x7f);
            var native=X87.EvaluateControlled(4,input,(X87)0d,0x7f);
            ulong expected=X87.DoubleBits(native,0x7f);
            ulong actual=BitConverter.DoubleToUInt64Bits((double)((R)value+(R)0d));
            Assert.True(expected==actual,$"input={BitConverter.DoubleToUInt64Bits(value):X16} expected={expected:X16} actual={actual:X16}");
        }
        foreach(double boundary in new[]{(double)float.MaxValue,(double)float.MinValue,
            Math.ScaleB(1d,-126),-Math.ScaleB(1d,-126),Math.ScaleB(1d,-140),Math.ScaleB(1d,140),0d,-0d})
        {Check(boundary);Check(Math.BitIncrement(boundary));Check(Math.BitDecrement(boundary));}
        for(int i=0;i<16384;i++)
        {
            ulong tiny=(ulong)random.NextInt64(1,0x0010000000000000L);
            if((i&1)!=0)tiny|=0x8000000000000000UL;
            Check(BitConverter.UInt64BitsToDouble(tiny));
            uint bits=(uint)random.Next(0x00800000,0x7f7ffffe);
            double lower=BitConverter.UInt32BitsToSingle(bits),upper=BitConverter.UInt32BitsToSingle(bits+1);
            double halfway=(lower+upper)*.5;
            if((i&1)!=0)halfway=-halfway;
            Check(halfway);Check(Math.BitIncrement(halfway));Check(Math.BitDecrement(halfway));
        }
    }
}
