using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbParticleMaterialPublicationTests
{
    [Fact]
    public void VisiblePrefixesShareBindingAndSurviveGrowthAndReplacement()
    {
        var definition=Definition();
        var first=Build(definition,"a",2);
        Assert.Same(first,Build(definition,"a",2));
        var grown=Build(definition,"a",64);
        var replacement=Build(definition,"b",3);
        Assert.Equal(2,first.TriangleBindings.Count);
        Assert.Equal(64,grown.TriangleBindings.Count);
        Assert.Equal("a",first.TextureStages[0].Textures[0].LogicalName);
        Assert.Equal("a",grown.TextureStages[0].Textures[0].LogicalName);
        Assert.Equal("b",replacement.TextureStages[0].Textures[0].LogicalName);
        Assert.Same(replacement,Build(definition,"b",3));
    }

    [Fact]
    public void ConcurrentViewsNeverMixTexturePublicationAndVisiblePrefix()
    {
        var definition=Definition();
        var queued=new EnbW3dRenderMaterialPass[1024];
        Parallel.For(0,queued.Length,index => queued[index]=Build(definition,index%2==0?"a":"b",1+index%17));
        for(int i=0;i<queued.Length;i++)
        {
            var pass=queued[i];
            Assert.Equal(1+i%17,pass.TriangleBindings.Count);
            string name=i%2==0?"a":"b";
            Assert.Equal(name,pass.TextureStages[0].Textures[0].LogicalName);
            Assert.All(pass.TriangleBindings,binding => Assert.Equal(name,binding.Textures[0]!.LogicalName));
        }
    }

    private static EnbW3dRenderMaterialPass Build(EnbCurrentRetailParticleEmitterDefinition definition,string name,int count)
    {
        Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryBuildMaterial(definition,
            new(name,"fixture.mix",0,128),count,out var pass,out _,out var error,runtimeTextureName:name),error);
        return pass;
    }

    private static EnbCurrentRetailParticleEmitterDefinition Definition()
    {
        static EnbCurrentRetailParticleScalarProperty Scalar(float value)=>new(0,[new(0,value)]);
        return new(0,"material-publication","fixture.tga",1,10,1,
            Vector3.Zero,Vector3.Zero,0,0,0,1,new(0,Vector3.Zero),new(0,Vector3.Zero),0,0,
            new(3,0,0,1,0,0,0,1,1,0,0,0,0,0,0,0),1,0,
            new(Vector3.Zero,[new(0,255,255,255,0)]),Scalar(1),Scalar(1),Scalar(0),0,Scalar(0));
    }
}
