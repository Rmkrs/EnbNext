using EnbNext.Rendering;
namespace EnbNext.Net7.Tests;
public sealed class EnbShaderBindingTableTests
{
    [Fact]
    public void BumpFloatSignedZerosAndNanPayloadsRemainDistinct()
    {
        uint[] bits=[0u,0x80000000u,0x7fc00001u,0x7fc00002u];
        var source=Enumerable.Range(0,128).Select(i=>new EnbW3dRenderShaderBinding(0,default,
            default(EnbW3dFixedFunctionState) with {TextureCombinerSemantics=default(EnbW3dTextureCombinerSemantics) with
                {BumpEnvironment=new(default,1,BitConverter.UInt32BitsToSingle(bits[i%4]))}})).ToArray();
        var compact=Assert.IsType<EnbW3dShaderBindingTable>(EnbW3dShaderBindingTable.Freeze(source));
        Assert.Equal(4,compact.DefinitionCount);
        for(int i=0;i<source.Length;i++)Assert.Equal(bits[i%4],BitConverter.SingleToUInt32Bits(compact[i].FixedFunction.TextureCombinerSemantics.BumpEnvironment.LuminanceOffset));
    }

    [Fact]
    public void RepeatedExpandedValuesKeepOrderAndDetachFromSource()
    {
        var a=new EnbW3dRenderShaderBinding(7,default,default);
        var b=new EnbW3dRenderShaderBinding(11,default,default);
        var source=Enumerable.Range(0,512).Select(i=>i%3==0?a:b).ToArray();
        var expected=source.ToArray();
        var compact=Assert.IsType<EnbW3dShaderBindingTable>(EnbW3dShaderBindingTable.Freeze(source));
        Assert.Equal(2,compact.DefinitionCount);Assert.Equal(512,compact.Count);
        Array.Fill(source,default);
        Assert.Equal(expected,compact.ToArray());
        Assert.Same(compact,EnbW3dShaderBindingTable.Freeze(compact));
        for(int i=0;i<expected.Length;i++)Assert.Equal(expected[i],compact[i]);
        Assert.Throws<ArgumentOutOfRangeException>(()=>compact[-1]);
        Assert.Throws<ArgumentOutOfRangeException>(()=>compact[512]);
    }
    [Fact]
    public void UniqueAndShortStreamsKeepExactValuesWithoutIndexOverhead()
    {
        foreach(int count in new[]{0,1,31,128})
        {
            var source=Enumerable.Range(0,count).Select(i=>new EnbW3dRenderShaderBinding((uint)i,default,default)).ToArray();
            var expected=source.ToArray();var frozen=EnbW3dShaderBindingTable.Freeze(source);
            Assert.IsNotType<EnbW3dShaderBindingTable>(frozen);Array.Fill(source,default);
            Assert.Equal(expected,frozen.ToArray());
        }
    }
}
