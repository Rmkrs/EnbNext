using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbRetailLoginNarrationTests
{
    [Theory]
    [InlineData(false,"Welcome")]
    [InlineData(true,"WelcomeReg")]
    public void FirstWelcomeMarksBothVariants(bool registration,string expected)
    {
        var state=new EnbRetailLoginNarration(false);
        Assert.Equal(expected,state.TakeIntro(0,registration));
        Assert.Equal("ReturningUser1",state.TakeIntro(1,!registration));
        Assert.Null(state.TakeIntro(0));
        Assert.Null(state.TakeIntro(1));
        Assert.Equal("ReturningUser2",state.TakeIntro(2));
        Assert.Equal("ReturningUser2",state.TakeIntro(5));
    }

    [Fact]
    public void CompletionMarkerSkipsWelcomeButDoesNotPreMarkReturningCues()
    {
        var state=new EnbRetailLoginNarration(true);
        Assert.Equal("ReturningUser2",state.TakeIntro(2));
        Assert.Null(state.TakeIntro(3));
        Assert.Null(state.TakeIntro(4));
        Assert.Equal("ReturningUser2",state.TakeIntro(5));
    }
}
