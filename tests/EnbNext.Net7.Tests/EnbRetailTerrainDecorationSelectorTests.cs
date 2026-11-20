using System.Buffers.Binary;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbRetailTerrainDecorationSelectorTests
{
    [Fact]
    public void RegisteredTreesSelectTheirAuthoredDecorationAndShadowWithoutLeakingReferences()
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var catalog=EnbMixResourceCatalog.Load(paths.MixFilesDirectory);Assert.NotNull(catalog);
        using var cache=new EnbW3dRenderResourceCache(definition=>(definition.LogicalName.ToLowerInvariant(),
            new EnbW3dTextureCreationRequest(EnbW3dTextureSemanticCompiler.Compile(definition.Info),0)),new EnbW3dRetailMapperRandomSource());
        using var factory=new EnbRetailTerrainShadowObjectFactory(catalog,cache,123,0x7abc1234,
            new EnbRetailFileResolver(System.IO.Path.GetDirectoryName(paths.ClientExecutablePath)!,catalog));
        var prototypes=new List<EnbRetailTerrainDecorationPrototype>();
        foreach(string name in new[]{"TDTREE_SWO01","TDTREE_SWO02","TDTREE_SWO03","TDTREE_SWO04","TDTREE_SWO05"})
        {
            var root=factory.Create(name,out var reason);Assert.True(root is not null,reason);
            var first=EnbRetailTerrainDecorationSelector.Select(root);
            prototypes.Add(first);
            Assert.NotNull(first.Decoration);
            Assert.Contains(".d",EnbW3dRenderObjectNameSemantics.RegisteredMeshName(first.Decoration).ToLowerInvariant());
            for(int i=0;i<4;i++)Assert.Equal(first,EnbRetailTerrainDecorationSelector.Select(root));
            EnbRetailTerrainShadowObjectFactory.Release(root);
        }
        var empty=factory.Create("NULL",out _);Assert.NotNull(empty);
        Assert.Equal(default,EnbRetailTerrainDecorationSelector.Select(empty));
        EnbRetailTerrainShadowObjectFactory.Release(empty);
        var terrain=new EnbRetailTerrain(0,8,new ushort[64],8,1,"","",Vector2.One,false,2,false,0,"");
        var placements=new[]{0,1,0,4}.Select(index=>new EnbRetailTerrainDecoration(index,Quaternion.Identity,Vector2.Zero,1)).ToArray();
        var input=new EnbRetailTerrainDecorations([],placements);
        var batches=EnbRetailTerrainDecorationMesh.Build(terrain,input,prototypes,out var patches);
        Assert.Equal(2,batches.Count);Assert.Equal(patches[0],patches[1]);
        for(int part=0;part<2;part++)
        {
            uint offset=0;var expected=new List<uint>();int vertices=0;
            foreach(var placement in placements)
            {
                var source=part==0?prototypes[placement.Prototype].Decoration!:prototypes[placement.Prototype].Shadow!;
                expected.AddRange(source.Indices.Select(index=>index+offset));offset+=source.Indices.Max()+1;vertices+=source.Positions.Count;
            }
            Assert.Equal(expected,batches[part].Indices);Assert.Equal(vertices,batches[part].Positions.Count);
            var pass=Assert.Single(batches[part].MaterialPasses);Assert.Single(pass.TextureStages);
            Assert.Empty(pass.DiffuseColors);Assert.Empty(pass.SpecularColors);Assert.Empty(batches[part].EffectiveColor1);
        }
        var mismatched=prototypes.ToArray();var original=mismatched[1];
        mismatched[1]=original with {DecorationMaterial=original.DecorationMaterial! with {TextureIdentity=new object()}};
        Assert.Throws<InvalidDataException>(()=>EnbRetailTerrainDecorationMesh.Build(terrain,input,mismatched,out _));
    }
}
