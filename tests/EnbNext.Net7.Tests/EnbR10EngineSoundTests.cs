using EnbNext.Assets;
using EnbNext.Net7.World;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR10EngineSoundTests
{
    [Theory]
    [InlineData(0, "engine_loop_1")]
    [InlineData(7, "engine_loop_8")]
    [InlineData(14, "engine_loop_15")]
    [InlineData(999, "engine_loop_1")]
    public void RetainedSelectorNotModelRaceOrTargetChoosesSound(int trail, string expected)
    {
        var world = new Net7WorldState();
        world.MaterializeCreate(new Net7CreateUpdate(23, 1, 10, 0, 0, 0, 0));
        var obj = new EnbPresentationSnapshotBuilder().Build(world, null, 0).Objects[0] with
        { ObjectType = 1, ShipPresentation = Ship() with { EngineTrailType = trail } };
        var sounds = EnbRetailSoundCatalog.ParseText("[engine_loop_1]\nSoundFilename=a.wav\n[engine_loop_8]\nSoundFilename=b.wav\n[engine_loop_15]\nSoundFilename=c.wav");
        Assert.Equal(expected, EnbCurrentRetailEngineSoundRuntime.ResolveName(obj, sounds));
        Assert.Equal(expected, EnbCurrentRetailEngineSoundRuntime.ResolveName(obj with { IsLocalShip = true, IsCurrentTarget = true, BaseAsset = 555 }, sounds));
        Assert.Equal("engine_loop_1",EnbCurrentRetailEngineSoundRuntime.ResolveName(obj with { ObjectType = 0 }, sounds));
        Assert.Null(EnbCurrentRetailEngineSoundRuntime.ResolveName(obj with { ShipPresentation = Ship() with { EngineTrailType = null } }, sounds));
        Assert.Null(EnbCurrentRetailEngineSoundRuntime.ResolveName(obj with { ShipPresentation = Ship() with { IsOrganic = true } }, sounds));
    }

    [Theory]
    [InlineData(null, 0, false)]
    [InlineData(0, 0, false)]
    [InlineData(1, 0, true)]
    [InlineData(2, 0, false)]
    [InlineData(1, 1, false)]
    [InlineData(1, 2, false)]
    [InlineData(1, 3, true)]
    [InlineData(1, 7, true)]
    [InlineData(1, 8, true)]
    [InlineData(1, 9, true)]
    [InlineData(1, 11, true)]
    public void CachedWarpGatesOrdinaryThrust(int? thrust, int cachedWarp, bool active)
        => Assert.Equal(active, EnbCurrentRetailEngineSoundRuntime.ShouldPlay(Ship() with
        { EngineThrustState = thrust, CachedWarpState = cachedWarp, PrivateWarpState = 1, GlobalWarpState = 2 }));
    [Fact]
    public void EffectsAcceptDopplerWithoutRelaxingEngineLoopPolicy()
    {
        var sound = EnbRetailSoundCatalog.ParseText("[engine]\nSoundFilename=a.wav\nSoundLoopCount=0\nSoundMinDopplerShift=.9\nSoundMaxDopplerShift=1.1").GetDefinitionOrFile("engine");
        Assert.Null(EnbCurrentRetailEngineSoundRuntime.ValidateDefinition(sound));
        Assert.Null(EnbCurrentRetailEffectSoundRuntime.ValidateDefinition(sound));
        Assert.Null(EnbCurrentRetailEffectSoundRuntime.ValidateDefinition(sound with { LoopCount=1, MinPitchShift=.1f }));
        Assert.NotNull(EnbCurrentRetailEffectSoundRuntime.ValidateDefinition(sound with { MinDopplerShift=0 }));
        Assert.NotNull(EnbCurrentRetailEffectSoundRuntime.ValidateDefinition(sound with { MaxDopplerShift=.8f }));
        Assert.NotNull(EnbCurrentRetailEffectSoundRuntime.ValidateDefinition(sound with { DopplerTweak=float.NaN }));
        Assert.NotNull(EnbCurrentRetailEngineSoundRuntime.ValidateDefinition(sound with { LoopCount = 1 }));
        Assert.NotNull(EnbCurrentRetailEngineSoundRuntime.ValidateDefinition(sound with { MinPitchShift = .1f }));
    }

    private static EnbSceneShipPresentationSnapshot Ship() => new(false, 0, 0, 0, false, null, 0, [], []) { EngineTrailType = 0 };

    [Theory]
    [InlineData(0,393,3)]
    [InlineData(25,398,2)]
    [InlineData(0,396,0)]
    public void NpcEngineUsesBaseAssetRemainder(int type,int asset,int index)
    {
        var obj=EnbCameraControlTests.Ship(0,0) with {ObjectType=(byte)type,BaseAsset=asset,ShipPresentation=Ship()};
        var sounds=EnbRetailSoundCatalog.ParseText($"[npc_engine_loop_{index}]\nSoundFilename=npc.wav");
        Assert.Equal($"npc_engine_loop_{index}",EnbCurrentRetailEngineSoundRuntime.ResolveName(obj,sounds));
        Assert.Equal($"npc_engine_loop_{index}",EnbCurrentRetailEngineSoundRuntime.ResolveName(obj with
            {ShipPresentation=Ship() with {EngineTrailType=99}},sounds));
    }
}
