using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>
/// Current-retail static-sector presentation rules that sit above the generic
/// BaseAsset/W3D/RSL renderer. There is only a small set of family-specific
/// rules; this slice owns the type-0x26 Harvestable PercentFull selector while
/// leaving geometry/material rendering data-driven through the common pipeline.
/// </summary>
public static class EnbCurrentRetailStaticSectorObjectRuntime
{
    public const byte HarvestableObjectType = 0x26;

    public static int ResolveHarvestableRenderState(float percentFull)
    {
        if (!float.IsFinite(percentFull))
            throw new ArgumentOutOfRangeException(nameof(percentFull), "Harvestable PercentFull must be finite.");

        if (percentFull >= 0.75f)
            return 0;
        if (percentFull >= 0.30f)
            return 1;
        if (percentFull >= 0.0001f)
            return 2;
        return 3;
    }

    public static EnbCurrentRetailRslDirectStateSelection? ResolveDirectRslSelection(
        IEnbRenderObject obj,
        EnbResolvedRslReference rsl)
    {
        ArgumentNullException.ThrowIfNull(obj);
        ArgumentNullException.ThrowIfNull(rsl);

        if (obj.ObjectType != HarvestableObjectType ||
            obj.HarvestablePercentFull is not float percentFull ||
            rsl.ResourceGraph is null)
        {
            return null;
        }

        int stateId = ResolveHarvestableRenderState(percentFull);
        if (!rsl.ResourceGraph.TryFindState(stateId, out EnbResolvedRslStateResource selectedState))
        {
            return new EnbCurrentRetailRslDirectStateSelection(
                stateId,
                EnbCurrentRetailRslDirectStateSelectionKind.HarvestablePercentFull,
                IsResolvable: false,
                Diagnostic: $"Harvestable PercentFull {percentFull:R} selects missing RSL state {stateId}.");
        }

        // FUN_004604D0 rejects a state whose parsed base flags contain bit 4,
        // which is the current parser's RS_ENABLED bit.  Preserve that guard
        // rather than sending the state through the generic LINK activator.
        if (selectedState.Definition.Flags.Enabled)
        {
            return new EnbCurrentRetailRslDirectStateSelection(
                stateId,
                EnbCurrentRetailRslDirectStateSelectionKind.HarvestablePercentFull,
                IsResolvable: false,
                Diagnostic: $"Harvestable PercentFull {percentFull:R} selects RSL state {stateId}, but current retail's direct selector blocks RS_ENABLED states.");
        }

        return new EnbCurrentRetailRslDirectStateSelection(
            stateId,
            EnbCurrentRetailRslDirectStateSelectionKind.HarvestablePercentFull,
            IsResolvable: true,
            Diagnostic: null);
    }
}

public enum EnbCurrentRetailRslDirectStateSelectionKind
{
    HarvestablePercentFull = 1,
}

public readonly record struct EnbCurrentRetailRslDirectStateSelection(
    int StateId,
    EnbCurrentRetailRslDirectStateSelectionKind Kind,
    bool IsResolvable,
    string? Diagnostic);
