namespace EnbNext.Assets;

/// <summary>
/// Current-retail terminal lifetime ordering for an Animation state that reaches
/// an end transition. FUN_004584B0 invokes the generic deactivation walker
/// FUN_00460710 on the source state and its complete LINK chain before invoking
/// NEXT virtual +0x34 and the normal activation/LINK walker FUN_00460440 on the
/// target. This helper records that ordering without inventing asynchronous
/// timing or state-class effects.
/// </summary>
public static class EnbCurrentRetailRslAnimationLifetimeRuntime
{
    public static EnbCurrentRetailRslAnimationLifetimePlan ResolveAuthoredNext(
        EnbResolvedRslLibraryResourceGraph graph,
        int sourceStateId)
    {
        ArgumentNullException.ThrowIfNull(graph);

        if (!graph.TryFindState(sourceStateId, out EnbResolvedRslStateResource source))
        {
            return new EnbCurrentRetailRslAnimationLifetimePlan(
                sourceStateId,
                TargetStateId: null,
                DeactivatedStateIds: [],
                TargetActivation: null,
                IsExecutable: false,
                Diagnostic: $"source Animation state {sourceStateId} is absent from the resolved RSL graph.");
        }
        if (source.Definition.Type != EnbRslStateType.Animation)
        {
            return new EnbCurrentRetailRslAnimationLifetimePlan(
                sourceStateId,
                TargetStateId: null,
                DeactivatedStateIds: [],
                TargetActivation: null,
                IsExecutable: false,
                Diagnostic: $"source state {sourceStateId} is {source.Definition.Type}, not Animation.");
        }
        if (!source.Definition.Flags.ActivateNext)
        {
            return new EnbCurrentRetailRslAnimationLifetimePlan(
                sourceStateId,
                source.Definition.NextStateId,
                DeactivatedStateIds: [],
                TargetActivation: null,
                IsExecutable: false,
                Diagnostic: $"Animation state {sourceStateId} does not author RS_ACTIVATE_NEXT; instance copying clears authored RS_ACTIVATE_NEXT_ONCE.");
        }

        if (!TryResolveDeactivationChain(graph, sourceStateId, out int[] deactivated, out string? deactivationDiagnostic))
        {
            return new EnbCurrentRetailRslAnimationLifetimePlan(
                sourceStateId,
                source.Definition.NextStateId,
                deactivated,
                TargetActivation: null,
                IsExecutable: false,
                Diagnostic: deactivationDiagnostic);
        }

        if (source.Definition.NextStateId is not int targetStateId)
        {
            return new EnbCurrentRetailRslAnimationLifetimePlan(
                sourceStateId,
                TargetStateId: null,
                deactivated,
                TargetActivation: null,
                IsExecutable: true,
                Diagnostic: null);
        }

        EnbCurrentRetailRslLinkActivationPlan activation =
            EnbCurrentRetailRslLinkActivationRuntime.Resolve(graph, targetStateId);
        if (activation.Termination is not (EnbCurrentRetailRslLinkActivationTermination.NoFurtherLink
            or EnbCurrentRetailRslLinkActivationTermination.LinkBudgetExhausted))
        {
            return new EnbCurrentRetailRslAnimationLifetimePlan(
                sourceStateId,
                targetStateId,
                deactivated,
                activation,
                IsExecutable: false,
                Diagnostic:
                    $"NEXT activation chain from state {targetStateId} ended as {activation.Termination} at {activation.UnresolvedOrUnfollowedLinkStateId?.ToString() ?? "-"}.");
        }

        return new EnbCurrentRetailRslAnimationLifetimePlan(
            sourceStateId,
            targetStateId,
            deactivated,
            activation,
            IsExecutable: true,
            Diagnostic: null);
    }

    private static bool TryResolveDeactivationChain(
        EnbResolvedRslLibraryResourceGraph graph,
        int sourceStateId,
        out int[] stateIds,
        out string? diagnostic)
    {
        var ordered = new List<int>();
        var visited = new HashSet<int>();
        diagnostic = null;
        int currentStateId = sourceStateId;

        while (true)
        {
            if (!visited.Add(currentStateId))
            {
                stateIds = ordered.ToArray();
                diagnostic =
                    $"source deactivation LINK chain cycles back to state {currentStateId}; current retail would keep following linked state pointers and EnbNext remains fail-closed.";
                return false;
            }
            if (!graph.TryFindState(currentStateId, out EnbResolvedRslStateResource current))
            {
                stateIds = ordered.ToArray();
                diagnostic = $"source deactivation LINK chain references missing state {currentStateId}.";
                return false;
            }

            ordered.Add(currentStateId);
            if (current.Definition.LinkStateId is not int linkedStateId)
            {
                stateIds = ordered.ToArray();
                return true;
            }

            currentStateId = linkedStateId;
        }
    }
}

public sealed record EnbCurrentRetailRslAnimationLifetimePlan(
    int SourceStateId,
    int? TargetStateId,
    IReadOnlyList<int> DeactivatedStateIds,
    EnbCurrentRetailRslLinkActivationPlan? TargetActivation,
    bool IsExecutable,
    string? Diagnostic)
{
    public IReadOnlyList<int> ActivatedTargetStateIds =>
        TargetActivation?.Steps.Select(static step => step.StateId).ToArray() ?? [];
}
