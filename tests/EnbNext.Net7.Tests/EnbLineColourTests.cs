using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbLineColourTests
{
    private static EnbW3dDrawTask Build(Vector4 color,int shader=1)
    {
        var asset=new EnbRetailTractorLineAsset(10,4,Vector2.Zero,new("line.tga","test.mix",0,1),
            new(0,"line.tga",new("line.tga","test.mix",0,1))){Color=color,ShaderPreset=shader};
        Assert.True(EnbResolvedRenderCamera.TryCreate(new(0,0,100),Quaternion.Identity,new(800,600,1),
            Math.PI/2,new(1,10000),out var camera));
        return asset.Build(1,1,1,Vector3.Zero,new(100,0,0),camera,0,1)!;
    }

    [Fact]
    public void AuthoredColourAndOpacityReachEveryGpuVertex()
    {
        var packet=Build(new(.65f,.85f,1,.5f));
        Assert.Equal(4,packet.Mesh.EffectiveColor1.Count);
        Assert.All(packet.Mesh.EffectiveColor1,c=>Assert.Equal(new EnbRenderColor32(165,216,255,127),c));
        Assert.Equal(0x94433u,packet.Mesh.MaterialPasses[0].SingleBindingSemantics!.Value.FixedFunction.PackedRetailState);
    }

    [Theory]
    [InlineData(1,0x94033u)]
    [InlineData(.9962f,0x94033u)]
    [InlineData(.9961f,0x94433u)]
    [InlineData(.4f,0x94433u)]
    public void RetailThresholdControlsModulation(float value,uint shader)
    {
        var packet=Build(new(value,value,value,1));
        Assert.Equal(shader,packet.Mesh.MaterialPasses[0].SingleBindingSemantics!.Value.FixedFunction.PackedRetailState);
    }

    [Fact]
    public void OpacityAloneEnablesModulation()
        => Assert.Equal(0x94433u,Build(new(1,1,1,.5f)).Mesh.MaterialPasses[0].SingleBindingSemantics!.Value.FixedFunction.PackedRetailState);

    [Theory]
    [InlineData(1)]
    [InlineData(.5f)]
    public void GravityPresetRetainsCutoutDepthWritesAndOpaqueSubmission(float opacity)
    {
        var packet=Build(new(1,1,1,opacity),3);
        var state=packet.Mesh.MaterialPasses[0].SingleBindingSemantics!.Value.FixedFunction;
        Assert.True(state.DepthWriteEnabled); Assert.True(state.AlphaTestEnabled);
        Assert.False(state.BlendEnabled);
        Assert.Equal(EnbRenderBlendFactor.One,state.SourceBlend);
        Assert.Equal(EnbRenderBlendFactor.Zero,state.DestinationBlend);
        Assert.Equal(EnbW3dDrawBlendClass.Opaque,packet.OrderKey.BlendClass);
        Assert.Equal(opacity==1?0xd401bu:0xd441bu,state.PackedRetailState);
    }
}
