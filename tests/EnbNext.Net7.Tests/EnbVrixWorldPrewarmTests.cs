using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbVrixWorldPrewarmTests
{
    [Theory]
    [InlineData("v-j_proj01_p4",500,20)]
    [InlineData("v-j_miss01_p1",250,12)]
    public void InstalledPrewarmKeepsNegativeBirthsAndFiniteMovingPoses(string name,int initial,int interval)
    {
        var paths=EnbInstalledAssetLocator.TryLocate(); Assert.NotNull(paths);
        var assets=EnbRetailAssetBundle.Load(paths);
        Assert.True(assets.RetailMixResources!.TryReadResource(name+".w3d",out var bytes,out _));
        Assert.True(EnbCurrentRetailParticleEmitterDecoder.TryDecode(bytes,out var d,out _));
        Assert.Equal(initial,(int)(d.InitialEmissionTimeSeconds*1000f));
        // Current retail uses PC24 FDIV before __ftol (0097D19C..D1B2).
        // The missile's float rate rounds that quotient to 12, not the 11
        // obtained by substituting host double-precision division.
        Assert.Equal(interval,(int)(1000f/d.EmissionRatePerSecond));
        Assert.False(d.LocalSpace); Assert.False(EnbCurrentRetailParticleEmitterRuntime.SupportsEmissionSpace(d));
        EnbCurrentRetailRObjParticleAsset.ValidateAnimated(d);
        var ship=new EnbCurrentRetailShipParticleRuntime(d,true);
        var bound=EnbCurrentRetailAnimatedParticleRuntime.CreateEmitter(d);
        ship.Advance(0,Matrix4x4.Identity,true);
        EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(bound,d,0,0,Matrix4x4.Identity,true,1,true);
        Assert.Empty(ship.Births);Assert.Empty(bound.WorldBirths);
        var pose=Matrix4x4.CreateRotationZ(.1f)*Matrix4x4.CreateTranslation(1,2,3);
        ship.Advance(10,pose,true);
        EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(bound,d,10,10,pose,true,1);
        long expected=((initial+10-1)/interval)*d.RuntimeBurstSize;
        Assert.Equal(expected,ship.EmittedCount);Assert.Equal(expected,bound.EmittedCount);
        Assert.Contains(ship.Births,b=>b.Time<0); Assert.Equal(ship.Births.Count,bound.WorldBirths.Count);
        for(int i=0;i<ship.Births.Count;i++)
        {
            var a=ship.Births[i];var b=bound.WorldBirths[i];
            Assert.Equal(a.Time,b.Time);
            Assert.True(float.IsFinite(a.World.M11+a.World.M22+a.World.M33+a.World.Translation.LengthSquared()));
            Assert.InRange(Vector3.Distance(a.World.Translation,b.World.Translation),0,.0001f);
            Assert.InRange(Vector3.Distance(a.InheritedVelocityPerMillisecond,b.InheritedVelocityPerMillisecond),0,.000001f);
        }
        var retained=ship.Births.ToArray();ship.Advance(10,Matrix4x4.Identity,true);Assert.Equal(retained,ship.Births);
        ship.Advance(11,pose,false);Assert.Equal(retained,ship.Births);
        ship.Advance(10000,pose,false);Assert.Empty(ship.Births);
        EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(bound,d,10000,9990,pose,false,1);
        Assert.Empty(bound.WorldBirths);
    }
}
