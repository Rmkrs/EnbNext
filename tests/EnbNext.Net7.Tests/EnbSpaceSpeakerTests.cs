using EnbNext.Assets;
using EnbNext.Net7.World;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbSpaceSpeakerTests
{
    [Fact]
    public void IngressColorSurvivesPaletteChangeBeforeFirstPresentation()
    {
        var world=new Net7WorldState();world.MaterializeCreate(new(7,1,1620,1,0,0,0));
        var old=new EnbRgb(1,.6f,0);var changed=new EnbRgb(0,1,0);
        world.PublishSpaceSpeaker("Unknown",7,new(old.R,old.G,old.B));
        var scene=new EnbPresentationSnapshotBuilder().Build(world,null,0);
        var palette=EnbRetailChatColors.Defaults.SetItem(7,changed);
        var runtime=new EnbSpaceSpeakerRuntime();
        Assert.Equal(old,runtime.ApplyFrame(scene,0,palette).Objects.Single().SpeakingColor);
        world.PublishSpaceSpeaker("Unknown",7,new(changed.R,changed.G,changed.B));
        scene=new EnbPresentationSnapshotBuilder().Build(world,null,0);
        Assert.Equal(changed,runtime.ApplyFrame(scene,0,EnbRetailChatColors.Defaults).Objects.Single().SpeakingColor);
    }
    [Fact]
    public void IngressUsesFirstExactRegistryIdentityAndNeverReplaysToLaterObjects()
    {
        var world=new Net7WorldState();
        world.PublishSpaceSpeaker("Unknown",7);
        world.MaterializeCreate(new(258,1,1620,1,0,0,0));
        world.MaterializeCreate(new(1,1,1620,1,0,0,0));
        var first=world.Objects.FindFirst(258)!;var second=world.Objects.FindFirst(1)!;
        Assert.Equal(0ul,first.SpeakerRevision);
        world.PublishSpaceSpeaker("unknown",7);Assert.Equal(0ul,first.SpeakerRevision);
        world.PublishSpaceSpeaker("Unknown",7);
        Assert.Equal(1ul,first.SpeakerRevision);Assert.Equal(0ul,second.SpeakerRevision);
        world.PublishSpaceSpeaker("Unknown",3);
        Assert.Equal(2ul,first.SpeakerRevision);Assert.Equal((byte)3,first.SpeakerType);
        world.Objects.RemoveFirst(258);world.PublishSpaceSpeaker("Unknown",4);
        Assert.Equal(1ul,second.SpeakerRevision);
    }

    [Fact]
    public void CompletedFramesCountDownCaptureColorAndResetOnlyOnNewSpeech()
    {
        var world=new Net7WorldState();world.MaterializeCreate(new(7,1,1620,1,0,0,0));
        var builder=new EnbPresentationSnapshotBuilder();var runtime=new EnbSpaceSpeakerRuntime();
        var palette=EnbRetailChatColors.Defaults.ToArray();
        EnbSceneObjectSnapshot Frame(uint delta)=>runtime.ApplyFrame(builder.Build(world,null,0),delta,palette).Objects.Single();
        Assert.Null(Frame(0).SpeakingColor);
        world.PublishSpaceSpeaker("Unknown",7);
        Assert.Equal(palette[7],Frame(500).SpeakingColor);
        var original=palette[7];palette[7]=new(0,1,0);
        Assert.Equal(original,Frame(1000).SpeakingColor);
        Assert.Null(Frame(500).SpeakingColor); // Space expires at zero; station's text gate differs.
        Assert.Null(Frame(0).SpeakingColor);
        world.PublishSpaceSpeaker("Unknown",7);
        Assert.Equal(palette[7],Frame(1999).SpeakingColor);
        world.PublishSpaceSpeaker("Unknown",7);
        Assert.Equal(palette[7],Frame(1000).SpeakingColor);
        Assert.Null(Frame(1001).SpeakingColor);
        world.Objects.RemoveFirst(7);world.MaterializeCreate(new(7,1,1620,1,0,0,0));
        Assert.Null(Frame(0).SpeakingColor);
    }
}
