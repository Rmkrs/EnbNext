using System.Collections;
using System.Reflection;
using EnbNext.Engine;
using EnbNext.Net7.World;
using EnbNext.Presentation;

namespace EnbNext.Net7.Tests;

public sealed class EnbShipLegoPathReplacementTests
{
    [Theory]
    [InlineData(true, "aux")]
    [InlineData(false, "0x00B4")]
    public void TheSourceMutatedLastOwnsASharedBoneNameChain(bool auxLast, string expectedSource)
    {
        // Retail 72B7F0 replaces the child already attached at the same chain.
        var specType = typeof(EnbWorldRenderer).GetNestedType("LiveShipLegoSpec", BindingFlags.NonPublic)!;
        var listType = typeof(List<>).MakeGenericType(specType);
        var specs = (IList)Activator.CreateInstance(listType)!;
        specs.Add(Activator.CreateInstance(specType, "aux", 0, new[] { "A" }, 1, 11, "")!);
        specs.Add(Activator.CreateInstance(specType, "0x00B4", 0, new[] { "A" }, 1, 12, "")!);
        specs.Add(Activator.CreateInstance(specType, "0x00B4", 1, new[] { "B" }, 1, 13, "")!);
        Net7LegoControllerMutation Create(ulong sequence, string source, int ordinal, string bone)
            => new(sequence, Net7LegoControllerMutationKind.Create, source, ordinal, 1, bone, null, 1, 1);
        var b4 = new[] { Create(1, "0x00B4", 0, "A"), Create(2, "0x00B4", 1, "B") };
        var aux = Create(3, "aux", 0, "A");
        var obj = new EnbRenderObjectState
        {
            LegoControllerMutations = auxLast ? [.. b4, aux] : [aux with { Sequence = 1 }, .. b4.Select(m => m with { Sequence = m.Sequence + 1 })],
        };

        typeof(EnbWorldRenderer).GetMethod("ResolveLegoPathReplacement", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [obj, specs]);

        Assert.Equal(2, specs.Count);
        string Source(object spec) => (string)specType.GetProperty("Source")!.GetValue(spec)!;
        string Path(object spec) => (string)specType.GetProperty("CanonicalPath")!.GetValue(spec)!;
        Assert.Equal(expectedSource, Source(specs.Cast<object>().Single(s => Path(s) == "A")));
        Assert.Single(specs.Cast<object>(), s => Path(s) == "B");
    }
}
