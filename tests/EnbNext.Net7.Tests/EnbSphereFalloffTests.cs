using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbSphereFalloffTests
{
    [Fact]
    public void IdentityFadesBothAxialPolesAndKeepsEquator()
    {
        foreach(var normal in new[]{Vector3.UnitX,-Vector3.UnitX})
            Assert.Equal(new Vector4(1,1,1,0),EnbRetailSphereFalloff.Evaluate(normal,Quaternion.Identity,1,false,false));
        Assert.Equal(Vector4.One,EnbRetailSphereFalloff.Evaluate(Vector3.UnitY,Quaternion.Identity,1,false,false));
    }

    [Fact]
    public void InversionAndDestinationBlendChooseDifferentColorChannels()
    {
        Assert.Equal(new Vector4(.25f,.25f,.25f,0),EnbRetailSphereFalloff.Evaluate(Vector3.UnitX,Quaternion.Identity,.25f,true,true));
        Assert.Equal(new Vector4(1,1,1,.75f),EnbRetailSphereFalloff.Evaluate(Vector3.UnitX,Quaternion.Identity,-.25f,false,false));
        Assert.Equal(new Vector4(1,1,1,0),EnbRetailSphereFalloff.Evaluate(Vector3.UnitX,Quaternion.Identity,3,false,false));
    }

    [Fact]
    public void DirectionUsesAuthoredQuaternionWithoutNormalization()
    {
        // Exact components make direction (0,2,0); normalizing would give (0,1,0).
        var orientation=new Quaternion(0,0,1,1);
        Assert.Equal(new Vector4(1,1,1,.5f),EnbRetailSphereFalloff.Evaluate(Vector3.UnitY,orientation,.25f,false,false));
        Assert.Equal(Vector4.One,EnbRetailSphereFalloff.Evaluate(Vector3.UnitX,orientation,.25f,false,false));
        var negated=new Quaternion(0,0,-1,-1);
        Assert.Equal(EnbRetailSphereFalloff.Evaluate(Vector3.UnitY,orientation,.25f,false,false),
            EnbRetailSphereFalloff.Evaluate(Vector3.UnitY,negated,.25f,false,false));
    }
}
