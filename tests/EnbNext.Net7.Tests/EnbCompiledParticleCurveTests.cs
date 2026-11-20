using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbCompiledParticleCurveTests
{

    [Theory]
    [InlineData(EnbRetailLinePrecision.Single24)]
    [InlineData(EnbRetailLinePrecision.Double53)]
    public void PointExpansionMatchesRegisterPathAcrossBinary32Exponents(EnbRetailLinePrecision precision)
    {
        var random=new Random(955);
        float Value()=>BitConverter.UInt32BitsToSingle((uint)random.NextInt64(0,1L<<32));
        for(int i=0;i<20000;i++)
        {
            var center=new Vector3(Value(),Value(),Value());float size=Value();
            int mode=i%2,corner=i%(mode==1?4:3);byte orientation=(byte)i;
            var expected=EnbRetailPointGroupExpansion.ExpandLiteral(center,size,mode,orientation,corner,precision);
            var actual=EnbRetailPointGroupExpansion.Expand(center,size,mode,orientation,corner,precision);
            Assert.Equal(BitConverter.SingleToUInt32Bits(expected.X),BitConverter.SingleToUInt32Bits(actual.X));
            Assert.Equal(BitConverter.SingleToUInt32Bits(expected.Y),BitConverter.SingleToUInt32Bits(actual.Y));
            Assert.Equal(BitConverter.SingleToUInt32Bits(expected.Z),BitConverter.SingleToUInt32Bits(actual.Z));
        }
    }

}
