namespace EnbNext.Assets;

/// <summary>
/// Reduces one already-proven current-retail generic LINK/class-activation
/// sequence to the visible single-RenderObj lane. Object activations replace
/// the owner's current RenderObj immediately. An Animation activated before a
/// later Object replacement no longer targets the final visible RenderObj; an
/// Animation after the final Object replacement binds that final hierarchy.
/// Multiple Animations against the same final current RenderObj are returned
/// in current-retail active-list order. Every successful activation appends to
/// the tail through library+20, which aliases the end sentinel's previous field.
/// </summary>
public static class EnbCurrentRetailRslVisualActivationRuntime
{
    public static EnbCurrentRetailRslVisualActivationPlan Resolve(
        EnbCurrentRetailRslClassActivationPlan classPlan)
    {
        ArgumentNullException.ThrowIfNull(classPlan);

        EnbCurrentRetailRslClassActivationStep[] visualSteps = classPlan.Steps
            .Where(static step =>
                step.Effect is EnbCurrentRetailRslClassActivationEffect.ObjectRenderObject or
                               EnbCurrentRetailRslClassActivationEffect.AnimationHierarchyBinding)
            .ToArray();

        if (visualSteps.Length == 0)
        {
            return new EnbCurrentRetailRslVisualActivationPlan(
                classPlan.InitialCurrentObjectStateId,
                classPlan.FinalCurrentObjectStateId,
                FinalVisibleAnimationStateIds: [],
                ReplacesCurrentRenderObject: false,
                IgnoredAnimationStateIdsBeforeFinalObjectReplacement: [],
                IsExecutable: true,
                Diagnostic: null);
        }

        EnbCurrentRetailRslClassActivationStep? finalObjectStep = visualSteps
            .Where(static step => step.Effect == EnbCurrentRetailRslClassActivationEffect.ObjectRenderObject)
            .LastOrDefault();
        int finalObjectOrdinal = finalObjectStep?.ActivationOrdinal ?? int.MinValue;

        int[] preReplacementAnimations = visualSteps
            .Where(step =>
                step.Effect == EnbCurrentRetailRslClassActivationEffect.AnimationHierarchyBinding &&
                step.ActivationOrdinal < finalObjectOrdinal)
            .Select(static step => step.StateId)
            .ToArray();

        EnbCurrentRetailRslClassActivationStep[] finalObjectAnimations = visualSteps
            .Where(step =>
                step.Effect == EnbCurrentRetailRslClassActivationEffect.AnimationHierarchyBinding &&
                step.ActivationOrdinal > finalObjectOrdinal)
            .ToArray();

        EnbCurrentRetailRslClassActivationStep? unresolvedAnimation = finalObjectAnimations.FirstOrDefault(
            static animation => !animation.IsExecutableWithKnownCurrentObject);
        if (unresolvedAnimation is not null)
        {
            return new EnbCurrentRetailRslVisualActivationPlan(
                classPlan.InitialCurrentObjectStateId,
                classPlan.FinalCurrentObjectStateId,
                finalObjectAnimations
                    .OrderBy(static animation => animation.ActivationOrdinal)
                    .Select(static animation => animation.StateId)
                    .ToArray(),
                ReplacesCurrentRenderObject: finalObjectStep is not null,
                IgnoredAnimationStateIdsBeforeFinalObjectReplacement: preReplacementAnimations,
                IsExecutable: false,
                Diagnostic:
                    $"Animation state {unresolvedAnimation.StateId} has no proven current RenderObj at its activation point.");
        }

        return new EnbCurrentRetailRslVisualActivationPlan(
            classPlan.InitialCurrentObjectStateId,
            classPlan.FinalCurrentObjectStateId,
            finalObjectAnimations
                .OrderBy(static animation => animation.ActivationOrdinal)
                .Select(static animation => animation.StateId)
                .ToArray(),
            ReplacesCurrentRenderObject: finalObjectStep is not null,
            IgnoredAnimationStateIdsBeforeFinalObjectReplacement: preReplacementAnimations,
            IsExecutable: true,
            Diagnostic: null);
    }
}

public sealed record EnbCurrentRetailRslVisualActivationPlan(
    int? InitialCurrentObjectStateId,
    int? FinalCurrentObjectStateId,
    IReadOnlyList<int> FinalVisibleAnimationStateIds,
    bool ReplacesCurrentRenderObject,
    IReadOnlyList<int> IgnoredAnimationStateIdsBeforeFinalObjectReplacement,
    bool IsExecutable,
    string? Diagnostic)
{
    public int? FinalVisibleAnimationStateId =>
        FinalVisibleAnimationStateIds.Count == 1 ? FinalVisibleAnimationStateIds[0] : null;

    public bool ChangesVisiblePresentation =>
        ReplacesCurrentRenderObject || FinalVisibleAnimationStateIds.Count != 0;
}
