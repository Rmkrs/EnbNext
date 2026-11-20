using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR7B0InfluenceSkyEvidenceTests
{
    [Fact]
    public void SkyBoxColorMutatesOnlyMaterialEmissiveRgb()
    {
        var material = new EnbW3dMaterialUniforms(
            Ambient: new Vector3(0.1f, 0.2f, 0.3f),
            Diffuse: new Vector3(0.4f, 0.5f, 0.6f),
            Specular: new Vector3(0.7f, 0.8f, 0.9f),
            Emissive: new Vector3(0.11f, 0.22f, 0.33f),
            Power: 17.5f,
            DiffuseAlpha: 0.625f,
            Mapper0: EnbW3dMapperUniforms.Identity,
            Mapper1: EnbW3dMapperUniforms.Identity);
        Vector3 skyBoxColor = new(0.75f, 0.25f, 0.5f);

        EnbW3dMaterialUniforms mutated =
            EnbCurrentRetailInfluenceSkyRuntime.ApplySkyBoxColorAfterLoad(material, skyBoxColor).Material;

        Assert.Equal(material.Ambient, mutated.Ambient);
        Assert.Equal(material.Diffuse, mutated.Diffuse);
        Assert.Equal(material.Specular, mutated.Specular);
        Assert.Equal(skyBoxColor, mutated.Emissive);
        Assert.Equal(material.Power, mutated.Power);
        Assert.Equal(material.DiffuseAlpha, mutated.DiffuseAlpha);
        Assert.Equal(material.Mapper0, mutated.Mapper0);
        Assert.Equal(material.Mapper1, mutated.Mapper1);
    }

    [Fact]
    public void DirectMeshShaderMutationMatchesCurrentRetailBitReplacement()
    {
        const uint original = 0xA5A5_FFFFu;
        uint mutated = EnbCurrentRetailInfluenceSkyRuntime.ApplyDirectMeshPackedShaderMutation(original);

        Assert.Equal((original & 0xFFFF_E7FFu) | 0x0000_0400u, mutated);
        Assert.Equal(0u, mutated & 0x0000_1800u);
        Assert.Equal(0x0000_0400u, mutated & 0x0000_0400u);
        Assert.Equal(original & ~0x0000_1C00u, mutated & ~0x0000_1C00u);
    }
}
