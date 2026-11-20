using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Net7.Tests;

public sealed class EnbAttachedLabelToggleTests
{
    [Fact]
    public void ToggleApproachesEachChannelAndRestoresOnFirstOutsideUpdate()
    {
        var effect=new EnbAttachedLabelToggle();
        EnbRgb Sample(long t,bool hover)=>effect.Update(t,hover,new(1,0,0),new(0,1,0),3);
        var original=Sample(0,false);
        var halfway=Sample(10,true);
        Assert.Equal(original.R-.3f,halfway.R,5);Assert.Equal(.3f,halfway.G,5);
        var target=Sample(100,true);Assert.Equal(0,target.R);Assert.Equal(original.R,target.G);
        Assert.Equal(original,Sample(101,false));
        effect.Pause();Assert.Equal(original,Sample(10000,true));
        Assert.Equal(halfway,Sample(10010,true));
    }
}
