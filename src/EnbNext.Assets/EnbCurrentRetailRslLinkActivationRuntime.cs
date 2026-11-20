namespace EnbNext.Assets;

/// <summary>
/// Current-retail generic RSL activation/link traversal recovered from
/// FUN_00460440. The starting state is activated first and each authored LINK
/// is then activated in sequence while the caller-supplied link budget remains
/// non-negative. Retail uses a budget of 0x32 for normal activation paths.
/// </summary>
public static class EnbCurrentRetailRslLinkActivationRuntime
{
    public const int DefaultLinkBudget = 0x32;

    public static EnbCurrentRetailRslLinkActivationPlan Resolve(
        EnbResolvedRslLibraryResourceGraph graph,
        int startingStateId,
        int linkBudget = DefaultLinkBudget,
        double? firstTargetPreActivationSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(graph);

        if (linkBudget < 0)
        {
            return new EnbCurrentRetailRslLinkActivationPlan(
                startingStateId,
                linkBudget,
                [],
                EnbCurrentRetailRslLinkActivationTermination.NegativeInitialBudget,
                null,
                firstTargetPreActivationSeconds);
        }

        if (!graph.TryFindState(startingStateId, out EnbResolvedRslStateResource current))
        {
            return new EnbCurrentRetailRslLinkActivationPlan(
                startingStateId,
                linkBudget,
                [],
                EnbCurrentRetailRslLinkActivationTermination.MissingStartingTarget,
                startingStateId,
                firstTargetPreActivationSeconds);
        }

        var steps = new List<EnbCurrentRetailRslLinkActivationStep>();
        int remainingBudget = linkBudget;

        while (true)
        {
            steps.Add(new EnbCurrentRetailRslLinkActivationStep(
                steps.Count,
                current.Definition.StateId,
                current.Definition.Type,
                ReceivesPreActivationCallback: steps.Count == 0 && firstTargetPreActivationSeconds.HasValue));

            if (current.Definition.LinkStateId is not int linkedStateId)
            {
                return new EnbCurrentRetailRslLinkActivationPlan(
                    startingStateId,
                    linkBudget,
                    steps,
                    EnbCurrentRetailRslLinkActivationTermination.NoFurtherLink,
                    null,
                    firstTargetPreActivationSeconds);
            }

            if (!graph.TryFindState(linkedStateId, out EnbResolvedRslStateResource linkedState))
            {
                return new EnbCurrentRetailRslLinkActivationPlan(
                    startingStateId,
                    linkBudget,
                    steps,
                    EnbCurrentRetailRslLinkActivationTermination.MissingLinkedTarget,
                    linkedStateId,
                    firstTargetPreActivationSeconds);
            }

            remainingBudget--;
            if (remainingBudget < 0)
            {
                return new EnbCurrentRetailRslLinkActivationPlan(
                    startingStateId,
                    linkBudget,
                    steps,
                    EnbCurrentRetailRslLinkActivationTermination.LinkBudgetExhausted,
                    linkedStateId,
                    firstTargetPreActivationSeconds);
            }

            current = linkedState;
        }
    }
}

public enum EnbCurrentRetailRslLinkActivationTermination
{
    NoFurtherLink,
    MissingStartingTarget,
    MissingLinkedTarget,
    NegativeInitialBudget,
    LinkBudgetExhausted,
}

public sealed record EnbCurrentRetailRslLinkActivationStep(
    int ActivationOrdinal,
    int StateId,
    EnbRslStateType StateType,
    bool ReceivesPreActivationCallback);

public sealed record EnbCurrentRetailRslLinkActivationPlan(
    int StartingStateId,
    int InitialLinkBudget,
    IReadOnlyList<EnbCurrentRetailRslLinkActivationStep> Steps,
    EnbCurrentRetailRslLinkActivationTermination Termination,
    int? UnresolvedOrUnfollowedLinkStateId,
    double? FirstTargetPreActivationSeconds);
