using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

[Xunit.Trait("Category", "Heavy")] // poses every frontend clip and bridge animation, and checks every personality's speech actions
public sealed class EnbInstalledPresentationPoseCorpusTests
{
    private static readonly string[] FrontendModels =
        ["bone_hov01a", "comp00a", "fe01a", "fe01c", "fe02a", "fe12a", "fe_class00", "fe_class01", "fe_narrator", "fe_room", "trade00a", "tsi14a"];

    [Fact]
    public void EveryFrontendClipBridgeAnimationAndPersonalityIsUsable()
    {
        var assets = EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var session = EnbRenderSession.Of(assets);
        var failures = new List<string>();
        void Run(Action action, string name)
        {
            try { action(); }
            catch (Exception ex) { if (failures.Count < 200) failures.Add(name + ": " + ex.GetType().Name + " " + ex.Message); }
        }
        // EnbFrontendModel.PoseFrame for every "<model>-N" clip and frame.
        int clips = 0;
        foreach (string name in FrontendModels)
        {
            EnbFrontendModel? model = null;
            Run(() => model = session.Frontend.Model(name), "model " + name);
            if (model is null) continue;
            Run(() => model.PoseFrame(null, 0), name + " rest");
            for (int n = 1; n <= 40; n++)
            {
                if (!model.TryAnimation($"{name}-{n}", out var clip) || clip is null) continue;
                clips++;
                for (float frame = 0; frame <= clip.NumFrames; frame += .5f)
                {
                    float f = frame;
                    Run(() => model.PoseFrame(clip, f), $"{name}-{n} frame {f}");
                }
            }
        }
        // 0053B7A0: an unknown race asserts and selects the Terran bridge.
        foreach (int race in new[] { 3, -1, int.MaxValue })
            if (EnbRetailBridgeResource.Load(assets, race).Name != "t_i_bridge") failures.Add("bridge race " + race);
        // Bridge resources for every playable race: every attachment at every animation frame.
        for (int race = 0; race < 3; race++)
        {
            EnbRetailBridgeResource? bridge = null;
            int r = race;
            Run(() => bridge = EnbRetailBridgeResource.Load(assets, r), "bridge " + r);
            if (bridge is null) continue;
            var pivots = bridge.Resource.Hierarchies.Single().Pivots.Select(p => p.Name).ToArray();
            foreach (string pivot in pivots) Run(() => bridge.AuthoredTransform(pivot), $"bridge {r} {pivot}");
            foreach (var animation in bridge.Resource.Animations)
                for (float frame = 0; frame <= animation.NumFrames; frame += 1)
                {
                    var sample = new EnbW3dAnimationSample(animation.Name, frame);
                    Run(() => bridge.AuthoredTransform(pivots[0], sample), $"bridge {r} {animation.Name} {frame}");
                }
        }
        // Every installed personality produces speech records; a personality lacking a talk/idle_talk tag that
        // 0078EEF0 selects stops under the safety rule (retail reads NULL+4), which is reported, not failed.
        int personalities = 0;
        List<string> gaps = [];
        string[] sentences = ["Hi.", "Hello there, pilot.", "Welcome to the station, we have a great many things to discuss today about the long war!"];
        foreach (string file in Directory.EnumerateFiles(Path.Combine(assets.Paths.ClientDataDirectory, "personalities"), "Personality*.ini"))
        {
            personalities++;
            var speech = new EnbRetailAvatarSpeech(EnbRetailIniDocument.Load(file), EnbRetailIniDocument.Parse(""));
            var actions = (IReadOnlyDictionary<string, int>)typeof(EnbRetailAvatarSpeech)
                .GetField("actions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(speech)!;
            foreach (string prefix in new[] { "talk", "idle_talk" })
                for (int level = 1; level <= 5; level++)
                    if (!actions.ContainsKey(prefix + level)) gaps.Add($"{Path.GetFileName(file)} lacks {prefix}{level}");
            foreach (string sentence in sentences)
                foreach (int roll in new[] { 0, 10 })
                {
                    Run(() => speech.Produce(sentence, (_, _) => roll), $"{Path.GetFileName(file)} speech {roll} {sentence}");
                    Run(() => speech.Parse(sentence, (_, _) => roll, true), $"{Path.GetFileName(file)} commands {roll} {sentence}");
                }
        }
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "enbnext-presentation-pose-corpus.txt"),
            new[] { $"clips {clips} personalities {personalities}" }.Concat(gaps).Concat(failures));
        Assert.True(clips > 0 && personalities > 0);
        Assert.Empty(failures);
    }
}
