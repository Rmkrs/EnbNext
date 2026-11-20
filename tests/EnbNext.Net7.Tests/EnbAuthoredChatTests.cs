using System.Text;
using EnbNext.Assets;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.Social;
using EnbNext.Presentation;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class EnbAuthoredChatTests
{
    [Fact]
    public void NamedPacketsAreEventsIncludingRepeatedNamesAndDoNotBecomeHistory()
    {
        var game = new Net7GameplayState();
        game.Apply(Packet.ForOpcode(0x23, Encoding.Latin1.GetBytes("Level\0")));
        var first = EnbSceneChatSnapshot.From(game.Social);
        game.Apply(Packet.ForOpcode(0x23, Encoding.Latin1.GetBytes("Level\0")));
        var next = EnbSceneChatSnapshot.From(game.Social);
        Assert.NotSame(first, next);
        Assert.Equal(2, next.Events.Length);
        Assert.All(next.Events, e => { Assert.Equal(Net7ChatPresentationKind.NamedScreenLine, e.Kind); Assert.Equal("Level", e.Text); Assert.Null(e.ScreenLine); });
        Assert.True(next.Events[1].Sequence > next.Events[0].Sequence);
        Assert.Empty(next.Lines);
        Assert.Equal("Level", game.SuperLine!.Text);
        Assert.Same(next, EnbSceneChatSnapshot.From(game.Social));
    }

    [Fact]
    public void EphemeralWeightsAreCumulativeTruncatedAndHaveAnUnselectedInclusiveEndpoint()
    {
        var catalog = EnbRetailEphemeralCatalog.Parse(EnbRetailIniDocument.Parse("""
            [Ephemeral-00]
            Type=Level
            Text=Level Up!
            FontType00=Simple
            FontChance00=50.9 trailing text
            FontType01=Fancy
            FontChance01=30.9
            FontType02=Complex
            FontChance02=20.9
            [Ephemeral-02]
            Type=Skipped
            """));
        var level = Assert.IsType<EnbRetailEphemeralCatalog.Entry>(catalog.Find("Level"));
        Assert.Null(catalog.Find("level")); Assert.Null(catalog.Find("Skipped"));
        Assert.Equal(100, level.TotalChance);
        Assert.Equal("Simple", level.SelectStyle(49));
        Assert.Equal("Fancy", level.SelectStyle(50));
        Assert.Equal("Complex", level.SelectStyle(80));
        Assert.Equal("Complex", level.SelectStyle(99));
        Assert.Null(level.SelectStyle(100));
    }

    [Fact]
    public void EphemeralDuplicateTypesReplaceAndMissingOrEmptyStylesDoNotInventFallbacks()
    {
        var catalog = EnbRetailEphemeralCatalog.Parse(EnbRetailIniDocument.Parse("""
            [Ephemeral-00]
            Type=Same
            Text=Old
            [Ephemeral-01]
            Type=Same
            FontType00=
            FontChance00=10
            FontType01=UnknownAuthoredStyle
            FontChance01=5
            FontType03=IgnoredAfterGap
            FontChance03=100
            [Ephemeral-02]
            Type=Empty
            """));
        var value = catalog.Find("Same")!;
        Assert.Equal(" ", value.Text); Assert.Equal(15, value.TotalChance);
        Assert.Equal("UnknownAuthoredStyle", value.SelectStyle(0));
        Assert.Null(value.SelectStyle(15));
        Assert.Null(catalog.Find("Empty")!.SelectStyle(0));
    }

    [Fact]
    public void CommandsKeepAuthoredOrderDescriptionsDefaultsAndFirstGap()
    {
        var entries = EnbRetailSlashCommand.Parse(EnbRetailIniDocument.Parse("""
            [SLASH-000]
            Command=/z <arg>
            Desc=Shows a description.
            [SLASH-001]
            [SLASH-003]
            Command=/not-loaded
            """));
        Assert.Equal(2, entries.Count);
        Assert.Equal(new("/z <arg>", "Shows a description."), entries[0]);
        Assert.Equal(new("Undefined", "No Description available"), entries[1]);
    }

    [Fact]
    public void InputHistoryWrapsTenSlotsWithoutBlankDraftAndResetsOnSubmitOnly()
    {
        var h = new EnbChatInputHistory();
        Assert.Null(h.Recall(true)); Assert.Null(h.Recall(false));
        for (int i = 0; i < 12; i++) h.Submit(i.ToString());
        Assert.Equal(Enumerable.Range(2, 10).Reverse().Select(i => i.ToString()), Enumerable.Range(0, 10).Select(_ => h.Recall(true)));
        Assert.Equal("11", h.Recall(true));
        Assert.Equal("2", h.Recall(false));
        h.Submit("");
        Assert.Equal("2", h.Recall(true));
        Assert.Equal("11", h.Recall(true));
        h.Clear(); Assert.Null(h.Recall(true));
    }

    [Fact]
    public void InputHistoryDeduplicatesAgainstRecalledSlotNotNewest()
    {
        var h = new EnbChatInputHistory();
        h.Submit("one"); h.Submit("two"); h.Submit("three");
        Assert.Equal("three", h.Recall(true)); Assert.Equal("two", h.Recall(true));
        h.Submit("two");
        Assert.Equal("two", h.Recall(true)); Assert.Equal("one", h.Recall(true));
        h.Submit("three"); // Not equal to the recalled "one": append a duplicate.
        Assert.Equal("three", h.Recall(true)); Assert.Equal("three", h.Recall(true));
    }
}
