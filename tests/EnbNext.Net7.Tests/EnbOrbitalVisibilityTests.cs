using System.Numerics;
using EnbNext.Rendering;
namespace EnbNext.Net7.Tests;

public sealed class EnbOrbitalVisibilityTests
{
    [Fact]
    public void EclipseVisibilityIsFractionalAndIgnoresObjectsBehindTheSource()
    {
        EnbW3dWorldBounds sun = new(new(0,0,100),10);
        float Visibility(EnbW3dWorldBounds blocker)
        {
            int blocked=0;
            return EnbRetailOrbitalFogEvaluator.ComputeVisibility(Vector3.Zero,0,[sun,blocker],ref blocked);
        }
        Assert.Equal(1,Visibility(new(new(0,0,150),10)));
        Assert.Equal(0,Visibility(new(new(0,0,50),10)));
        Assert.InRange(Visibility(new(new(3,0,50),3)),1f/16,15f/16);
    }
    [Fact]
    public void DazzleOwnerRetainsNonEmitterVisibilityAndGatesOnlyBelowTheHorizon()
    {
        var orbital = new EnbNext.Assets.EnbRetailOrbitalDefinition(0, 1, 1, 0, 0, 0,
            0, 0, 0, 100, 0, 0, true, "light", "sky", .1f, false);
        EnbW3dWorldBounds[] clear = [new(new(0,0,100),10)];
        EnbW3dWorldBounds[] eclipsed = [clear[0],new(new(0,0,50),10)];
        float Visibility(int mode, float elevation, bool emitter, EnbW3dWorldBounds[] spheres)
            => EnbRetailOrbitalFogEvaluator.ComputeDazzleVisibility(mode,
                orbital with { EmitsLight = emitter }, elevation, Vector3.Zero, 0, spheres);
        Assert.Equal(0, Visibility(1, -.11f, true, clear));
        Assert.Equal(1, Visibility(1, -.1f, true, clear));
        Assert.Equal(1, Visibility(0, -.11f, true, clear));
        Assert.Equal(0, Visibility(1, .1f, true, eclipsed));
        Assert.Equal(1, Visibility(1, -.11f, false, eclipsed));
    }
}
