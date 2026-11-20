using System.Collections.Immutable;
using System.Text;
using EnbNext.Assets;
using EnbNext.Net7.Gameplay;

namespace EnbNext.Presentation;

/// <summary>Detached server state. Visibility is evaluated against the live clock, not snapshot arrival.</summary>
public sealed record EnbSceneBuffSnapshot(int Slot, string Type, string ScrubTypeName, bool IsPermanent,
    long RemovalTime, ImmutableArray<Net7BuffElementPresentation> Elements)
{
    public static EnbSceneBuffSnapshot From(Net7BuffPresentation source) => new(source.SlotIndex,
        source.BuffType, source.ScrubTypeName, source.IsPermanent, source.RemovalTime, source.Elements.ToImmutableArray());

    public bool IsVisible(long currentClientTime) => IsPermanent || (RemovalTime > 0 && RemovalTime >= currentClientTime);

    // Current client 00666570. Source ordering comes from Net7PlayerSemanticDomain.
    // Magnitude/expiration influence ordering, but are not printed in this tooltip.
    public string Tooltip(EnbRetailBuffDefinition definition)
    {
        var text = new StringBuilder(definition.Name);
        string scrub = ScrubTypeName;
        if (scrub.Length != 0) text.Append(text.Length == 0 ? "(" : "\n(").Append(scrub).Append(')');
        foreach (var element in Elements)
            text.Append("\n ").Append(element.IsActive ? '+' : ' ')
                .Append(element.SourceEntity.Length == 0 ? "(unknown)" : element.SourceEntity)
                .Append(": ").Append(element.SourceObject);
        return text.ToString();
    }
}
