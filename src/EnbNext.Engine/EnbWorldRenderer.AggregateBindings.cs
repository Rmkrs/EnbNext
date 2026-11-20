using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    // A retained occurrence has an immutable Root reference. Bind topology once
    // there; replacing a factory creates a new occurrence. Current pose, visibility,
    // enabled attachments and emitter callbacks still execute on every invocation.
    private sealed record AggregateAttachmentBinding(EnbW3dRenderHierarchy? Hierarchy, int[] Pivots);

    private long _aggregateAttachmentBindingPreparations;

    private AggregateAttachmentBinding RetainAggregateAttachmentBinding(LiveRootInstance instance)
    {
        if (instance.AttachmentBinding is { } retained) return retained;
        _aggregateAttachmentBindingPreparations++;
        var root = instance.Root;
        EnbW3dRenderHierarchy? hierarchy = null;
        foreach (var candidate in ConfiguredHierarchyResource(root).Hierarchies)
            if (string.Equals(candidate.Name, root.AttachmentHierarchyName, StringComparison.OrdinalIgnoreCase))
            { hierarchy = candidate; break; }
        var pivots = root.AggregateAttachments.Count == 0 ? Array.Empty<int>() : new int[root.AggregateAttachments.Count];
        if (hierarchy is not null)
            for (int i = 0; i < pivots.Length; i++)
                pivots[i] = EnbCurrentRetailAggregateBoneLookup.Resolve(hierarchy, root.AggregateAttachments[i].BoneName);
        return instance.AttachmentBinding = new(hierarchy, pivots);
    }
}
