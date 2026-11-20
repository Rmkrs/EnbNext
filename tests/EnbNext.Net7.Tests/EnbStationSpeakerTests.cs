using EnbNext.Assets;
using EnbNext.Net7.Social;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbStationSpeakerTests
{
    [Theory]
    [InlineData("Sector",2)] [InlineData("Group",4)] [InlineData("Guild",8)]
    [InlineData("General",9)] [InlineData("Out of Context",10)] [InlineData("Market",12)]
    [InlineData("New Players",11)] [InlineData("Terran",14)] [InlineData("123`private",13)] [InlineData("Custom`private",15)]
    public void ChannelSpeakerUsesRoutingIdentityAndClassifiedColor(string channel,byte type)
    {
        var social=new Net7SocialState();var speakers=new List<(string,byte)>();
        social.SpeakerPresented+=(name,color)=>speakers.Add((name,color));
        social.ApplyClientChatEvent(Net7SocialWire.DecodeClientChatEvent(Net7SocialWireTests.ChatEvent(3,0,["Route","Display","",channel,"Words",""],[])));
        Assert.Equal(("Route",type),Assert.Single(speakers));
        Assert.Equal(type,Assert.Single(social.PresentationEvents,e=>e.Kind==Net7ChatPresentationKind.SpeakerColor).Type);
        social.ApplyClientChatEvent(Net7SocialWire.DecodeClientChatEvent(Net7SocialWireTests.ChatEvent(3,0,["Route","Display","","Group","<S>1Member`symbol",""],[])));
        Assert.Single(speakers);
    }
    [Fact]
    public void LocalAndPrivateSpeakerUseTheirNativeTypes()
    {
        var social=new Net7SocialState();var speakers=new List<(string,byte)>();
        social.SpeakerPresented+=(name,color)=>speakers.Add((name,color));
        foreach(byte type in new byte[]{5,4})
            social.ApplyClientChatEvent(Net7SocialWire.DecodeClientChatEvent(Net7SocialWireTests.ChatEvent(type,0,["Route","Display","","","Words",""],[])));
        Assert.Equal(new[]{("Route",(byte)7),("Route",(byte)3)},speakers);
    }
    [Fact]
    public void SpeakerTimerPausesUnderPanelsAndRestoresCachedFirstName()
    {
        var label=new EnbStationSpeakerLabel("Pilot","Pilot Surname");
        var normal=new EnbRgb(.33f,.33f,.99f);var chat=new EnbRgb(1,.6f,0);
        label.Speak(chat);label.Advance(1000,false,normal);label.Advance(5000,true,normal);
        Assert.Equal(1,label.Remaining);Assert.Equal("\".....\"",label.Text);Assert.Equal(chat,label.Color);
        label.Advance(1001,false,normal);
        Assert.Equal("Pilot",label.Text);Assert.False(label.Flash);Assert.Equal(normal,label.Color);
        label.Speak(chat);label.Advance(2000,false,normal);
        Assert.Equal(0,label.Remaining);Assert.True(label.Flash);Assert.Equal(normal,label.Color);
        Assert.Equal(.25f,EnbStationSpeakerLabel.AdvanceFlash(1,125));
        Assert.Equal(2,EnbStationSpeakerLabel.AdvanceFlash(1,1000)); // Single subtraction, not modulo.
    }
}
