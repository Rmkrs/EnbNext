using EnbNext.Net7.Gameplay;
using EnbNext.Protocol.Net;
using EnbNext.Presentation;

namespace EnbNext.Net7.Tests;

public sealed class Net7IncomingMessageTests
{
    private static void Offer(Net7GameplayState state,uint type,uint id,string caption)
        => state.Apply(Packet.ForOpcode(0xBE,Net7UiPresentationWireTests.Build(w=>{w.NetworkU32(type);w.NetworkU32(id);w.SharedString(caption);})));
    private static void Timeout(Net7GameplayState state,uint id)
        { state.Apply(Packet.ForOpcode(0xBF,Net7UiPresentationWireTests.Build(w=>{w.NetworkU32(1);w.NetworkU32(id); }))); }

    [Fact]
    public void OnlyServerMessageOffersExposeAButtonAndChatCacheTracksIt()
    {
        var state=new Net7GameplayState();
        Offer(state,0,4,"Confirm");Assert.Null(state.IncomingMessage);
        var before=EnbSceneChatSnapshot.From(state.Social,state.IncomingMessage);
        Offer(state,1,5,"Incoming Message");
        var shown=EnbSceneChatSnapshot.From(state.Social,state.IncomingMessage);
        Assert.NotSame(before,shown);Assert.Equal(5u,shown.IncomingMessage!.Id);
        Assert.Same(shown,EnbSceneChatSnapshot.From(state.Social,state.IncomingMessage));
        Timeout(state,5);Assert.Null(state.IncomingMessage);
        Assert.Null(EnbSceneChatSnapshot.From(state.Social,state.IncomingMessage).IncomingMessage);
        Assert.False(state.TryConsumeConfirmedAction(1,5,out _));
        Assert.Contains((0u,4u),state.ConfirmedActions.Keys);
    }

    [Fact]
    public void LatestCaptionAcknowledgesOldestIdAndClickHidesRemainingQueue()
    {
        var state=new Net7GameplayState();Offer(state,1,7,"First");Offer(state,1,8,"Second");
        Assert.Equal("Second",state.IncomingMessage!.Message);Assert.Equal(7u,state.IncomingMessage.Id);
        Assert.False(state.TryConsumeConfirmedAction(1,8,out _));
        Assert.True(state.TryConsumeConfirmedAction(1,7,out var pending));Assert.Equal(7u,pending!.Id);
        Assert.Null(state.IncomingMessage);Assert.False(state.TryConsumeConfirmedAction(1,7,out _));
        Offer(state,1,9,"Third");Assert.Equal(8u,state.IncomingMessage!.Id);
        Timeout(state,8);Assert.Equal(9u,state.IncomingMessage!.Id);
        Assert.Equal("Third",state.IncomingMessage.Message);
        Assert.True(state.TryConsumeConfirmedAction(1,9,out _));Assert.Null(state.IncomingMessage);
    }

    [Fact]
    public void DuplicateOfferIdsRemainSeparateNativeQueueEntries()
    {
        var state=new Net7GameplayState();Offer(state,1,7,"First");Offer(state,1,7,"Again");
        Assert.True(state.TryConsumeConfirmedAction(1,7,out _));
        Offer(state,1,8,"Next");Assert.Equal(7u,state.IncomingMessage!.Id);
        Assert.True(state.TryConsumeConfirmedAction(1,7,out _));
    }

    [Fact]
    public void TimeoutWaitsForVisibleHeadAndSettlesBeforePublication()
    {
        var state=new Net7GameplayState();Offer(state,1,7,"First");Offer(state,1,8,"Second");
        Timeout(state,8);Assert.Equal(7u,state.IncomingMessage!.Id);
        Assert.True(state.TryConsumeConfirmedAction(1,7,out _));
        Assert.Null(state.IncomingMessage);
        Offer(state,1,9,"Third");Assert.Equal(9u,state.IncomingMessage!.Id);
        Timeout(state,9);Assert.Null(state.IncomingMessage);
    }

    [Fact]
    public void HandoffRemovesMessageAndOldResponseAuthority()
    {
        var state=new Net7GameplayState();Offer(state,1,7,"Message");state.ResetForServerHandoff();
        Assert.Null(state.IncomingMessage);Assert.False(state.TryConsumeConfirmedAction(1,7,out _));
        Offer(state,1,8,"New sector");Assert.Equal(8u,state.IncomingMessage!.Id);
    }
}
