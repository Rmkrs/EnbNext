using EnbNext.Assets;

namespace EnbNext.Net7.Tests;

public sealed class EnbR6Z24CurrentRetailRslSoundDefinitionTests
{
    [Fact]
    public void SoundCatalogResolvesDirectFilenameAndAuthoredDefaultsWithoutCompiledSoundValues()
    {
        EnbRetailSoundCatalog catalog = EnbRetailSoundCatalog.ParseText(
            """
            [SOUND_DEFAULTS]
            Default_DropOffRadius=1200
            Default_MaxVolRadius=400
            Default_Priority=0.9
            Default_Volume=0.8
            Default_MaxInstances=4

            [Gate Active]
            SoundFilename=gate_active.mp3
            SoundLoopCount=3
            SoundType=2
            SoundVolume=0.65
            """);

        Assert.True(catalog.TryGetDefinition("gate active", out EnbRetailSoundDefinition sound));
        EnbRetailSoundFileChoice file = Assert.Single(sound.Files);
        Assert.Equal("gate_active.mp3", file.LogicalResourceName);
        Assert.Null(file.Weight);
        Assert.False(file.IsNullChoice);
        Assert.Equal(3, sound.LoopCount);
        Assert.Equal(1200f, sound.DropOffRadius);
        Assert.Equal(400f, sound.MaxVolumeRadius);
        Assert.Equal(0.9f, sound.Priority);
        Assert.Equal(0.65f, sound.Volume);
        Assert.Equal(2, sound.SoundType);
        Assert.Equal(4, sound.MaxInstances);
        Assert.Null(sound.Pitch);
    }

    [Fact]
    public void SoundCatalogKeepsWeightedFilesAndNullChoiceAsAuthored()
    {
        EnbRetailSoundCatalog catalog = EnbRetailSoundCatalog.ParseText(
            """
            [Random Gate]
            SoundLoopCount=1

            [Random Gate\Files]
            a.mp3=3
            NULL=2
            ignored.mp3=0
            """);

        Assert.Equal(1, catalog.Count);
        Assert.True(catalog.TryGetDefinition("RANDOM GATE", out EnbRetailSoundDefinition sound));
        Assert.Collection(
            sound.Files,
            file =>
            {
                Assert.Equal("a.mp3", file.LogicalResourceName);
                Assert.Equal(3, file.Weight);
                Assert.False(file.IsNullChoice);
            },
            file =>
            {
                Assert.Null(file.LogicalResourceName);
                Assert.Equal(2, file.Weight);
                Assert.True(file.IsNullChoice);
            });
    }

    [Fact]
    public void AssetBundleLoadsSoundsIniThroughCurrentRetailLooseFilePrecedence()
    {
        string root = Path.Combine(Path.GetTempPath(), $"enbnext-r6z24-sounds-{Guid.NewGuid():N}");
        try
        {
            string release = Path.Combine(root, "release");
            string ini = Path.Combine(root, "Data", "client", "ini");
            Directory.CreateDirectory(release);
            Directory.CreateDirectory(ini);
            Directory.CreateDirectory(Path.Combine(root, "Data", "client", "mixfiles"));
            File.WriteAllBytes(Path.Combine(release, "client.exe"), [0]);
            File.WriteAllText(Path.Combine(ini, "basset.ini"), "[BASE-0]\n");
            File.WriteAllText(Path.Combine(ini, "sounds.ini"), "[Wrong]\nSoundFilename=wrong.mp3\n");
            string winningSounds = Path.Combine(release, "sounds.ini");
            File.WriteAllText(winningSounds, "[Winning]\nSoundFilename=winning.mp3\n");

            EnbInstalledAssetPaths paths = EnbInstalledAssetLocator.TryResolveRoot(root)
                ?? throw new Xunit.Sdk.XunitException("Synthetic selected install did not resolve.");
            EnbRetailAssetBundle bundle = EnbRetailAssetBundle.Load(paths);

            EnbRetailSoundCatalog sounds = Assert.IsType<EnbRetailSoundCatalog>(bundle.Sounds);
            Assert.True(sounds.TryGetDefinition("Winning", out _));
            Assert.False(sounds.TryGetDefinition("Wrong", out _));
            Assert.True(bundle.TryGetSource(EnbRetailAssetSourceKind.SoundsIni, out EnbRetailAssetSource source));
            Assert.Equal(winningSounds, source.Path);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SoundResourceResolutionUsesSelectedInstallLoosePathBeforeMixFallback()
    {
        string root = Path.Combine(Path.GetTempPath(), $"enbnext-r6z24-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "release"));
            Directory.CreateDirectory(Path.Combine(root, "Data", "client", "ini"));
            Directory.CreateDirectory(Path.Combine(root, "Data", "client", "mixfiles"));
            Directory.CreateDirectory(Path.Combine(root, "Data", "client", "sound"));
            File.WriteAllBytes(Path.Combine(root, "release", "client.exe"), [0]);
            File.WriteAllText(Path.Combine(root, "Data", "client", "ini", "basset.ini"), "[BASE-0]\n");
            string soundPath = Path.Combine(root, "Data", "client", "sound", "gate_active.mp3");
            File.WriteAllBytes(soundPath, [1, 2, 3]);

            EnbInstalledAssetPaths paths = EnbInstalledAssetLocator.TryResolveRoot(root)
                ?? throw new Xunit.Sdk.XunitException("Synthetic selected install did not resolve.");
            EnbRetailSoundCatalog catalog = EnbRetailSoundCatalog.ParseText(
                """
                [Gate Active]
                SoundFilename=gate_active.mp3
                """);
            Assert.True(catalog.TryGetDefinition("Gate Active", out EnbRetailSoundDefinition sound));

            EnbRetailSoundResolution resolution = catalog.Resolve(sound, paths, retailMixResources: null);

            EnbRetailSoundResolvedChoice choice = Assert.Single(resolution.Choices);
            Assert.Equal(EnbRetailSoundResourceStatus.LooseFile, choice.Status);
            Assert.Equal(soundPath, choice.LoosePath);
            Assert.True(resolution.IsComplete);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
