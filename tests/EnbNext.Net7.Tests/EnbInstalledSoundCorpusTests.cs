using EnbNext.Assets;
using EnbNext.Engine;

namespace EnbNext.Net7.Tests;

[Xunit.Trait("Category", "Heavy")] // decodes every installed sounds.ini definition
public sealed class EnbInstalledSoundCorpusTests
{
    [Fact]
    public void EveryInstalledSoundDefinitionDecodesOrIsAbsentMedia()
    {
        var assets = EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var sounds = assets.Sounds!;
        var failures = new List<string>();
        // Callers decode one already selected weighted file at a time; SoundType 0
        // (station room music/ambience) is only played through the ambient path.
        foreach (var definition in sounds.Definitions)
            foreach (var file in definition.Files.Where(f => !f.IsNullChoice))
            {
                var decoded = LiveWorldOpenAlAudio.DecodeSoundDefinition(definition with { Files = [file] }, assets,
                    ambient: definition.EffectiveSoundType == 0);
                if (decoded.Error is not null) failures.Add(definition.Name + "/" + file.LogicalResourceName + ": " + decoded.Error);
            }
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "enbnext-sound-corpus-failures.txt"), failures);
        // Duplicate MIX names resolve to the first enumerated package (004E5440).
        Assert.Empty(failures);
    }

    [Fact]
    public void EveryInstalledEngineLoopIsAContinuousMonoVoice()
    {
        // UpdateEngineThrustEffects 006FDBD0 selects engine_loop_<trail+1> or
        // npc_engine_loop_<base%6>; every installed one must pass the engine path.
        var assets = EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var names = assets.Sounds!.Definitions.Select(d => d.Name)
            .Where(n => n.StartsWith("engine_loop_", StringComparison.OrdinalIgnoreCase) ||
                        n.StartsWith("npc_engine_loop_", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.NotEmpty(names);
        var failures = names.Select(n => (n, d: LiveWorldOpenAlAudio.DecodeEffectSound(n, assets, engineLoop: true)))
            .Where(x => x.d.Error is not null || !x.d.MissingMedia && x.d.Pcm!.Channels != 1)
            .Select(x => x.n + ": " + (x.d.Error ?? $"{x.d.Pcm!.Channels} channels")).ToArray();
        Assert.Empty(failures);
    }

    [Fact]
    public void EveryInstalledSoundDefinitionHasAPositiveChoiceWeight()
    {
        // Weighted selection (station ambient, effect sounds) draws from the positive weights; unweighted files count 1.
        var assets = EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var zero = assets.Sounds!.Definitions.Where(d => d.Files.Sum(f => Math.Max(0, f.Weight ?? 1)) <= 0).Select(d => d.Name).ToArray();
        Assert.Empty(zero);
    }
}
