using EnbNext.Presentation;
using System.Text.Json;
using EnbNext.App;

namespace EnbNext.Net7.Tests;

public sealed class EnbDisplayPreferencesTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(1d)]
    [InlineData(1.25d)]
    [InlineData(1.5d)]
    [InlineData(1.75d)]
    [InlineData(2d)]
    public void UiScaleIsAnAbsolutePreferencePreservedByPlacementAndCharacterRoundTrip(double? scale)
    {
        Assert.Null(JsonSerializer.Deserialize<EnbStartupPreferences>("{}")!.UiScale);
        var system=new EnbStartupPreferences(UiScale:scale);
        var moved=system.WithNormalPlacement(50,60,1280,720,1920,1080)
            .WithDisplayChoice(new(1920,1080,EnbDisplayMode.Windowed));
        var saved=JsonSerializer.Deserialize<EnbStartupPreferences>(JsonSerializer.Serialize(moved))!;
        Assert.Equal(scale,saved.UiScale);
        var character=EnbCharacterDisplayPreferences.From(saved,true);
        var restored=JsonSerializer.Deserialize<EnbCharacterDisplayPreferences>(JsonSerializer.Serialize(character))!;
        Assert.Equal(scale,restored.ApplyTo(new(UiScale:1.75)).UiScale);
        Assert.Null(new EnbCharacterDisplayPreferences().ApplyTo(system).UiScale);
    }

    [Fact]
    public void IntroPreferenceDefaultsOnAndSurvivesPlacementAndDisplayChanges()
    {
        var legacy = JsonSerializer.Deserialize<EnbStartupPreferences>("{\"Server\":\"test\",\"DisplayWidth\":1024}")!;
        Assert.True(legacy.PlayStartupIntro);
        Assert.Null(legacy.SoundSettingsOwner);
        var disabled = (legacy with { PlayStartupIntro = false, SoundSettingsOwner = 0x4001A3CA })
            .WithNormalPlacement(10,20,800,600,800,600)
            .WithDisplayChoice(new(1920,1080,EnbDisplayMode.BorderlessFullscreen));
        var saved = JsonSerializer.Deserialize<EnbStartupPreferences>(JsonSerializer.Serialize(disabled))!;
        Assert.False(saved.PlayStartupIntro);
        Assert.Equal(0x4001A3CAu, saved.SoundSettingsOwner);
        Assert.True(saved.FullScreen);
    }

    [Fact]
    public void ResolutionButtonUsesCompactLabelsButRetainsExplicitModeNames()
    {
        var windowed = new EnbDisplayChoice(1024, 768, EnbDisplayMode.Windowed);
        var borderless = new EnbDisplayChoice(3840, 2160, EnbDisplayMode.BorderlessFullscreen);

        Assert.Equal("1024 x 768", windowed.ButtonLabel);
        Assert.Equal("Windowed 1024 x 768", windowed.Label);
        Assert.Equal("Borderless", borderless.ButtonLabel);
        Assert.Equal("Borderless fullscreen", borderless.Label);
    }

    [Fact]
    public void MaximizingDoesNotChangeTheSelectedWindowedResolution()
    {
        var selected = new EnbDisplayChoice(1600, 900, EnbDisplayMode.Windowed);
        var settings = new EnbStartupPreferences().WithDisplayChoice(selected)
            .WithNormalPlacement(100, 200, 1632, 939, 1600, 900);

        // The OS can maximize the same window without selecting a new mode or
        // replacing its last normal client size with the maximized client area.
        settings = settings with { Maximized = true };
        var reloaded = JsonSerializer.Deserialize<EnbStartupPreferences>(JsonSerializer.Serialize(settings))!;

        Assert.True(reloaded.Maximized);
        Assert.Equal(selected, reloaded.SelectedDisplay(3840, 2160));
        Assert.False(reloaded.WouldChangeDisplay(selected, 3840, 2160));
        Assert.Equal(1600, reloaded.DisplayWidth);
        Assert.Equal(900, reloaded.DisplayHeight);
    }

    [Fact]
    public void BorderlessModeKeepsTheWindowedSizeForReturningToWindowedMode()
    {
        var windowed = new EnbDisplayChoice(1600, 900, EnbDisplayMode.Windowed);
        var borderless = new EnbDisplayChoice(3840, 2160, EnbDisplayMode.BorderlessFullscreen);
        var settings = new EnbStartupPreferences().WithDisplayChoice(windowed)
            .WithNormalPlacement(100, 200, 1632, 939, 1600, 900) with { Maximized = true };

        settings = settings.WithDisplayChoice(borderless);
        Assert.Equal(borderless, settings.SelectedDisplay(3840, 2160));
        Assert.True(settings.Maximized);
        Assert.Equal(1600, settings.DisplayWidth);
        Assert.Equal(900, settings.DisplayHeight);

        settings = settings.WithDisplayChoice(windowed);
        Assert.Equal(windowed, settings.SelectedDisplay(3840, 2160));
        Assert.False(settings.Maximized);
    }

    [Fact]
    public void ResizingAStableNormalWindowUpdatesItsWindowedResolution()
    {
        var settings = new EnbStartupPreferences()
            .WithNormalPlacement(50, 60, 1320, 760, 1280, 720)
            .WithNormalPlacement(70, 80, 1640, 940, 1600, 900);

        Assert.Equal(new EnbDisplayChoice(1600, 900, EnbDisplayMode.Windowed),
            settings.SelectedDisplay(3840, 2160));
        Assert.Equal(70, settings.X);
        Assert.Equal(80, settings.Y);
    }

    [Fact]
    public void CharacterDisplayProfileRetainsItsOwnResolutionAndMonitorPlacement()
    {
        var system = new EnbStartupPreferences(X: 40, Y: 60, DisplayWidth: 1280, DisplayHeight: 720);
        var character = system.WithDisplayChoice(new(1600, 900, EnbDisplayMode.Windowed))
            .WithNormalPlacement(2500, 150, 1600, 900, 1600, 900);
        var persisted = EnbCharacterDisplayPreferences.From(character, enabled: true);
        var reloaded = JsonSerializer.Deserialize<EnbCharacterDisplayPreferences>(JsonSerializer.Serialize(persisted))!;
        var active = reloaded.ApplyTo(system);

        Assert.True(reloaded.Enabled);
        Assert.Equal(new EnbDisplayChoice(1600, 900, EnbDisplayMode.Windowed), active.SelectedDisplay(3840, 2160));
        Assert.Equal((2500, 150), (active.X, active.Y));
        Assert.Equal(new EnbDisplayChoice(1280, 720, EnbDisplayMode.Windowed), system.SelectedDisplay(3840, 2160));
        Assert.Equal((40, 60), (system.X, system.Y));
    }

    [Fact]
    public void DisabledCharacterProfileInheritsSystemDefault()
    {
        var system = new EnbStartupPreferences(DisplayWidth: 1920, DisplayHeight: 1080);
        var oldOverride = EnbCharacterDisplayPreferences.From(
            system.WithDisplayChoice(new(1024, 768, EnbDisplayMode.Windowed)), enabled: false);

        Assert.False(oldOverride.Enabled);
        Assert.Equal(1024, oldOverride.DisplayWidth);
        Assert.Equal(768, oldOverride.DisplayHeight);
        Assert.Equal(new EnbDisplayChoice(1024, 768, EnbDisplayMode.Windowed),
            oldOverride.ApplyTo(system).SelectedDisplay(3840, 2160));
        Assert.Equal(new EnbDisplayChoice(1920, 1080, EnbDisplayMode.Windowed),
            system.SelectedDisplay(3840, 2160));
    }

    [Fact]
    public void DifferentAvatarsAndAccountsHaveSeparateLocalDisplayProfiles()
    {
        string startup = Path.Combine(Path.GetTempPath(), "enb-display-tests", "startup.json");
        var first = EnbCharacterDisplayPreferences.PathFor(startup, 7, 42);
        var second = EnbCharacterDisplayPreferences.PathFor(startup, 7, 43);
        var otherAccount = EnbCharacterDisplayPreferences.PathFor(startup, 8, 42);

        Assert.NotEqual(first, second);
        Assert.NotEqual(first, otherAccount);
        Assert.Equal(Path.GetDirectoryName(startup), Path.GetDirectoryName(Path.GetDirectoryName(first)));

        var profiles = new Dictionary<string, string>
        {
            [first] = JsonSerializer.Serialize(new EnbCharacterDisplayPreferences(true, DisplayWidth: 1024, DisplayHeight: 768)),
            [second] = JsonSerializer.Serialize(new EnbCharacterDisplayPreferences(true, DisplayWidth: 1600, DisplayHeight: 900)),
            [otherAccount] = JsonSerializer.Serialize(new EnbCharacterDisplayPreferences(true, DisplayWidth: 2560, DisplayHeight: 1440)),
        };
        Assert.Equal(new[] { 1024, 1600, 2560 }, new[] { first, second, otherAccount }
            .Select(path => JsonSerializer.Deserialize<EnbCharacterDisplayPreferences>(profiles[path])!.DisplayWidth));
    }
}
