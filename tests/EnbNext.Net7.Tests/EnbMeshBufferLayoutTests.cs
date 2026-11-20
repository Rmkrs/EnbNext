using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbMeshBufferLayoutTests
{
    [Fact]
    public void RetainedUniformMapperTakesPrecedenceOverExpandedDrawBindings()
    {
        using var resources = new EnbW3dRenderResourceCache(definition =>
            (definition.LogicalName, new EnbW3dTextureCreationRequest(EnbW3dTextureSamplingSemantics.RetailDefault, 0)));
        using var owner = EnbCurrentRetailRslEffectPassOwner.Create(resources,
            new(EnbNext.Assets.EnbCurrentRetailRslEffectPassType.Environment, "env.tga", 0x94033, false, false));
        var reflection = owner.Material.CurrentRetailUniformMaterial!.Value;
        var plain = reflection with { Material = reflection.Material with { Mapper0 = null } };
        var pass = (owner.Material).Copy().SetVertexMaterials([plain]).SetCurrentRetailUniformMaterial(reflection);
        Assert.True(EnbCurrentRetailMeshBufferLayout.RequiresAuthoredNormals(0x4000, [pass], true, false, 1));
        pass = (pass).Copy().SetVertexMaterials([reflection]).SetCurrentRetailUniformMaterial(plain);
        Assert.False(EnbCurrentRetailMeshBufferLayout.RequiresAuthoredNormals(0x4000, [pass], true, false, 1));
        pass = (pass).Copy().SetCurrentRetailUniformMaterial(null);
        Assert.True(EnbCurrentRetailMeshBufferLayout.RequiresAuthoredNormals(0x4000, [pass], true, false, 1));
    }

    [Theory]
    [InlineData(0u, 0u)]
    [InlineData(0x4000u, 0x4000u)]
    [InlineData(0x01004000u, 0u)]
    [InlineData(0x08004000u, 0u)]
    [InlineData(0x02004000u, 0x2000u)]
    [InlineData(0x06000000u, 0x4000u)]
    public void HighAttributeFamilyTakesPrecedenceOverLowNormalFlag(uint attributes, uint expected)
        => Assert.Equal(expected, EnbCurrentRetailMeshBufferLayout.AuthoredNormalPolicyFlags(attributes));

    [Fact]
    public void MapperNormalsDetermineSharedBufferAdmissionForUnlitModels()
    {
        var containers = new EnbCurrentRetailMeshContainerRegistry();
        var ordinary = containers.Register(new object(), Fvf(1, false), false, false, 3, 3);
        foreach (var selector in Enum.GetValues<EnbW3dMapperTypeSelector>())
        {
            bool normals = EnbCurrentRetailMeshBufferLayout.RequiresNormals(0x4000, true, false, 1, [selector]);
            uint fvf = EnbCurrentRetailMeshBufferLayout.CalculateFvf(1, false, false, normals, false, false, false);
            var admitted = containers.Register(new object(), fvf, false, false, 3, 3);
            bool expected = (byte)selector is 1 or 2 or 12 or 13 or 14 or 15 or 17 or 19 or 20;
            Assert.Equal(expected, ReferenceEquals(ordinary, admitted));
        }
        Assert.True(EnbCurrentRetailMeshBufferLayout.RequiresNormals(0, false, false, 0, []));
        Assert.True(EnbCurrentRetailMeshBufferLayout.RequiresNormals(0xe000, true, true, 2, []));
        Assert.False(EnbCurrentRetailMeshBufferLayout.RequiresNormals(0xe000, false, true, 2, []));
        Assert.False(EnbCurrentRetailMeshBufferLayout.RequiresNormals(0xe000, true, true, 1, []));
        Assert.True(EnbCurrentRetailMeshBufferLayout.RequiresNormals(0x2000, true, false, 1,
            [EnbW3dMapperTypeSelector.Mode04, EnbW3dMapperTypeSelector.Mode0C]));
    }

    [Fact]
    public void DescriptorStreamsSeparateContainersWhileSkinUsesItsFixedLayout()
    {
        var containers = new EnbCurrentRetailMeshContainerRegistry();
        var plain = containers.Register(new object(),Fvf(1,false),false,false,3,3);
        var color = containers.Register(new object(),Fvf(1,true),false,false,3,3);
        var secondUv = containers.Register(new object(),Fvf(2,false),false,false,3,3);
        Assert.NotSame(plain,color);
        Assert.NotSame(plain,secondUv);
        Assert.Same(plain,containers.Register(new object(),Fvf(1,false),false,false,3,3));
        Assert.Equal(0x112u,EnbCurrentRetailMeshBufferLayout.CalculateFvf(2,true,true,false,true,true,true));
    }

    [Fact]
    public void DisabledLowLevelSortingPreservesDescriptorLayout()
    {
        Assert.Equal(Fvf(1,false),EnbCurrentRetailMeshBufferLayout.CalculateFvf(1,false,false,true,false,true,false));
        Assert.Equal(0x252u,EnbCurrentRetailMeshBufferLayout.CalculateFvf(1,false,false,true,false,true,true));
        Assert.Equal(0x882u,EnbCurrentRetailMeshBufferLayout.CalculateFvf(8,false,true,false,false,false,false));
    }

    private static uint Fvf(int coordinates,bool color)
        => EnbCurrentRetailMeshBufferLayout.CalculateFvf(coordinates,color,false,true,false,false,false);
}
