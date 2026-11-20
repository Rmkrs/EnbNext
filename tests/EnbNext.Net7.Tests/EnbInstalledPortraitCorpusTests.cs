using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

[Xunit.Trait("Category", "Heavy")] // loads every installed avatar portrait component through the production portrait loader
public sealed class EnbInstalledPortraitCorpusTests
{
    [Fact]
    public void EveryInstalledPortraitComponentLoads()
    {
        var assets = EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var mix = assets.RetailMixResources!;
        bool Exists(string name) => mix.TryReadResource(name, out _, out _);
        var lines = new List<string>();
        var failures = new List<string>();
        int loads = 0;
        void Try(string what, EnbSceneAvatarSnapshot avatar)
        {
            try { EnbAvatarPortraitAsset.Load(assets, avatar); loads++; }
            catch (Exception ex) { failures.Add($"{what}: {ex.GetType().Name}: {ex.Message} @ {ex.StackTrace?.Split('\n').FirstOrDefault(l => l.Contains("EnbNext."))?.Trim()}"); }
        }
        var narrator = EnbRetailAvatarDefaults.Narrator(assets);
        Try("narrator", narrator);
        Try("help", EnbRetailAvatarDefaults.Help(assets));
        var personalities = Enumerable.Range(0, 100).Where(p => File.Exists(Path.Combine(assets.Paths.ClientDataDirectory, "personalities", $"Personality{p:00}.ini"))).ToArray();
        lines.Add("personalities " + string.Join(",", personalities));
        for (uint gender = 0; gender < 2; gender++)
        for (uint race = 0; race < 3; race++)
        {
            string prefix = (gender == 0 ? "AM" : "AF") + new[] { "T", "J", "P" }[race];
            var bodies = new List<(byte Body, byte Pants)>();
            for (int b = 0; b < 100; b++) for (int p = 0; p < 100; p++) if (Exists($"{prefix}BODY{b:00}{p:00}.w3d")) bodies.Add(((byte)b, (byte)p));
            var heads = Enumerable.Range(0, 100).Where(h => Exists($"{prefix}HEAD{h:00}.w3d")).Select(h => (byte)h).ToArray();
            lines.Add($"{prefix} bodies {bodies.Count} heads {heads.Length}");
            if (bodies.Count == 0 || heads.Length == 0) { failures.Add($"{prefix}: no installed body or head"); continue; }
            var baseAvatar = narrator with { AvatarType = 0, Gender = gender, Race = race, BodyType = bodies[0].Body, PantsType = bodies[0].Pants,
                HeadType = heads[0], HairNum = 0, EarNum = 0, GoggleNum = 0, BeardNum = 0, Skeleton = "", Personality = (byte)personalities[0] };
            foreach (var (body, pants) in bodies) Try($"{prefix} body {body:00}{pants:00}", baseAvatar with { BodyType = body, PantsType = pants });
            foreach (byte head in heads) Try($"{prefix} head {head:00}", baseAvatar with { HeadType = head });
            foreach (var kind in new[] { "HAIR", "EAR", "GOGGLE", "BEARD" })
            {
                var numbers = Enumerable.Range(1, 99).Where(n => Exists($"{prefix}{kind}{n:00}.w3d")).Select(n => (byte)n).ToArray();
                lines.Add($"{prefix} {kind} {numbers.Length}");
                foreach (byte n in numbers)
                    Try($"{prefix} {kind} {n:00}", kind switch
                    {
                        "HAIR" => baseAvatar with { HairNum = n }, "EAR" => baseAvatar with { EarNum = n },
                        "GOGGLE" => baseAvatar with { GoggleNum = n }, _ => baseAvatar with { BeardNum = n },
                    });
            }
            foreach (int p in personalities) Try($"{prefix} personality {p:00}", baseAvatar with { Personality = (byte)p });
            // 0077B870 asserts but keeps out-of-range weights and normalizes only a positive sum.
            uint two = BitConverter.SingleToUInt32Bits(2f), half = BitConverter.SingleToUInt32Bits(.5f);
            Try($"{prefix} weights above one", baseAvatar with { BodyWeight = [two, half, 0, 0, 0], HeadWeight = [two, 0, 0, 0, half] });
        }
        lines.Add("loads " + loads);
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "enbnext-portrait-corpus.txt"), [.. lines, "failures:", .. failures]);
        // Open: Personality06 names ABANIM06-20 for breathe1, which is not installed.
        // Retail 7831C0 leaves the gesture slot empty and logs "Cannot Find Gesture
        // Animation"; the portrait loader does not yet model a clip-less actor.
        Assert.All(failures, f => Assert.Contains("personality 06: InvalidDataException: Portrait asset unavailable: ABANIM06-20.w3d", f));
        Assert.Equal(6, failures.Count);
    }
}
