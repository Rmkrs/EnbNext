using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbSpaceCompositionTests
{
    [Theory]
    [InlineData(false, false, "Backdrop,Orbitals,World")]
    [InlineData(false, true, "Backdrop,Stars,Orbitals,World")]
    [InlineData(true, false, "Orbitals,Backdrop,World")]
    [InlineData(true, true, "Stars,Orbitals,Backdrop,World")]
    public void NativeFlagsSelectSceneOrderWithoutMovingForeground(bool swap, bool stars, string expected)
    {
        Assert.Equal(expected, string.Join(',', EnbRetailSpaceComposition.Passes(swap, stars)));
        Assert.Equal(new EnbRenderClipRange(20,100000),EnbRetailSpaceComposition.BackdropClip);
        Assert.Equal(new EnbRenderClipRange(50,150),EnbRetailSpaceComposition.StarsClip);
    }
}
