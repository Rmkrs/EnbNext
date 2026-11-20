using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR10VerbPresentationTests
{
    [Theory]
    [InlineData(0x000d0000u,"Follow",true)]
    [InlineData(0x00060000u,"Trade",true)]
    [InlineData(0x00060002u,"Trade: Too Far",false)]
    [InlineData(0x00050000u,"Message",true)]
    [InlineData(0x00040000u,"Group",true)]
    [InlineData(0x00040001u,"Group: Player already in a group",false)]
    [InlineData(0x000b0000u,"Register",true)]
    [InlineData(0x00080000u,"Dock",true)]
    [InlineData(0x0008ffffu,"Dock: Unavailable",false)]
    [InlineData(0x00080003u,"Dock",false)]
    [InlineData(0xffff0002u,"unknown",false)]
    public void CurrentRetailNameAndStatusTables(uint value,string text,bool enabled)
    {
        Assert.Equal(text,EnbCurrentRetailVerbPresentation.Format(value));
        Assert.Equal(enabled,EnbCurrentRetailVerbPresentation.IsEnabled(value));
    }
}
