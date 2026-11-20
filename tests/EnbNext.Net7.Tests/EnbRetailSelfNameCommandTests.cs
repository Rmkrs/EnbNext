using EnbNext.Assets;
using Xunit;

namespace EnbNext.Net7.Tests;

public sealed class EnbRetailSelfNameCommandTests
{
    [Theory]
    [InlineData("/self tag on",true)]
    [InlineData("/SELF TAG OFF",false)]
    [InlineData("/self tag",null)]
    [InlineData("/self tag on ",null)]
    [InlineData("/self tag on extra",null)]
    public void ExactLocalCommands(string text,bool? expected)
        => Assert.Equal(expected,EnbRetailSelfNameCommand.Parse(text));
}
