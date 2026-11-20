using EnbNext.Net7.Social;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbCallerPortraitTests
{
    [Fact]
    public void PacketIngressPreservesCommandsBeforeDisplayFilteringAndDoesNotReplaySnapshots()
    {
        var social = new Net7SocialState(id => id == 1 ? "Self" : "Caller", () => 1,
            id => id is 1 or 2 ? 9ul : null, name => name == "Caller" ? 2u : null);
        Net7PlayerNlpSegment[] commands = [new(0, 2, [], null), new(2, 17, [], null)];
        social.ApplyMessageChatstream(new(0, 4, 2, commands));
        var first = EnbSceneChatSnapshot.From(social);
        Assert.Empty(first.Lines);
        Assert.Equal(new Net7AvatarNlpCommand(2, 17, ""), first.AvatarSpeech.Single().Commands[1]);
        Assert.True(first.AvatarSpeech.Single().ShowCaller);
        Assert.Same(first, EnbSceneChatSnapshot.From(social));
        social.ApplyMessageChatstream(new(0, 3, 2, commands));
        Assert.Equal(2, social.AvatarSpeech.Last().Sequence);
        Assert.Single(first.AvatarSpeech); // Immutable published event list.
        social.ApplyMessageChatstream(new(0, 3, 1, commands));
        Assert.False(social.AvatarSpeech.Last().ShowCaller);
        social.ApplyMessageChatstream(new(0, 3, 99, commands));
        Assert.Equal(3, social.AvatarSpeech.Count);
        social.ApplyClientChatEvent(new(4, 0, "Caller", "Caller", "", "", "", "", [2, 17, 0, 0, 8, 0, 0, 0]));
        Assert.Equal(4, social.AvatarSpeech.Count);
        Assert.True(social.AvatarSpeech.Last().ShowCaller);
        social.ResetForHandoff(); Assert.Empty(social.AvatarSpeech);
        social.ResetForCharacterSelection(); Assert.NotEqual(first.StreamId, social.StreamId);
    }

    [Fact]
    public void SerializedCommandsKeepNativeSignedIdsMoodAndTerminalRules()
    {
        var received = new EnbRetailAvatarReceivedSpeech();
        var result = received.Consume([new(0, 2, ""), new(1, 17, "Hello"), new(2, 255, ""), new(3, 1, "")], 0);
        Assert.Equal(3, result.Count);
        Assert.Equal(17, result[0].AnimationNumber); Assert.Equal("Hello", result[0].Text); Assert.Equal(2, result[0].Mood);
        Assert.Equal(-1, result[1].AnimationNumber); Assert.Equal("breathe1", result[2].AnimationTag);
        Assert.Equal(1, received.Mood);
        Assert.Empty(received.Consume([], 1));
        Assert.Single(received.Consume([new(0, 3, "")], 1));
        Assert.Empty(received.Consume([new(6, 0, ""), new(2, 17, "")], 0));
        Assert.Equal(0, received.Locomotion);
        Assert.Equal(2, received.Consume([new(7, 0, ""), new(2, 17, "")], 0).Count);
        Assert.Empty(received.Consume([new(4, 0, ""), new(2, 17, "")], 0));
        Assert.Equal(1, received.Posture);
    }
}
