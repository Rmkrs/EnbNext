using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

[Xunit.Trait("Category", "Heavy")] // reads the installed frontend roster model and dialogbox.ini
public sealed class EnbInstalledFrontendPromptCorpusTests
{
    [Fact]
    public void InstalledRosterCameraAnimationsAndPetitionPromptsExist()
    {
        var paths = EnbInstalledAssetLocator.TryLocate()!;
        var assets = EnbRetailAssetBundle.Load(paths);
        // EnbFrontendController.StartOccupiedCameraAnimation asks fe01c for fe01c-<slot+5>.
        var roster = new EnbFrontendModel(assets, "fe01c");
        for (int slot = 0; slot < 5; slot++)
            Assert.True(roster.TryAnimation($"fe01c-{slot + 5}", out var clip) && clip is not null, $"fe01c-{slot + 5}");
        // EnbFrontendController.ApplyLoginAnimation throws only when fe01a has no login animation.
        var login = new EnbFrontendModel(assets, "fe01a");
        foreach (string name in new[] { "fe01a-1", "fe01a-2" })
            Assert.True(login.TryAnimation(name, out var clip) && clip is not null, name);
        // EnbFrontendController.ChangeNarrator: the narrator idle (EnbRetailAvatarIdle.Next,
        // posture 0, never stand-still) asks only bored/bored1..4/breathe1..4, and the frontend
        // asks walk_on/walk_off. The installed narrator (personality 07) lacks only breathe2
        // (ABANIM07-20), so the unavailable-clip branch is reached in normal frontend idling.
        var narrator = EnbRetailAvatarDefaults.Narrator(assets);
        var missing = new List<string>();
        foreach (string tag in new[] { "bored", "bored1", "bored2", "bored3", "bored4", "breathe1", "breathe2", "breathe3", "breathe4", "walk_on", "walk_off" })
        {
            try { EnbAvatarPortraitAsset.Load(assets, narrator, tag); }
            catch (AvatarAnimationUnavailableException) { missing.Add(tag); }
        }
        Assert.Equal(new[] { "breathe2" }, missing);
        // EnbInformationUi.ShowPrompt sections.
        var ini = EnbRetailIniDocument.Load(Path.Combine(paths.ClientIniDirectory, "dialogbox.ini"));
        foreach (string section in new[] { "PETITION_ENTRY_CANCEL", "PETITION_ENTRY_NO_DATA", "PETITION_PLAYER_ENTRY_NO_DATA" })
            Assert.True(ini.TryGetSection(section, out _), section);
    }
}
