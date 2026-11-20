using System.Numerics;
using EnbNext.Assets;
using EnbNext.Net7.Presentation;
using EnbNext.Net7.World;
using EnbNext.Protocol.Net;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbStationGuildLabelTests
{
    [Fact]
    public void LabelRetainsTextAndColorUntilRankChanges()
    {
        var initial=new Net7StationGuildState("Guild",1,"Officer",new(.1f,.2f,.3f),new(.4f,.5f,.6f),"Guild");
        var label=new EnbStationGuildLabel(initial);
        Assert.Equal("[Guild Officer]",label.Text);Assert.Equal(new EnbRgb(.1f,.2f,.3f),label.Color);
        var changed=initial with {Name="Other",RankName="Leader",SameColor=Vector3.One,LocalName="Elsewhere"};
        label.Refresh(changed,new(0,0,0));Assert.Equal("[Guild Officer]",label.Text);
        label.Refresh(changed with {Rank=2},new(0,0,0));Assert.Equal("[Other]",label.Text);
        Assert.Equal(new EnbRgb(.4f,.5f,.6f),label.Color);
        label.Refresh(null,new(0,0,0));Assert.Equal("[Other]",label.Text);
        label.Refresh(changed with {Rank=-1},new(0,0,0));Assert.Equal("",label.Text);Assert.Equal(-1,label.Rank);
    }
    [Fact]
    public void SameGuildFormattingPreservesEmptyRankAndWhitespace()
    {
        Assert.Equal("[Guild ]",new EnbStationGuildLabel(new("Guild",0,"",null,null,"Guild")).Text);
        Assert.Equal("[Guild  Officer ]",new EnbStationGuildLabel(new("Guild",0," Officer ",null,null,"Guild")).Text);
        Assert.Equal("[Guild]",new EnbStationGuildLabel(new("Guild",0,"Officer",null,null,"guild")).Text);
    }
    [Fact]
    public void IngressCapturesGuildStateWithoutAliasingLaterAuxUpdates()
    {
        var world=new Net7WorldState();world.SetLocalPlayerId(1);
        var local=world.MaterializeCreate(new Net7CreateUpdate(1,1,0,1,0,0,0)).AuxData!;
        var remote=world.MaterializeCreate(new Net7CreateUpdate(2,1,0,0,0,0,0)).AuxData!;
        local.Resolve(1923).Value=new Net7AuxDataString("Guild"u8.ToArray());
        remote.Resolve(1923).Value=new Net7AuxDataString("Guild"u8.ToArray());
        remote.Resolve(1924).Value=BitConverter.GetBytes(1);
        remote.Resolve(1925).Value=new Net7AuxDataString("Officer"u8.ToArray());
        byte[] color=[..BitConverter.GetBytes(.1f),..BitConverter.GetBytes(.2f),..BitConverter.GetBytes(.3f)];
        remote.Resolve(1926).Value=color;
        world.Apply(Packet.ForOpcode(0x9e,Net7PresentationWireTests.BuildStarbaseAvatarChange(2,0,0,Vector3.Zero,0)),0);
        var captured=world.StationAvatarEvents[0].Guild!;
        Array.Clear(color);remote.Resolve(1924).Value=BitConverter.GetBytes(2);
        local.Resolve(1923).Value=new Net7AuxDataString("Other"u8.ToArray());
        Assert.Equal(1,captured.Rank);Assert.Equal("Guild",captured.LocalName);Assert.Equal(new Vector3(.1f,.2f,.3f),captured.SameColor);
    }
    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void ReceivedGuildColorSurvivesIngressAndPresentation(float component)
    {
        var world = new Net7WorldState();
        world.SetLocalPlayerId(1);
        var local = world.MaterializeCreate(new Net7CreateUpdate(1, 1, 0, 1, 0, 0, 0)).AuxData!;
        var remote = world.MaterializeCreate(new Net7CreateUpdate(2, 1, 0, 0, 0, 0, 0)).AuxData!;
        local.Resolve(1923).Value = new Net7AuxDataString("Guild"u8.ToArray());
        remote.Resolve(1923).Value = new Net7AuxDataString("Guild"u8.ToArray());
        remote.Resolve(1924).Value = BitConverter.GetBytes(1);
        remote.Resolve(1925).Value = new Net7AuxDataString("Officer"u8.ToArray());
        byte[] color = [..BitConverter.GetBytes(component), ..BitConverter.GetBytes(.2f), ..BitConverter.GetBytes(.3f)];
        remote.Resolve(1926).Value = color;
        remote.Resolve(1927).Value = color.ToArray();
        world.Apply(Packet.ForOpcode(0x9e, Net7PresentationWireTests.BuildStarbaseAvatarChange(2, 0, 0, Vector3.Zero, 0)), 0);
        var captured = world.StationAvatarEvents[0].Guild!;
        var scene = new EnbNext.Presentation.EnbPresentationSnapshotPublisher().Publish(world, assets: null, clientTime: 1);
        var projected = EnbStationGuildLabel.FromScene(scene, 2)!;
        foreach (var state in new[] { captured, projected })
        {
            Assert.Equal(component, state.SameColor!.Value.X);
            Assert.Equal(component, state.OtherColor!.Value.X);
            var label = new EnbStationGuildLabel(state);
            Assert.Equal("[Guild Officer]", label.Text);
            Assert.Equal(component, label.Color!.Value.R);
            Assert.Equal(.2f, label.Color.Value.G);
            Assert.Equal(.3f, label.Color.Value.B);
        }
    }
}
