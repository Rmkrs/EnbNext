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
}
