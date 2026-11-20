using EnbNext.Assets;
using EnbNext.Net7.Social;
using System.Text;
using Xunit;

public sealed class EnbSharedChatStorageTests
{
    [Fact]
    public void UnchangedRewrapPreservesVisibleRowsAndHistoryMarkers()
    {
        var history = new EnbNext.Rendering.EnbRetailChatHistory();
        history.Append("Old", 0, 1, 2, text => [text]);
        history.Append("Visible", 0, 2, 2, text => [text]);
        history.Append("Newest", 0, 3, 2, text => [text]);
        var before = history.Rows.ToArray();
        Assert.True(before[0].HasHistoryMarker);
        Assert.False(before[1].HasHistoryMarker);
        history.Rewrap(text => [text]);
        Assert.Equal(before, history.Rows);
        history.Rewrap(text => [text[..1], text[1..]]);
        Assert.Equal(6, history.Rows.Count);
        Assert.All(history.Rows, row => Assert.True(row.HasHistoryMarker));
    }

    [Fact]
    public void SaveConfirmationIsLocalTypeZeroWithRetailFileName()
    {
        var social = new Net7SocialState();
        social.ReportQuickMessageSaved(4, "chat.ini");
        var message = Assert.Single(social.Messages);
        Assert.Equal(0, message.Type);
        Assert.Equal("COMPUTER: Quick message MSG_4 has been saved in chat.ini.", message.Text);
    }
    [Fact]
    public void QuickMessagesShareRetailSectionAndPreserveOtherData()
    {
        string directory = Path.Combine(Path.GetTempPath(), "enb-chat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "chat.ini");
            File.WriteAllText(path, "; retained\r\n[16909060]\r\nMSG_1=Retail message\r\nMSG_2=Second\r\nUnknown=keep\r\n[42]\r\nMSG_1=Another character\r\n", Encoding.Latin1);
            Assert.Equal("16909060", EnbRetailQuickMessages.Section(0x04030201));
            Assert.Equal("Retail message", EnbRetailQuickMessages.Load(path, 0x04030201)[0]);
            EnbRetailQuickMessages.Save(path, 0x04030201, 1, "Next message");
            var retail = EnbRetailIniDocument.Load(path);
            Assert.True(retail.TryGetSection("16909060", out var section));
            Assert.Equal("Next message", section["MSG_1"]);
            Assert.Equal("Second", section["MSG_2"]);
            Assert.Equal("keep", section["Unknown"]);
            Assert.True(retail.TryGetSection("42", out var other));
            Assert.Equal("Another character", other["MSG_1"]);
            Assert.Contains("; retained", File.ReadAllText(path));
            EnbRetailQuickMessages.Save(path, 0x04030201, 9, "");
            Assert.Equal("", EnbRetailQuickMessages.Load(path, 0x04030201)[8]);
            string before = File.ReadAllText(path);
            Assert.Throws<InvalidDataException>(() => EnbRetailQuickMessages.Save(path, 0x04030201, 1, "line\nMSG_2=bad"));
            Assert.Throws<InvalidDataException>(() => EnbRetailQuickMessages.Save(path, 0x04030201, 1, new string('x', 256)));
            Assert.Equal(before, File.ReadAllText(path));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void TypedChatOptionsRemainReadableByRetailAndKeepUnrelatedSettings()
    {
        string input = "[INT-007]\nOptionName=DefaultChatWindow\nOptionValue=3\n[BOOL-000]\nOptionName=Channel_Local\nOptionValue=yes\n[INT-008]\nOptionName=Unrelated\nOptionValue=42\n";
        string output = EnbRetailCinematicOptionsWriter.UpdateValues(input,
            [("INT", "DefaultChatWindow", "2"), ("INT", "ChatWinSplit_VertSplit", "180"), ("BOOL", "Channel_Local", "no"), ("STRING", "Channel_Label", "New Players")]);
        var values = EnbRetailIniDocument.Parse(output).EnumerateSections().Select(s => s.Value)
            .Where(s => s.ContainsKey("OptionName")).ToDictionary(s => s["OptionName"], s => s["OptionValue"]);
        Assert.Equal("2", values["DefaultChatWindow"]);
        Assert.Equal("180", values["ChatWinSplit_VertSplit"]);
        Assert.Equal("no", values["Channel_Local"]);
        Assert.Equal("New Players", values["Channel_Label"]);
        Assert.Equal("42", values["Unrelated"]);
    }
}
