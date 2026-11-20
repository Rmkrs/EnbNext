using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbSphereMaterialTests
{
    [Fact]
    public void AnimatedColorWritesFloatEmissiveWithoutVertexFadeOrQuantization()
    {
        var color=new System.Numerics.Vector3(.123456f,.345678f,.567891f);
        var additive=EnbRetailSphereMaterial.ApplyColor(default,color,.4f,0x94633);
        Assert.Equal(color*.4f,additive.Emissive);Assert.Equal(.25f,additive.DiffuseAlpha);
        Assert.Equal(System.Numerics.Vector3.Zero,additive.Diffuse);
        Assert.Equal(System.Numerics.Vector3.Zero,additive.Ambient);
        var ordinary=EnbRetailSphereMaterial.ApplyColor(default,color,.4f,0);
        Assert.Equal(color,ordinary.Emissive);Assert.Equal(.4f,ordinary.DiffuseAlpha);
    }
    [Theory]
    [InlineData(false,true,0x94433u,EnbW3dFogMode.Disabled)]
    [InlineData(false,false,0x84433u,EnbW3dFogMode.Disabled)]
    [InlineData(true,true,0x94633u,EnbW3dFogMode.Black)]
    [InlineData(true,false,0x84633u,EnbW3dFogMode.Black)]
    public void InstalledSphereShaderRetainsCullingWithActualTextureBinding(bool optimize,bool texture,uint packed,EnbW3dFogMode fog)
    {
        var result=EnbRetailSphereMaterial.Decode(new(0x01000003,0x01000100,1,0),optimize,texture);
        Assert.Equal(packed,result.PackedRetailState);
        Assert.Equal(EnbRenderCullMode.Clockwise,result.CullMode);
        Assert.Equal(fog,result.FogMode);
        Assert.True(result.BlendEnabled);Assert.False(result.DepthWriteEnabled);Assert.False(result.AlphaTestEnabled);
        Assert.Equal(EnbRenderBlendFactor.One,result.SourceBlend);Assert.Equal(EnbRenderBlendFactor.One,result.DestinationBlend);
    }
}
