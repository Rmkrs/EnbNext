using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>
/// One current-retail star-layer render allocation. The manager passes the
/// current remaining StarBudget to the layer before subtracting the layer's
/// complete authored cost, so the final visited layer may be clipped.
/// </summary>
public sealed record EnbStarLayerBudgetAllocation(
    EnbRetailStarLayerDefinition Layer,
    int RemainingBudgetBeforeLayer,
    int VisibleStarCount,
    int VisibleRenderCost)
{
    public bool IsPartial => VisibleStarCount < Layer.NumStars;
}

public sealed record EnbStarBudgetSelection(
    IReadOnlyList<EnbStarLayerBudgetAllocation> LayerAllocations,
    int VisibleRenderCost,
    int TotalConfiguredCost,
    float ReportedCurrentCost)
{
    public int VisitedLayerCount => LayerAllocations.Count;

    public int VisibleStarCount
    {
        get
        {
            int total = 0;
            foreach (EnbStarLayerBudgetAllocation allocation in LayerAllocations)
                total = unchecked(total + allocation.VisibleStarCount);
            return total;
        }
    }
}

/// <summary>
/// Current-retail star-field budget policy. StarBudget is an internal cap for
/// the ordered stars.ini layers. Each visited layer receives the remaining
/// budget and may render only a prefix of its authored stars; only afterwards
/// does the manager subtract that layer's complete authored cost and decide
/// whether to visit another layer.
///
/// The star manager itself is a fixed-cost scene layer rather than a selectable
/// projected-detail/HLOD candidate. Its reported current cost is
/// min(total configured layer cost, StarBudget).
/// </summary>
public static class EnbStarBudgetSemantics
{
    public static EnbStarBudgetSelection Allocate(
        IReadOnlyList<EnbRetailStarLayerDefinition> layers,
        int starBudget)
    {
        ArgumentNullException.ThrowIfNull(layers);

        int totalConfiguredCost = 0;
        foreach (EnbRetailStarLayerDefinition layer in layers)
        {
            if (layer.NumStars < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(layers),
                    "Star-layer NumStars must be non-negative at the R5H semantic boundary.");
            }

            totalConfiguredCost = unchecked(totalConfiguredCost + layer.RetailRenderCost);
        }

        var allocations = new List<EnbStarLayerBudgetAllocation>();
        int remainingBudget = starBudget;
        int visibleRenderCost = 0;

        if (layers.Count != 0)
        {
            int index = 0;
            do
            {
                EnbRetailStarLayerDefinition layer = layers[index];
                int costPerStar = layer.Quads ? 2 : 1;

                //481C22 uses SHR, then481C28 zero-extends the budget for FILD.
                //The manager's subsequent subtraction/continuation is signed.
                uint budgetStarCount = unchecked((uint)remainingBudget) / (uint)costPerStar;
                int visibleStarCount = (int)Math.Min((uint)layer.NumStars, budgetStarCount);
                int layerVisibleCost = unchecked(visibleStarCount * costPerStar);

                allocations.Add(new EnbStarLayerBudgetAllocation(
                    layer,
                    remainingBudget,
                    visibleStarCount,
                    layerVisibleCost));
                visibleRenderCost = unchecked(visibleRenderCost + layerVisibleCost);

                // FUN_00482D80 subtracts the complete authored layer cost,
                // not the clipped visible cost, then continues only if > 0.
                remainingBudget = unchecked(remainingBudget - layer.RetailRenderCost);
                index++;
            }
            while (remainingBudget > 0 && index < layers.Count);
        }

        return new EnbStarBudgetSelection(
            allocations,
            visibleRenderCost,
            totalConfiguredCost,
            Math.Min((float)totalConfiguredCost, unchecked((uint)starBudget)));
    }
}
