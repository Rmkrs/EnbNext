using System.Numerics;
using System.Reflection;
using EnbNext.Assets;
using EnbNext.Engine;

namespace EnbNext.Net7.Tests;

[Xunit.Trait("Category", "Heavy")] // assembles every installed character-creation ship and class preview through the production frontend
public sealed class EnbInstalledCreationShipCorpusTests
{
    [Fact]
    public void EveryInstalledCreationShipAssembles()
    {
        var assets = EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
        try
        {
            var controller = new EnbFrontendController(assets);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var type = typeof(EnbFrontendController);
            var ship = type.GetMethod("AddCreationRetailShipParts", flags)!;
            var sphere = type.GetMethod("CreationRetailShipSphere", flags)!;
            var aggregate = type.GetMethod("AddCreationAggregateParts", flags)!;
            var mount = type.GetMethod("CreationClassMount", BindingFlags.Static | BindingFlags.NonPublic)!;
            var listType = ship.GetParameters()[4].ParameterType;
            bool Exists(string name) => assets.RetailMixResources!.TryReadResource(name + ".w3d", out _, out _);
            var lines = new List<string>();
            var failures = new List<string>();
            int ships = 0;
            for (uint race = 0; race < 3; race++)
            for (uint profession = 0; profession < 3; profession++)
            {
                string classObject = ((string)mount.Invoke(null, [race, profession])!).ToLowerInvariant() + "03";
                try { aggregate.Invoke(controller, [classObject, Matrix4x4.Identity, 0, Activator.CreateInstance(listType), new HashSet<string>()]); }
                catch (TargetInvocationException ex) { failures.Add($"class {classObject}: {ex.InnerException!.GetType().Name}: {ex.InnerException.Message}"); }
                char code = "TJP"[(int)race];
                var hulls = Enumerable.Range(0, 10).Where(h => Exists($"S{code}H{h + 1}1")).ToArray();
                var wings = Enumerable.Range(0, 10).Where(w => Exists($"S{code}W{w + 1}1")).ToArray();
                if (profession == 0) lines.Add($"race {race}: hulls {hulls.Length}, wings {wings.Length}");
                foreach (int hull in hulls)
                foreach (int wing in wings)
                {
                    try
                    {
                        var parts = Activator.CreateInstance(listType);
                        ship.Invoke(controller, [race, profession, hull, wing, parts]);
                        sphere.Invoke(controller, [race, profession, hull, wing]);
                        ships++;
                    }
                    catch (TargetInvocationException ex) { failures.Add($"ship {race}/{profession}/{hull}/{wing}: {ex.InnerException!.GetType().Name}: {ex.InnerException.Message}"); }
                }
            }
            // Frontend preparation steps (models, narrator/class animations, beams).
            var steps = (IEnumerable<Action>)type.GetMethod("CreationProfessionPreparationSteps", flags)!.Invoke(controller, null)!;
            int stepCount = 0;
            foreach (var step in steps)
            {
                stepCount++;
                try { step(); }
                catch (Exception ex) { failures.Add($"preparation step {stepCount}: {ex.GetType().Name}: {ex.Message}"); }
            }
            foreach (string model in new[] { "fe_room", "fe12a", "fe01c", "fe01a", "fe02a" })
                try { type.GetMethod("Model", flags)!.Invoke(controller, [model]); }
                catch (TargetInvocationException ex) { failures.Add($"model {model}: {ex.InnerException!.GetType().Name}: {ex.InnerException.Message}"); }
            lines.Add($"preparation steps {stepCount}");
            lines.Add($"assembled ships {ships}");
            File.WriteAllLines(Path.Combine(Path.GetTempPath(), "enbnext-creation-ship-corpus.txt"), [.. lines, "failures:", .. failures]);
            Assert.Empty(failures);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
}
