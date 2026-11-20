using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR7B1InfluenceSkyPresentationTests
{
    [Fact]
    public void SkyBoxColorPostLoadMutationDoesNotReprocessPreprocessedColor1()
    {
        var material = new EnbW3dMaterialUniforms(
            Ambient: Vector3.Zero,
            Diffuse: Vector3.Zero,
            Specular: Vector3.Zero,
            Emissive: Vector3.One,
            Power: 1f,
            DiffuseAlpha: 1f,
            Mapper0: EnbW3dMapperUniforms.Identity,
            Mapper1: EnbW3dMapperUniforms.Identity);
        Vector3 skyBoxColor = new(64f / 255f, 32f / 255f, 16f / 255f);

        EnbCurrentRetailInfluenceSkyPostLoadMaterialMutation mutation =
            EnbCurrentRetailInfluenceSkyRuntime.ApplySkyBoxColorAfterLoad(material, skyBoxColor);

        Assert.Equal(skyBoxColor, mutation.Material.Emissive);
        Assert.False(mutation.ReprocessColor1);
    }

    [Fact]
    public void AuthoredEnvironmentShaderUsesRetailPackedStateBeforeDirectMeshMutation()
    {
        // This is the compact current-retail environment shader form. Production
        // derives it from the installed BaseAsset-selected W3D, not from this test.
        var authored = new EnbW3dRenderShaderRecord(
            0x0000_0007u,
            0x0100_0102u,
            0x0200_0201u,
            0x0200_0200u);

        bool ok = EnbCurrentRetailInfluenceSkyRuntime.TryResolveDirectMeshFixedFunctionState(
            authored,
            out EnbW3dFixedFunctionState effective,
            out uint authoredPacked,
            out string? diagnostic);

        Assert.True(ok, diagnostic);
        Assert.Equal(0x0029_4417u, authoredPacked);
        Assert.Equal(
            EnbCurrentRetailInfluenceSkyRuntime.ApplyDirectMeshPackedShaderMutation(authoredPacked),
            effective.PackedRetailState);
        Assert.Equal(0x0029_4417u, effective.PackedRetailState);
    }
}
