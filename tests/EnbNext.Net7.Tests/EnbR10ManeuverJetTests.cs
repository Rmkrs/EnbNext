using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR10ManeuverJetTests
{
    [Theory]
    [InlineData(0, 0f, "")]
    [InlineData(1, 0f, "")]
    [InlineData(0, .15707964f, "")]
    [InlineData(0, -.15707964f, "")]
    [InlineData(0, .16f, "03")]
    [InlineData(1, -.16f, "12")]
    [InlineData(2, .5f, "45")]
    [InlineData(2, -.5f, "45")]
    public void SignedBankThresholdAndReversePriorityMatchRetail(int thrust, float bank, string expected)
    {
        var scene = new EnbPresentationSnapshotBuilder().Build(EnbR11FlightTests.FlightWorld(), null, 1100);
        var ship = Assert.Single(scene.Objects) with { ObjectType = 1, ShipVisualBankRadians = bank };
        ship = ship with { ShipPresentation = ship.ShipPresentation! with { EngineThrustState = thrust } };
        string Slots(EnbSceneObjectSnapshot obj) => string.Concat(Enumerable.Range(0,6).Where(s => EnbShipManeuverJetRuntime.IsActive(obj,s)));
        Assert.Equal(expected, Slots(ship));
        Assert.Equal(expected, Slots(ship with { IsLocalShip = false }));
        Assert.Equal("", Slots(ship with { ShipPresentation = ship.ShipPresentation! with { IsOrganic = true } }));
        Assert.Equal("", Slots(ship with { ObjectType = 25 }));
        Assert.Equal(thrust == 2 ? "45" : "", Slots(ship with { ShipVisualBankRadians = null }));
    }

    [Fact]
    public void InstalledJetSpritesRetainInheritedVelocityAndDrainWithoutRestartKick()
    {
        var paths = EnbInstalledAssetLocator.TryLocate(); if (paths is null) return;
        var assets = EnbRetailAssetBundle.Load(paths);
        foreach (string name in new[] { "sidethr_PL", "sidethr_PR", "Forethr_PF" })
        {
            var sprite = EnbCurrentRetailShipExhaustAsset.Load(assets, name + "_p2", allowInheritedMotion: true);
            Assert.NotNull(sprite.Texture); Assert.Null(sprite.Error); Assert.Null(sprite.DeferredReason);
            var line = EnbCurrentRetailShipExhaustAsset.Load(assets, name + "_p1", allowInheritedMotion: true);
            Assert.Null(line.DeferredReason); Assert.NotNull(line.Texture);
            var buffer = new EnbCurrentRetailShipParticleRuntime(sprite.Definition, allowInheritedMotion: true,randomStreams:new());
            var stationaryBuffer = new EnbCurrentRetailShipParticleRuntime(sprite.Definition.Copy().SetVelocityInheritanceFactor(0),randomStreams:new());
            buffer.Advance(0, Matrix4x4.Identity, true);
            buffer.Advance(21, Matrix4x4.CreateTranslation(21,0,0), true);
            stationaryBuffer.Advance(0, Matrix4x4.Identity, true);
            stationaryBuffer.Advance(21, Matrix4x4.CreateTranslation(21,0,0), true);
            Assert.Equal(2, buffer.Births.Count);
            Assert.All(buffer.Births, b => Assert.Equal(.1f, b.InheritedVelocityPerMillisecond.X, 5));
            var emitter = new EnbW3dParticleEmitterDrawInstance(sprite.Definition.Name, sprite.Key.ResourceName, 1,
                EnbRenderPassKind.OrdinaryWorld, sprite.Definition, sprite.Texture!, Matrix4x4.Identity, "fixture", 0, 0);
            EnbResolvedRenderCamera.TryCreate(Vector3.Zero, Quaternion.Identity, new(800,600,1), Math.PI/2, new(1,10000), out var camera);
            EnbW3dDrawTask Draw(EnbCurrentRetailParticleBirth birth)
            {
                Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(sprite.Key,1,1,emitter,camera,
                    EnbW3dLightEnvironment.BlackNoLights,1,0,21,out var packet,out _,out var error,worldBirths:[birth]),error);
                return Assert.IsType<EnbW3dDrawTask>(packet);
            }
            var original = buffer.Births[0];
            var moving = Draw(original).DeformedGeometry!.Positions;
            // The birth writer already includes inherited velocity in the
            // retained sampled velocity. Compare an actual zero-inheritance
            // birth rather than editing an input field after sampling.
            var stationary = Draw(stationaryBuffer.Births[0]).DeformedGeometry!.Positions;
            for(int i=0;i<moving.Count;i++)
                Assert.InRange(Vector3.Distance(moving[i]-stationary[i],new Vector3(1.1f,0,0)),0,.0001f);
            var retained = buffer.Births[0];
            buffer.Advance(31, Matrix4x4.CreateTranslation(31,0,0), false);
            Assert.Equal(retained, buffer.Births[0]);
            buffer.Advance(232, Matrix4x4.CreateTranslation(232,0,0), false);
            Assert.Empty(buffer.Births);
            buffer.Advance(253, Matrix4x4.CreateTranslation(253,0,0), true);
            Assert.All(buffer.Births, b => Assert.Equal(Vector3.Zero, b.InheritedVelocityPerMillisecond));
        }
    }
}
