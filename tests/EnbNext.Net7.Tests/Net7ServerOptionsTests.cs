namespace EnbNext.Net7.Tests;

public sealed class Net7ServerOptionsTests
{
    [Fact]
    public void DefaultsMatchClosedCurrentEndpointTopology()
    {
        var options = new Net7ServerOptions();

        Assert.Equal(3806, options.MasterControlUdpPort);
        Assert.Equal(3807, options.AccountTcpPort);
        Assert.Equal(3808, options.MasterHandoffUdpPort);
        Assert.Equal(3501, Net7ServerOptions.CurrentMinGameplaySectorUdpPort);
        Assert.Equal(4000, Net7ServerOptions.CurrentMaxGameplaySectorUdpPort);

        Assert.Equal(20, options.MasterRequestAttempts);
        Assert.Equal(4, options.MasterResponsePollsPerAttempt);
        Assert.Equal(TimeSpan.FromMilliseconds(250), options.MasterResponsePollInterval);
        Assert.Equal(4, options.IntermediateLoginWatchdogSends);
        Assert.Equal(50, options.IntermediateLoginPollsPerCycle);
        Assert.Equal(TimeSpan.FromSeconds(5), options.IntermediateLoginPollInterval);
        Assert.Equal(Net7FeatureFlags.None, options.FeatureFlags);
    }
}
