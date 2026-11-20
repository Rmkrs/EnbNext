using System.Numerics;
using System.Runtime.InteropServices;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbAffineCompositionNativeTests
{
    [Theory]
    [InlineData(EnbRetailLinePrecision.Single24)]
    [InlineData(EnbRetailLinePrecision.Double53)]
    public void CompleteCompositionMatchesEveryLiteralMatrixWord(EnbRetailLinePrecision precision)
    {
        var random=new Random(0x8c2cd0);
        for(int i=0;i<6000;i++)
        {
            float F()=>i<3000?(float)(random.NextDouble()*200-100):BitConverter.UInt32BitsToSingle((uint)random.NextInt64(0,1L<<32));
            Matrix4x4 M()=>EnbRetailPointGroupTransform.FromRows(Enumerable.Range(0,12).Select(_=>F()).ToArray());
            var local=M();var parent=M();
            Check(EnbRetailParticleHierarchyTransform.ComposeLiteral(local,parent,precision),EnbRetailParticleHierarchyTransform.Compose(local,parent,precision));
            Check(EnbRetailPointGroupTransform.ComposeLiteral(local,parent,precision),EnbRetailPointGroupTransform.Compose(local,parent,precision));
        }
        static void Check(Matrix4x4 expected,Matrix4x4 actual)
            => Assert.Equal(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref expected,1)).ToArray(),
                MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref actual,1)).ToArray());
    }
    [Fact]
    public void NormalExponentEnvelopePreservesCancellationAndLiteralPc24Words()
    {
        var random = new Random(0x9656d0);
        for (int i = 0; i < 8192; i++)
        {
            float F()
            {
                int exponent = random.Next(-51, 61);
                float mantissa = 1 + random.Next(0, 1 << 23) / 8388608f;
                float value = MathF.ScaleB(mantissa, exponent);
                if (exponent == 60) value = MathF.ScaleB(1, 60);
                return random.Next(2) == 0 ? value : -value;
            }
            var a = EnbRetailPointGroupTransform.FromRows(Enumerable.Range(0, 12).Select(_ => F()).ToArray());
            var b = EnbRetailPointGroupTransform.FromRows(Enumerable.Range(0, 12).Select(_ => F()).ToArray());
            if (i % 2 == 0)
            {
                // Nearly cancelling lower-envelope products exercise the smallest spacing.
                a.M11 = MathF.BitIncrement(MathF.ScaleB(1, -51));
                a.M21 = -MathF.ScaleB(1, -51);
                b.M11 = b.M12 = MathF.ScaleB(1, -51);
                b.M13 = 0;
            }
            Check(EnbRetailParticleHierarchyTransform.ComposeLiteral(a,b,EnbRetailLinePrecision.Single24), EnbRetailParticleHierarchyTransform.Compose(a,b));
            Check(EnbRetailPointGroupTransform.ComposeLiteral(a,b,EnbRetailLinePrecision.Single24), EnbRetailPointGroupTransform.Compose(a,b));
        }
        static void Check(Matrix4x4 expected, Matrix4x4 actual)
            => Assert.Equal(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref expected, 1)).ToArray(),
                MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref actual, 1)).ToArray());
    }
}
