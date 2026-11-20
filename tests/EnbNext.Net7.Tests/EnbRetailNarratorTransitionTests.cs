using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbRetailNarratorTransitionTests
{
    [Fact]
    public void RevealDelayAndHideCompletionFollowCharacterViewState()
    {
        var state=new EnbRetailNarratorTransition();
        Assert.True(state.Hidden);
        Assert.False(state.Request(true)); // 67A8D0 rejects a request during transition.
        for(int i=0;i<3;i++){state.Update(false);Assert.True(state.Hidden);}
        state.Update(false);Assert.False(state.Hidden);
        Assert.False(state.Request(false));
        Assert.True(state.Request(true));
        state.Update(false);Assert.False(state.Hidden);
        state.Update(true);Assert.True(state.Hidden);
        Assert.True(state.Request(false));
        for(int i=0;i<3;i++){state.Update(true);Assert.True(state.Hidden);}
        state.Update(true);Assert.False(state.Hidden);
    }
}
