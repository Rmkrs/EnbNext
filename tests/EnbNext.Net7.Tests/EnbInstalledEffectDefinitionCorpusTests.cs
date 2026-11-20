using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

[Xunit.Trait("Category", "Heavy")] // constructs every installed effect.ini definition's seek path and radius-sound schedule
public sealed class EnbInstalledEffectDefinitionCorpusTests
{
    [Fact]
    public void EveryInstalledEffectDefinitionParsesItsSeekAndRadiusSoundParameters()
    {
        var assets = EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var effects = (Dictionary<int, EnbEffectDefinition>)typeof(EnbRetailPresentationCatalog)
            .GetField("_effects", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(assets.PresentationCatalog)!;
        var failures = new List<string>();
        int seeks = 0, linears = 0, sounds = 0;
        foreach (var definition in effects.Values)
            try
            {
                foreach (uint duration in new uint[] { 0, 1, 1000, 60000, 1_200_000 })
                {
                    if (EnbRetailSeekEffectConstruction.Spline(definition, duration) is not null) seeks++;
                    if (EnbRetailSeekEffectConstruction.Linear(definition, duration) is not null) linears++;
                }
                if (EnbRetailIniDocument.TryReadFloat(definition.Properties, "TriggerSFXRadius", out float radius) && radius >= 0)
                { _ = new EnbRadiusSoundRuntime(definition, 0); sounds++; }
            }
            catch (Exception ex) { failures.Add($"{definition.DefinitionId}: {ex.GetType().Name} {ex.Message}"); }
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "enbnext-effect-definition-corpus.txt"),
            new[] { $"definitions {effects.Count} seeks {seeks} linears {linears} sounds {sounds}" }.Concat(failures));
        Assert.True(effects.Count > 0);
        Assert.Empty(failures);
    }
}
