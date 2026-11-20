using System.Collections.Immutable;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private readonly Dictionary<string, HashSet<object>> _gadgetFlashes = [];
    private long _gadgetFlashSequence, _bridgeGadgetSequence;

    // 006603A0 emits FlashingGadgetEvent when the hover clone is installed;
    // 0053CCD0 requests use_controls only while that clone exists under the pointer.
    // Observe published visual transitions, not clicks, keys or repeated GPU draws.
    private void ObserveGadgetFlashes(string source, IEnumerable<object> flashes)
    {
        var next = flashes.ToHashSet();
        lock (_gadgetFlashes)
        {
            _gadgetFlashes.TryGetValue(source, out var previous);
            _gadgetFlashes[source] = next;
            foreach (var flash in next)
                if (previous is null || !previous.Contains(flash))
                    Interlocked.Increment(ref (MapWorldRenderer ?? this)._gadgetFlashSequence);
        }
    }

    private void ObserveVerbFlashes(string source, ImmutableList<EnbUiModelDraw> values)
        => ObserveGadgetFlashes(source, values.Where(v => v.Hovered && !v.SuppressHoverEvent &&
            EnbCurrentRetailVerbPresentation.IsEnabled(v.Verb) && v.Asset.Resource.Meshes.Count != 0)
            .Select(v => (object)(v.Asset, v.Verb >> 16, v.GadgetOwner??(object)v.Bounds)));

    private static IEnumerable<object> CockpitFlashes(EnbCockpitDraw? p)
    {
        if (p is null) yield break;
        if (p.Shortcuts is {} shortcuts)
        {
            if (p.Hovered == "BANK_0" && shortcuts.LeftEnabled || p.Hovered == "BANK_1" && shortcuts.RightEnabled)
                yield return (shortcuts.Asset, p.Hovered);
            for (int i = 0; i < shortcuts.Icons.Length; i++)
                if (shortcuts.Icons[i] is {Highlight:{} texture} icon)
                    //6609A0 keeps a clone only for the same source/first texture.
                    //Use the untinted icon, not a status-colour raster or list index.
                    yield return (icon.GadgetOwner??(object)(shortcuts.Asset,i),texture);
        }
        if (p.Buffs is {} buffs)
            foreach (var icon in buffs.Icons)
                if (icon.Hovered) yield return (icon.Asset, icon.GadgetOwner??(object)icon.Slot);
    }
}
