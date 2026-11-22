using EnbNext.Engine;

namespace EnbNext.Net7.Tests;

public sealed class EnbNextModernChatTabsTests
{
    // Tab files written before categories and channels, damaged files and empty files all start the default tabs.
    [Theory]
    [InlineData("{\"Tabs\":[{\"Name\":\"General\",\"Types\":[0,1,2]},{\"Name\":\"Social\",\"Types\":[3,4]}],\"Active\":1}")]
    [InlineData("{\"Tabs\":[],\"Active\":3}")]
    [InlineData("not json")]
    public void UnreadableOrOlderTabFilesStartTheDefaultTabs(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), "enbnext-modern-chat-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, content);
        try
        {
            using var chat = new EnbChatUi(() => 555) { ModernTabsPath = _ => path };
            Assert.Equal(4, chat.ModernTabCount);
        }
        finally { File.Delete(path); }
    }

    // Tabs keep their chat windows; a window number left unused closes up, and a file from before the windows is one window.
    [Theory]
    [InlineData("{\"Tabs\":[{\"Name\":\"A\",\"Kinds\":[\"Say\"],\"Window\":0},{\"Name\":\"C\",\"Kinds\":[\"Say\"],\"Window\":2},{\"Name\":\"B\",\"Kinds\":[\"Say\"],\"Window\":0}],\"Active\":0,\"Windows\":[{\"Active\":1},null,{\"Active\":0,\"X\":0.5,\"Y\":0.1,\"Width\":300,\"Height\":150}]}", "A,B|C")]
    [InlineData("{\"Tabs\":[{\"Name\":\"A\",\"Kinds\":[\"Say\"]},{\"Name\":\"B\",\"Kinds\":[\"Say\"]}],\"Active\":1}", "A,B")]
    public void TabsKeepTheirChatWindows(string content, string expected)
    {
        string path = Path.Combine(Path.GetTempPath(), "enbnext-modern-chat-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, content);
        try
        {
            using var chat = new EnbChatUi(() => 555) { ModernTabsPath = _ => path };
            Assert.Equal(expected, string.Join("|", chat.ModernWindowTabs.Select(w => string.Join(",", w))));
        }
        finally { File.Delete(path); }
    }
}
