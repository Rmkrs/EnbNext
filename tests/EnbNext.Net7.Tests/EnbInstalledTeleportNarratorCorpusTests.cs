using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

[Xunit.Trait("Category", "Heavy")] // runs every avatar teleport beam through its whole lifetime and loads the fixed narrator/help actors
public sealed class EnbInstalledTeleportNarratorCorpusTests
{
    [Fact]
    public void EveryTeleportBeamPosesAndDrawsAndFixedActorsLoad()
    {
        var assets = EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        Assert.True(EnbResolvedRenderCamera.TryCreate(new(0, -10, 2), Quaternion.Identity, new(800, 600, 1), Math.PI / 3, new(.01f, 10000), out var camera));
        int packets = 0;
        foreach (string name in new[] { "av_tele01", "av_tele02", "av_tele03", "av_tele04" })
        {
            var beam = new EnbStationBeamEffect(assets, name).CreateInstance();
            var scene = new EnbRetailTeleportScene();
            uint end = (uint)(beam.OwnerDurationMilliseconds + beam.DurationMilliseconds) + 17;
            uint clock = 0;
            for (; clock <= end; clock += 17)
            {
                beam.Prepare(scene, clock, clock, Matrix4x4.Identity);
                scene.Advance(clock, clock == 0 ? 0u : 17u);
                packets += beam.Draw(clock, camera).Count;
            }
            beam.Destroy(clock);
            for (uint tail = clock + 17; !beam.Retired && tail < clock + 60000; tail += 17)
            {
                beam.Prepare(scene, tail, tail, Matrix4x4.Identity);
                scene.Advance(tail, 17);
                packets += beam.Draw(tail, camera).Count;
            }
            Assert.True(beam.Retired, name);
        }
        Assert.True(packets > 0);
        // avatardef.ini supplies both fixed presentation actors.
        Assert.NotNull(EnbAvatarPortraitAsset.Load(assets, EnbRetailAvatarDefaults.Narrator(assets)));
        Assert.NotNull(EnbAvatarPortraitAsset.Load(assets, EnbRetailAvatarDefaults.Help(assets)));
        // 0077E9D0/0077F000 test AvatarData type (actor+0xF8+8) against zero: any nonzero type takes the ANPC parts.
        Assert.NotNull(EnbAvatarPortraitAsset.Load(assets, EnbRetailAvatarDefaults.Narrator(assets) with { AvatarType = 7 }));
    }
}
