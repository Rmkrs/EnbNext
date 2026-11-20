using System.Text.Json;
using EnbNext.Rendering;
using Xunit;

namespace EnbNext.Net7.Tests;

public sealed class EnbEnhancedGraphicsTests
{
    [Fact]
    public void ExistingPreferencesKeepHdrOffAndNewPreferencesRoundTrip()
    {
        var previous=JsonSerializer.Deserialize<EnbEnhancedGraphicsOptions>("{\"Enabled\":true,\"Bloom\":true}");
        Assert.True(previous.BloomActive);Assert.False(previous.HdrActive);Assert.False(previous.SupersamplingActive);Assert.False(previous.AnisotropicFiltering);
        var enabled=new EnbEnhancedGraphicsOptions(true,true,true,true,true);
        Assert.Equal(enabled,JsonSerializer.Deserialize<EnbEnhancedGraphicsOptions>(JsonSerializer.Serialize(enabled)));
        Assert.False(EnbEnhancedGraphicsOptions.Default.Enabled);
        Assert.False(EnbEnhancedGraphicsOptions.Default.Hdr);
    }
    [Fact]
    public void RetiredEnhancementPreferencesPreserveAcceptedSettings()
    {
        var saved=JsonSerializer.Deserialize<EnbEnhancedGraphicsOptions>("{\"Enabled\":true,\"Bloom\":false,\"Hdr\":true,\"PerPixelLighting\":true,\"Msaa\":true}");
        Assert.Equal(new EnbEnhancedGraphicsOptions(true,false,true),saved);
        Assert.True(saved.HdrActive);Assert.False(saved.BloomActive);
        using var rewritten=JsonDocument.Parse(JsonSerializer.Serialize(saved));
        Assert.False(rewritten.RootElement.TryGetProperty("PerPixelLighting",out _));
        Assert.False(rewritten.RootElement.TryGetProperty("Msaa",out _));
    }
    [Fact]
    public void MasterDisablesBothFeaturesWithoutDiscardingPreferences()
    {
        var retained=new EnbEnhancedGraphicsOptions(false,true,true,true,true);
        Assert.True(retained.Bloom);Assert.True(retained.Hdr);Assert.True(retained.Supersampling);Assert.False(retained.SupersamplingActive);Assert.True(retained.AnisotropicFiltering);Assert.False(retained.AnisotropicFilteringActive);
        Assert.False(retained.BloomActive);Assert.False(retained.HdrActive);
        var hdrOnly=retained with {Enabled=true,Bloom=false};
        Assert.True(hdrOnly.SupersamplingActive);Assert.True(hdrOnly.AnisotropicFilteringActive);Assert.True(hdrOnly.HdrActive);Assert.False(hdrOnly.BloomActive);
    }
}
