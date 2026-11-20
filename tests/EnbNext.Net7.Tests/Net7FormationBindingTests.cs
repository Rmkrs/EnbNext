using EnbNext.Net7.Gameplay;
using EnbNext.Net7.Social;

namespace EnbNext.Net7.Tests;

public sealed class Net7FormationBindingTests
{
    [Theory]
    [InlineData(false,false,1,0,"Pipe",0)]
    [InlineData(true,true,1,1,"Pipe",0)]
    [InlineData(true,false,0,1,"Pipe",0)]
    [InlineData(true,false,1,0,"",0)]
    [InlineData(true,false,1,0,"Pipe",7)]
    [InlineData(true,false,1,1,"Pipe",8)]
    [InlineData(true,false,1,1,"",8)]
    [InlineData(true,false,2,99,"",8)]
    public void ToggleUsesRetainedFormationAndMembership(bool member,bool leader,int state,int formation,string name,int expected)
    {
        var plan=Net7SocialGroupPolicy.PlanFormationToggle(42,member,leader,state,(uint)formation,name);
        Assert.Equal(expected!=0,plan.ShouldSend);
        if(expected==0)return;
        Assert.Equal(0x00BC,plan.Opcode);
        Assert.Equal(Net7GameplayWire.BuildCtaRequest(42,0,(uint)expected),plan.Payload);
    }
}
