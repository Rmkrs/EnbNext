using System.Numerics;
using System.Runtime.CompilerServices;
using EnbNext.Rendering;
namespace EnbNext.Net7.Tests;
[Collection(LoadSensitiveCollection.Name)]
public sealed class EnbSubmissionMaterialOwnerTests
{
    private static EnbW3dSharedMaterialMappers Mappers()
    {
        Assert.True(EnbW3dMapperRuntimeProgramCompiler.TryCompile(null,out var program,out var error),error);
        Assert.True(EnbW3dMapperRuntimeState.TryCreate(program,123,out var a,out error),error);
        Assert.True(EnbW3dMapperRuntimeState.TryCreate(program,123,out var b,out error),error);
        return new(a,b);
    }
    [Fact]
    public void CompactBindingCapturesTextureReferencesWithoutCopyingSemanticState()
    {
        var a=new EnbW3dRenderTextureBinding(0,"a",null);var b=new EnbW3dRenderTextureBinding(1,"b",null);
        EnbW3dRenderTextureBinding?[] textures=[a,b];
        var source=new EnbW3dRenderTriangleBinding(2,4,null,null,textures,default(EnbW3dCompiledMaterialSemantics));
        var binding=new EnbW3dEffectiveSubmissionBinding(source);
        textures[0]=b;Assert.Same(a,binding.Textures[0]);Assert.Same(b,binding.Textures[1]);
        Assert.True(Unsafe.SizeOf<EnbW3dEffectiveSubmissionBinding>()<=64);
        for(int i=0;i<10000;i++)_ = new EnbW3dEffectiveSubmissionBinding(source);
        long before=GC.GetAllocatedBytesForCurrentThread();int count=0;
        for(int i=0;i<1000;i++)count+=new EnbW3dEffectiveSubmissionBinding(source).Textures.Length;
        Assert.Equal(0,GC.GetAllocatedBytesForCurrentThread()-before);Assert.Equal(2000,count);
        var changed=binding with {CurrentRetailNullMaterial=true};Assert.False(binding.CurrentRetailNullMaterial);Assert.True(changed.CurrentRetailNullMaterial);
    }
    [Fact]
    public void MapperOwnedIdentityIsBitExactBoundedAndDoesNotChangeMapperEquality()
    {
        var owner=Mappers();var equal=new EnbW3dSharedMaterialMappers(owner.Stage0,owner.Stage1);
        var uniform=default(EnbW3dMaterialUniforms);var lighting=EnbW3dVertexLightingSemantics.RetailDefault;
        var first=owner.RetainMaterialIdentity(uniform,0,lighting,0,1,0,true,true);
        Assert.Equal(owner,equal);Assert.Equal(owner.GetHashCode(),equal.GetHashCode());
        Assert.Same(first,owner.RetainMaterialIdentity(uniform,0,lighting,0,1,0,true,true));
        Assert.NotEqual(first,owner.RetainMaterialIdentity(uniform with {Power=-0.0f},0,lighting,0,1,0,true,true));
        Assert.NotEqual(first,owner.RetainMaterialIdentity(uniform,0,lighting,0,1,1,true,true));
        Assert.NotEqual(first,owner.RetainMaterialIdentity(uniform,0,lighting,1,1,0,true,true));
        Assert.NotEqual(first,owner.RetainMaterialIdentity(uniform,0,lighting,0,1,0,false,true));
        var other=Mappers();var clone=owner with {Stage0=other.Stage0,Stage1=other.Stage1};
        Assert.NotEqual(first,clone.RetainMaterialIdentity(uniform,0,lighting,0,1,0,true,true));
        for(int i=0;i<10000;i++)owner.RetainMaterialIdentity(uniform,0,lighting,0,1,0,true,true);
        long before=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<1000;i++)owner.RetainMaterialIdentity(uniform,0,lighting,0,1,0,true,true);
        Assert.Equal(0,GC.GetAllocatedBytesForCurrentThread()-before);
        Assert.Equal(first,owner.RetainMaterialIdentity(uniform,0,lighting,0,1,0,true,true));
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RememberThenReleaseSource(EnbW3dSharedMaterialMappers owner)
    {
        var source=new EnbW3dRenderTriangleBinding(0,0,null,null,[],default(EnbW3dCompiledMaterialSemantics));
        var binding=new EnbW3dEffectiveSubmissionBinding(source);
        owner.RetainMaterialIdentity(binding.Semantics.Uniforms,0,binding.Semantics.VertexLighting,0,1,0,true,true);
        return new(source.SharedState!);
    }
    [Fact]
    public void LiveMapperDoesNotRetainRetiredSourceMaterialState()
    {
        var owner=Mappers();var weak=RememberThenReleaseSource(owner);
        for(int i=0;i<3&&weak.IsAlive;i++){GC.Collect();GC.WaitForPendingFinalizers();}
        Assert.False(weak.IsAlive);GC.KeepAlive(owner);
    }
}
