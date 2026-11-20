using System.Buffers.Binary;
using EnbNext.Net7.Protocol;
using Xunit;

namespace EnbNext.Net7.Tests;

public sealed class Net7StationBrowserTests
{
    [Theory]
    [InlineData(0x12345678u, "2018915346")]
    [InlineData(0x01000000u, "1")]
    [InlineData(0xFFFFFFFFu, "4294967295")]
    public void BrowserIdentityUsesRetailHalfSwapAndEmptyLoginKey(uint fixedId, string expected)
    {
        var join = Net7SectorJoinClientData.CreateSuccessful(fixedId, 42, 3);
        Assert.Equal("characterid=" + expected + "&lkey=", Net7StationBrowser.BuildQuery(join));
    }

    [Fact]
    public void PreservedJoinUsesBothHalvesAndSignedDecimal()
    {
        var bytes = Net7SectorJoinClientData.CreateSuccessful(0x01000000, 42, 0).ToWireBytes();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x0C, 4), 0x80000000);
        Assert.Equal("characterid=-9223372036854775807&lkey=", Net7StationBrowser.BuildQuery(Net7SectorJoinClientData.FromWireBytes(bytes)));
    }

    [Theory]
    [InlineData("https://example.invalid/browser", "https://example.invalid/browser?characterid=1&lkey=")]
    [InlineData("https://example.invalid/browser?page=2#jobs", "https://example.invalid/browser?page=2&characterid=1&lkey=#jobs")]
    [InlineData("https://example.invalid/browser?characterid=1&lkey=", "https://example.invalid/browser?characterid=1&lkey=")]
    public void PortalQueryPreservesNavigationAndDoesNotRepeatItself(string input, string expected)
    {
        var address = Net7StationBrowser.AppendQuery(new Uri(input), "characterid=1&lkey=");
        Assert.Equal(expected, address.AbsoluteUri);
        Assert.Equal(address, Net7StationBrowser.AppendQuery(address, "characterid=1&lkey="));
    }
}
