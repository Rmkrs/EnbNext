using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbRingMaterialTests
{
    [Theory]
    [InlineData(false,true,0x94433u,EnbRenderCullMode.Clockwise,EnbW3dFogMode.Disabled)]
    [InlineData(false,false,0x84433u,EnbRenderCullMode.Clockwise,EnbW3dFogMode.Disabled)]
    [InlineData(true,true,0x14633u,EnbRenderCullMode.None,EnbW3dFogMode.Black)]
    [InlineData(true,false,0x4633u,EnbRenderCullMode.None,EnbW3dFogMode.Black)]
    public void InstalledRingShaderUsesNativeOptimizationAndActualTextureBinding(
        bool optimize,bool texture,uint packed,EnbRenderCullMode cull,EnbW3dFogMode fog)
    {
        // All 43 audited headers: 03 00 00 01 00 01 00 01 01 00 00 00 00 00 00 00.
        var result=EnbRetailRingMaterial.Decode(new(0x01000003,0x01000100,1,0),optimize,texture);
        Assert.Equal(packed,result.PackedRetailState);
        Assert.Equal(cull,result.CullMode);Assert.Equal(fog,result.FogMode);
        Assert.False(result.DepthWriteEnabled);Assert.False(result.AlphaTestEnabled);
        Assert.True(result.BlendEnabled);
        Assert.Equal(EnbRenderBlendFactor.One,result.SourceBlend);
        Assert.Equal(EnbRenderBlendFactor.One,result.DestinationBlend);
    }
}
