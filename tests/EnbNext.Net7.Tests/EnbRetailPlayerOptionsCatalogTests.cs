using EnbNext.Assets;

namespace EnbNext.Net7.Tests;

public sealed class EnbRetailPlayerOptionsCatalogTests
{
    [Fact]
    public void InventorySortRestoresIndependentModesAndUsesPersistedKeysVerbatim()
    {
        var defaults=EnbRetailPlayerOptionsCatalog.ParseText("");
        Assert.Equal(0,defaults.InventoryCargoSort);Assert.Equal(0,defaults.InventoryVaultSort);
        Assert.False(defaults.InventoryCargoReverse||defaults.InventoryVaultReverse);
        Assert.Equal(new EnbRetailInventorySortPreset("category",5,4,1),defaults.InventorySort(0));
        string text=EnbRetailCinematicOptionsWriter.UpdateValues("",[("INT","Inv_Sort_Cargo","2"),("INT","Inv_Sort_Vault","1"),
            ("BOOL","Inv_Sort_Vault_Reverse","yes"),("INT","Inv_Sort1_primary","7"),("INT","Inv_Sort1_tertiary","-1"),("STRING","Inv_Sort1_Name","custom")]);
        var restored=EnbRetailPlayerOptionsCatalog.ParseText(text);
        Assert.Equal(2,restored.InventoryCargoSort);Assert.Equal(1,restored.InventoryVaultSort);
        Assert.False(restored.InventoryCargoReverse);Assert.True(restored.InventoryVaultReverse);
        var preset=restored.InventorySort(restored.InventoryVaultSort);
        Assert.Equal(new EnbRetailInventorySortPreset("custom",7,4,uint.MaxValue),preset);
        Assert.Equal(Convert.FromHexString("11223344000000030000000700000004FFFFFFFF01"),
            EnbNext.Net7.Gameplay.Net7GameplayWire.BuildInventorySort(0x11223344,3,preset.Primary,preset.Secondary,preset.Tertiary,restored.InventoryVaultReverse));
        var raw=EnbRetailPlayerOptionsCatalog.ParseText("[INT-000]\nOptionName=Inv_Sort_Vault\nOptionValue=258\n");
        Assert.Equal(2,raw.InventoryVaultSort);
        Assert.Equal(new EnbRetailInventorySortPreset("",0,0,0),raw.InventorySort(255));
    }
    [Fact]
    public void InventoryFiltersRetainIndependentStringValuesAcrossOptionWrites()
    {
        var defaults=EnbRetailPlayerOptionsCatalog.ParseText("");
        Assert.Equal("All",defaults.InventoryCargoFilter);
        Assert.Equal("All",defaults.InventoryVaultFilter);
        string text=EnbRetailCinematicOptionsWriter.UpdateValues("",
            [("STRING","Inv_Filter_Cargo","Shields"),("STRING","Inv_Filter_Vault","Resources"),("BOOL","Trade_Sort_By_Name","yes")]);
        var initial=EnbRetailPlayerOptionsCatalog.ParseText(text);
        Assert.Equal("Shields",initial.InventoryCargoFilter);
        Assert.Equal("Resources",initial.InventoryVaultFilter);
        text=EnbRetailCinematicOptionsWriter.UpdateValues(text,[("STRING","Inv_Filter_Cargo","Shield")]);
        var restored=EnbRetailPlayerOptionsCatalog.ParseText(text);
        Assert.Equal("Shield",restored.InventoryCargoFilter); // Do not normalize unknown or prefix values to All.
        Assert.Equal("Resources",restored.InventoryVaultFilter);
        Assert.True(restored.TradeSortByName);
    }
    [Fact]
    public void CameraPanelReadsAllRetailEntriesAndCinematicSavePreservesOtherModes()
    {
        string text = string.Join('\n', new[] { "Ship", "Loot", "Land", "Dock", "Bridge" }
            .Select((name, i) => $"[BOOL-{i:000}]\nOptionName=Camera_{name}\nOptionValue=no"));
        var defaults = EnbRetailPlayerOptionsCatalog.ParseText("");
        Assert.True(defaults.CameraShip && defaults.CameraLoot && defaults.CameraLand && defaults.CameraDock && defaults.CameraBridge);
        var options = EnbRetailPlayerOptionsCatalog.ParseText(EnbRetailCinematicOptionsWriter.UpdateValues(text, [("BOOL", "Camera_Warp", "yes"), ("BOOL", "Camera_Gate", "yes")]));
        Assert.True(options.CameraWarp && options.CameraGate);
        Assert.False(options.CameraShip || options.CameraLoot || options.CameraLand || options.CameraDock || options.CameraBridge);
    }
    [Fact]
    public void CameraGateUsesItsRetailBooleanOptionAndDefaultsDisabled()
    {
        Assert.True(EnbRetailPlayerOptionsCatalog.ParseText(
            "[BOOL-013]\nOptionName=Camera_Gate\nOptionValue=yes\n").CameraGate);
        Assert.False(EnbRetailPlayerOptionsCatalog.ParseText(string.Empty).CameraGate);
    }

    [Fact]
    public void ParserReadsBridgeModeAndDoesNotConfuseCameraBridge()
    {
        EnbRetailPlayerOptions enabled = EnbRetailPlayerOptionsCatalog.ParseText("""
            [BOOL-000]
            OptionName=Camera_Bridge
            OptionValue=no

            [BOOL-001]
            OptionName=Bridge_Mode
            OptionValue=yes
            """);

        Assert.True(enabled.BridgeMode);

        EnbRetailPlayerOptions disabled = EnbRetailPlayerOptionsCatalog.ParseText("""
            [BOOL-000]
            OptionName=Camera_Bridge
            OptionValue=yes

            [BOOL-001]
            OptionName=Bridge_Mode
            OptionValue=no
            """);

        Assert.False(disabled.BridgeMode);
        Assert.False(EnbRetailPlayerOptionsCatalog.ParseText("[BOOL-000]\nOptionName=Camera_Bridge\nOptionValue=yes\n").BridgeMode);
    }

    [Fact]
    public void CurrentCompatibilitySelectorMatchesCapturedRetailPlayerOptionsFile()
    {
        // Current-retail evidence: signed-bswap32(fixedSessionId) for this
        // fixture is -1063059136 / unsigned 3231908160. Retail's player-options
        // selector is the retained u64 modulo 1,000,000,000, yielding the
        // captured player231908160_options.ini name.
        const uint fixedSessionId = 0x4001A3C0u;

        Assert.Equal(-1063059136, EnbRetailCameraModeCatalog.GetSessionOverrideSelector(fixedSessionId));
        Assert.Equal(231908160, EnbRetailPlayerOptionsCatalog.GetCurrentCompatibilitySelector(fixedSessionId));
    }

    [Fact]
    public void AssetBundleLoadsPersistedBridgeModeFromCurrentPlayerOptionsPathAndFallsBackToRetailDefault()
    {
        string root = CreateRetailRoot();
        try
        {
            string output = Path.Combine(root, "Data", "client", "output");
            Directory.CreateDirectory(output);

            const uint fixedSessionId = 0x4001A3C0u;
            string persistedPath = Path.Combine(output, "player231908160_options.ini");
            File.WriteAllText(persistedPath, """
                [BOOL-000]
                OptionName=Bridge_Mode
                OptionValue=yes
                """);

            EnbInstalledAssetPaths paths = EnbInstalledAssetLocator.TryResolveRoot(root)
                ?? throw new Xunit.Sdk.XunitException("Synthetic retail root was not resolved.");
            EnbRetailAssetBundle bundle = EnbRetailAssetBundle.Load(paths);

            EnbRetailPlayerOptionsSelection selected = bundle.ResolvePlayerOptions(fixedSessionId);
            Assert.True(selected.Options.BridgeMode);
            Assert.Equal(231908160, selected.Selector);
            Assert.True(selected.UsesPersistedFile);
            EnbRetailDataProvenance provenance = Assert.IsType<EnbRetailDataProvenance>(selected.Provenance);
            Assert.Equal(persistedPath, provenance.Path);

            EnbRetailPlayerOptionsSelection fallback = bundle.ResolvePlayerOptions(0x01000000u);
            Assert.False(fallback.Options.BridgeMode);
            Assert.Equal(1, fallback.Selector);
            Assert.False(fallback.UsesPersistedFile);
            Assert.Null(fallback.Provenance);

            EnbRetailPlayerOptionsSelection beforeSession = bundle.ResolvePlayerOptions(null);
            Assert.False(beforeSession.Options.BridgeMode);
            Assert.Null(beforeSession.Selector);
            Assert.False(beforeSession.UsesPersistedFile);
            Assert.Null(beforeSession.Provenance);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateRetailRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "enbnext-player-options-" + Guid.NewGuid().ToString("N"));
        string ini = Path.Combine(root, "Data", "client", "ini");
        Directory.CreateDirectory(Path.Combine(root, "release"));
        Directory.CreateDirectory(ini);
        Directory.CreateDirectory(Path.Combine(root, "Data", "client", "mixfiles"));
        File.WriteAllBytes(Path.Combine(root, "release", "client.exe"), [0x4D, 0x5A]);
        File.WriteAllText(Path.Combine(ini, "basset.ini"), "[BASE-1]\nNULLOBJECT\n");
        return root;
    }
}
