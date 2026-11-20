namespace EnbNext.Assets;

/// <summary>
/// Class-specific side effects of the current-retail RSL activation virtual
/// (+0x14) reached by the generic LINK walker. These are executable client
/// semantics, not authored rs.ini values.
/// </summary>
public static class EnbCurrentRetailRslClassActivationRuntime
{
    public static EnbCurrentRetailRslClassActivationContract Describe(EnbRslStateType type)
        => type switch
        {
            //00459030 enables/relinks and clears completion; its material
            //scope is applied separately by the library's draw traversal.
            EnbRslStateType.EffectPass => new EnbCurrentRetailRslClassActivationContract(
                type, EnbCurrentRetailRslClassActivationEffect.MaterialPass,
                EnbCurrentRetailRslPreActivationEffect.NoOp,
                EnablesAndMovesToActiveListTail: true,
                MaterializesRenderObject: false,
                ReplacesCurrentRenderObjectState: false,
                RequiresCurrentRenderObject: false,
                BindsHierarchyAnimation: false,
                PreservesAnimationFrameOnActivation: false,
                ResolvesSoundHandle: false,
                StartsSoundOnFirstActiveUpdate: false),
            //00458B20 enables/relinks, materializes the inherited Object,
            //unhides it and asks004609C0 to attach it to the current Object.
            //Attachment can fail without undoing activation or replacing it.
            EnbRslStateType.Bone => new EnbCurrentRetailRslClassActivationContract(
                type,
                EnbCurrentRetailRslClassActivationEffect.BoneAttachment,
                EnbCurrentRetailRslPreActivationEffect.NoOp,
                EnablesAndMovesToActiveListTail: true,
                MaterializesRenderObject: true,
                ReplacesCurrentRenderObjectState: false,
                RequiresCurrentRenderObject: false,
                BindsHierarchyAnimation: false,
                PreservesAnimationFrameOnActivation: false,
                ResolvesSoundHandle: false,
                StartsSoundOnFirstActiveUpdate: false),
            //0045D9B0 calls owner+1D8 with (this,MESSAGE). Primary controller
            //00AE8294+1D8 resolves00460BA0 RET8. No enabling or relinking.
            EnbRslStateType.Message => new EnbCurrentRetailRslClassActivationContract(
                type,
                EnbCurrentRetailRslClassActivationEffect.ControllerMessage,
                EnbCurrentRetailRslPreActivationEffect.NoOp,
                EnablesAndMovesToActiveListTail: false,
                MaterializesRenderObject: false,
                ReplacesCurrentRenderObjectState: false,
                RequiresCurrentRenderObject: false,
                BindsHierarchyAnimation: false,
                PreservesAnimationFrameOnActivation: false,
                ResolvesSoundHandle: false,
                StartsSoundOnFirstActiveUpdate: false),
            //0045A290 calls00457E10 and returns. The surrounding00460440
            //walker subsequently activates LINK; it does not replace Object here.
            EnbRslStateType.Link => new EnbCurrentRetailRslClassActivationContract(
                type,
                EnbCurrentRetailRslClassActivationEffect.LinkState,
                EnbCurrentRetailRslPreActivationEffect.NoOp,
                EnablesAndMovesToActiveListTail: true,
                MaterializesRenderObject: false,
                ReplacesCurrentRenderObjectState: false,
                RequiresCurrentRenderObject: false,
                BindsHierarchyAnimation: false,
                PreservesAnimationFrameOnActivation: false,
                ResolvesSoundHandle: false,
                StartsSoundOnFirstActiveUpdate: false),
            EnbRslStateType.Object => new EnbCurrentRetailRslClassActivationContract(
                type,
                EnbCurrentRetailRslClassActivationEffect.ObjectRenderObject,
                EnbCurrentRetailRslPreActivationEffect.NoOp,
                EnablesAndMovesToActiveListTail: true,
                MaterializesRenderObject: true,
                ReplacesCurrentRenderObjectState: true,
                RequiresCurrentRenderObject: false,
                BindsHierarchyAnimation: false,
                PreservesAnimationFrameOnActivation: false,
                ResolvesSoundHandle: false,
                StartsSoundOnFirstActiveUpdate: false),

            EnbRslStateType.Animation => new EnbCurrentRetailRslClassActivationContract(
                type,
                EnbCurrentRetailRslClassActivationEffect.AnimationHierarchyBinding,
                EnbCurrentRetailRslPreActivationEffect.AnimationFrameIfHierarchyAlreadyBound,
                EnablesAndMovesToActiveListTail: true,
                MaterializesRenderObject: false,
                ReplacesCurrentRenderObjectState: false,
                RequiresCurrentRenderObject: true,
                BindsHierarchyAnimation: true,
                PreservesAnimationFrameOnActivation: true,
                ResolvesSoundHandle: false,
                StartsSoundOnFirstActiveUpdate: false),

            EnbRslStateType.Sound => new EnbCurrentRetailRslClassActivationContract(
                type,
                EnbCurrentRetailRslClassActivationEffect.SoundHandle,
                EnbCurrentRetailRslPreActivationEffect.NoOp,
                EnablesAndMovesToActiveListTail: true,
                MaterializesRenderObject: false,
                ReplacesCurrentRenderObjectState: false,
                RequiresCurrentRenderObject: false,
                BindsHierarchyAnimation: false,
                PreservesAnimationFrameOnActivation: false,
                ResolvesSoundHandle: true,
                StartsSoundOnFirstActiveUpdate: true),

            _ => new EnbCurrentRetailRslClassActivationContract(
                type,
                EnbCurrentRetailRslClassActivationEffect.Unsupported,
                EnbCurrentRetailRslPreActivationEffect.Unknown,
                EnablesAndMovesToActiveListTail: false,
                MaterializesRenderObject: false,
                ReplacesCurrentRenderObjectState: false,
                RequiresCurrentRenderObject: false,
                BindsHierarchyAnimation: false,
                PreservesAnimationFrameOnActivation: false,
                ResolvesSoundHandle: false,
                StartsSoundOnFirstActiveUpdate: false),
        };

    /// <summary>
    /// Annotates an already-resolved R6Z28 LINK traversal with the exact
    /// class-dispatch effects invoked by each activation. Object replacement is
    /// tracked because current retail deactivates the previous current Object
    /// state before installing the newly activated Object state. Animation
    /// activation records which current Object state it requires at that exact
    /// point in the ordered chain.
    /// </summary>
    public static EnbCurrentRetailRslClassActivationPlan Resolve(
        EnbResolvedRslLibraryResourceGraph graph,
        EnbCurrentRetailRslLinkActivationPlan traversal,
        int? initialCurrentObjectStateId = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(traversal);

        int? currentObjectStateId = initialCurrentObjectStateId;
        var steps = new List<EnbCurrentRetailRslClassActivationStep>(traversal.Steps.Count);

        foreach (EnbCurrentRetailRslLinkActivationStep traversalStep in traversal.Steps)
        {
            if (!graph.TryFindState(traversalStep.StateId, out EnbResolvedRslStateResource state))
            {
                steps.Add(new EnbCurrentRetailRslClassActivationStep(
                    traversalStep.ActivationOrdinal,
                    traversalStep.StateId,
                    traversalStep.StateType,
                    EnbCurrentRetailRslClassActivationEffect.Unsupported,
                    EnbCurrentRetailRslPreActivationEffect.Unknown,
                    traversalStep.ReceivesPreActivationCallback,
                    currentObjectStateId,
                    currentObjectStateId,
                    DeactivatedPreviousCurrentObjectStateId: null,
                    IsExecutableWithKnownCurrentObject: false));
                continue;
            }

            EnbCurrentRetailRslClassActivationContract contract = Describe(state.Definition.Type);
            int? objectBefore = currentObjectStateId;
            int? deactivatedObject = null;
            bool executable = contract.Effect != EnbCurrentRetailRslClassActivationEffect.Unsupported;
            if (contract.Effect == EnbCurrentRetailRslClassActivationEffect.MaterialPass)
                executable = state.FileDependency.EffectPass is { ConstructsUsablePass: true, Texture.Length: > 0 };

            if (contract.Effect == EnbCurrentRetailRslClassActivationEffect.ObjectRenderObject)
            {
                deactivatedObject = currentObjectStateId;
                currentObjectStateId = state.Definition.StateId;
            }
            else if (contract.Effect == EnbCurrentRetailRslClassActivationEffect.AnimationHierarchyBinding)
            {
                executable = currentObjectStateId.HasValue;
            }

            steps.Add(new EnbCurrentRetailRslClassActivationStep(
                traversalStep.ActivationOrdinal,
                state.Definition.StateId,
                state.Definition.Type,
                contract.Effect,
                contract.PreActivationEffect,
                traversalStep.ReceivesPreActivationCallback,
                objectBefore,
                currentObjectStateId,
                deactivatedObject,
                executable));
        }

        return new EnbCurrentRetailRslClassActivationPlan(
            traversal,
            initialCurrentObjectStateId,
            currentObjectStateId,
            steps,
            steps.All(static step => step.IsExecutableWithKnownCurrentObject));
    }
}

public enum EnbCurrentRetailRslClassActivationEffect
{
    Unsupported,
    ObjectRenderObject,
    AnimationHierarchyBinding,
    SoundHandle,
    LinkState,
    ControllerMessage,
    BoneAttachment,
    MaterialPass,
}

public enum EnbCurrentRetailRslPreActivationEffect
{
    Unknown,
    NoOp,
    AnimationFrameIfHierarchyAlreadyBound,
}

public sealed record EnbCurrentRetailRslClassActivationContract(
    EnbRslStateType StateType,
    EnbCurrentRetailRslClassActivationEffect Effect,
    EnbCurrentRetailRslPreActivationEffect PreActivationEffect,
    bool EnablesAndMovesToActiveListTail,
    bool MaterializesRenderObject,
    bool ReplacesCurrentRenderObjectState,
    bool RequiresCurrentRenderObject,
    bool BindsHierarchyAnimation,
    bool PreservesAnimationFrameOnActivation,
    bool ResolvesSoundHandle,
    bool StartsSoundOnFirstActiveUpdate);

public sealed record EnbCurrentRetailRslClassActivationStep(
    int ActivationOrdinal,
    int StateId,
    EnbRslStateType StateType,
    EnbCurrentRetailRslClassActivationEffect Effect,
    EnbCurrentRetailRslPreActivationEffect PreActivationEffect,
    bool ReceivesPreActivationCallback,
    int? CurrentObjectStateIdBefore,
    int? CurrentObjectStateIdAfter,
    int? DeactivatedPreviousCurrentObjectStateId,
    bool IsExecutableWithKnownCurrentObject);

public sealed record EnbCurrentRetailRslClassActivationPlan(
    EnbCurrentRetailRslLinkActivationPlan Traversal,
    int? InitialCurrentObjectStateId,
    int? FinalCurrentObjectStateId,
    IReadOnlyList<EnbCurrentRetailRslClassActivationStep> Steps,
    bool IsExecutableWithKnownCurrentObject);
