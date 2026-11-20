using EnbNext.Assets;

namespace EnbNext.Net7.Tests;

public sealed class EnbR6Z29CurrentRetailRslClassActivationEffectTests
{
    [Fact]
    public void ClassContractsMatchCurrentRetailActivationVirtuals()
    {
        EnbCurrentRetailRslClassActivationContract obj =
            EnbCurrentRetailRslClassActivationRuntime.Describe(EnbRslStateType.Object);
        Assert.True(obj.EnablesAndMovesToActiveListTail);
        Assert.True(obj.MaterializesRenderObject);
        Assert.True(obj.ReplacesCurrentRenderObjectState);
        Assert.False(obj.RequiresCurrentRenderObject);
        Assert.Equal(EnbCurrentRetailRslPreActivationEffect.NoOp, obj.PreActivationEffect);

        EnbCurrentRetailRslClassActivationContract animation =
            EnbCurrentRetailRslClassActivationRuntime.Describe(EnbRslStateType.Animation);
        Assert.True(animation.EnablesAndMovesToActiveListTail);
        Assert.True(animation.RequiresCurrentRenderObject);
        Assert.True(animation.BindsHierarchyAnimation);
        Assert.True(animation.PreservesAnimationFrameOnActivation);
        Assert.Equal(
            EnbCurrentRetailRslPreActivationEffect.AnimationFrameIfHierarchyAlreadyBound,
            animation.PreActivationEffect);

        EnbCurrentRetailRslClassActivationContract sound =
            EnbCurrentRetailRslClassActivationRuntime.Describe(EnbRslStateType.Sound);
        Assert.True(sound.EnablesAndMovesToActiveListTail);
        Assert.True(sound.ResolvesSoundHandle);
        Assert.True(sound.StartsSoundOnFirstActiveUpdate);
        Assert.False(sound.RequiresCurrentRenderObject);
        Assert.Equal(EnbCurrentRetailRslPreActivationEffect.NoOp, sound.PreActivationEffect);
    }

    [Fact]
    public void ObjectThenAnimationThenSoundTracksCurrentObjectAndClassEffectsInOrder()
    {
        EnbResolvedRslLibraryResourceGraph graph = ParseGraph("""
            [0\STATE0]
            ID=10
            TYPE=object
            OBJECT=ROOT
            LINK=11

            [0\STATE1]
            ID=11
            TYPE=anim
            SUFFIX=-1
            LINK=12

            [0\STATE2]
            ID=12
            TYPE=sound
            SOUND=IDLE
            """);

        EnbCurrentRetailRslLinkActivationPlan traversal =
            EnbCurrentRetailRslLinkActivationRuntime.Resolve(graph, 10);
        EnbCurrentRetailRslClassActivationPlan plan =
            EnbCurrentRetailRslClassActivationRuntime.Resolve(graph, traversal, initialCurrentObjectStateId: 3);

        Assert.True(plan.IsExecutableWithKnownCurrentObject);
        Assert.Equal(10, plan.FinalCurrentObjectStateId);
        Assert.Collection(plan.Steps,
            step =>
            {
                Assert.Equal(EnbCurrentRetailRslClassActivationEffect.ObjectRenderObject, step.Effect);
                Assert.Equal(3, step.CurrentObjectStateIdBefore);
                Assert.Equal(3, step.DeactivatedPreviousCurrentObjectStateId);
                Assert.Equal(10, step.CurrentObjectStateIdAfter);
            },
            step =>
            {
                Assert.Equal(EnbCurrentRetailRslClassActivationEffect.AnimationHierarchyBinding, step.Effect);
                Assert.Equal(10, step.CurrentObjectStateIdBefore);
                Assert.Equal(10, step.CurrentObjectStateIdAfter);
                Assert.True(step.IsExecutableWithKnownCurrentObject);
            },
            step =>
            {
                Assert.Equal(EnbCurrentRetailRslClassActivationEffect.SoundHandle, step.Effect);
                Assert.Equal(10, step.CurrentObjectStateIdBefore);
                Assert.Equal(10, step.CurrentObjectStateIdAfter);
                Assert.True(step.IsExecutableWithKnownCurrentObject);
            });
    }

    [Fact]
    public void AnimationWithoutCurrentObjectIsExplicitlyNotExecutable()
    {
        EnbResolvedRslLibraryResourceGraph graph = ParseGraph("""
            [0\STATE0]
            ID=7
            TYPE=anim
            SUFFIX=-1
            """);
        EnbCurrentRetailRslLinkActivationPlan traversal =
            EnbCurrentRetailRslLinkActivationRuntime.Resolve(graph, 7, firstTargetPreActivationSeconds: 0.0);
        EnbCurrentRetailRslClassActivationPlan plan =
            EnbCurrentRetailRslClassActivationRuntime.Resolve(graph, traversal);

        EnbCurrentRetailRslClassActivationStep step = Assert.Single(plan.Steps);
        Assert.False(step.IsExecutableWithKnownCurrentObject);
        Assert.False(plan.IsExecutableWithKnownCurrentObject);
        Assert.True(step.ReceivesPreActivationCallback);
        Assert.Equal(
            EnbCurrentRetailRslPreActivationEffect.AnimationFrameIfHierarchyAlreadyBound,
            step.PreActivationEffect);
    }

    [Fact]
    public void FirstTargetPreActivationIsNoOpForObjectAndSoundButAnimationSpecificForAnimation()
    {
        foreach ((EnbRslStateType type, string payload, EnbCurrentRetailRslPreActivationEffect expected) in new[]
        {
            (EnbRslStateType.Object, "OBJECT=ROOT", EnbCurrentRetailRslPreActivationEffect.NoOp),
            (EnbRslStateType.Sound, "SOUND=PING", EnbCurrentRetailRslPreActivationEffect.NoOp),
            (EnbRslStateType.Animation, "SUFFIX=-1", EnbCurrentRetailRslPreActivationEffect.AnimationFrameIfHierarchyAlreadyBound),
        })
        {
            string typeToken = type switch
            {
                EnbRslStateType.Object => "object",
                EnbRslStateType.Sound => "sound",
                _ => "anim",
            };
            EnbResolvedRslLibraryResourceGraph graph = ParseGraph($"""
                [0\STATE0]
                ID=1
                TYPE={typeToken}
                {payload}
                """);
            EnbCurrentRetailRslLinkActivationPlan traversal =
                EnbCurrentRetailRslLinkActivationRuntime.Resolve(graph, 1, firstTargetPreActivationSeconds: 0.0);
            EnbCurrentRetailRslClassActivationPlan plan =
                EnbCurrentRetailRslClassActivationRuntime.Resolve(
                    graph,
                    traversal,
                    initialCurrentObjectStateId: type == EnbRslStateType.Animation ? 99 : null);

            EnbCurrentRetailRslClassActivationStep step = Assert.Single(plan.Steps);
            Assert.True(step.ReceivesPreActivationCallback);
            Assert.Equal(expected, step.PreActivationEffect);
        }
    }


    [Fact]
    public void AnimationAuthoredLinkStartsAtLinkedSoundWithoutPreActivationCallback()
    {
        EnbResolvedRslLibraryResourceGraph graph = ParseGraph("""
            [0\STATE0]
            ID=1
            TYPE=anim
            SUFFIX=-1
            LINK=5

            [0\STATE1]
            ID=5
            TYPE=sound
            SOUND=IDLE
            """);

        Assert.True(graph.TryFindState(1, out EnbResolvedRslStateResource animation));
        Assert.Equal(5, animation.Definition.LinkStateId);

        EnbCurrentRetailRslLinkActivationPlan traversal =
            EnbCurrentRetailRslLinkActivationRuntime.Resolve(graph, animation.Definition.LinkStateId!.Value);
        EnbCurrentRetailRslClassActivationPlan plan =
            EnbCurrentRetailRslClassActivationRuntime.Resolve(graph, traversal, initialCurrentObjectStateId: 0);

        EnbCurrentRetailRslClassActivationStep step = Assert.Single(plan.Steps);
        Assert.Equal(5, step.StateId);
        Assert.Equal(EnbCurrentRetailRslClassActivationEffect.SoundHandle, step.Effect);
        Assert.False(step.ReceivesPreActivationCallback);
        Assert.True(step.IsExecutableWithKnownCurrentObject);
    }

    [Fact]
    public void RepeatedObjectActivationReplacesAndDeactivatesWhateverObjectWasCurrent()
    {
        EnbResolvedRslLibraryResourceGraph graph = ParseGraph("""
            [0\STATE0]
            ID=1
            TYPE=object
            OBJECT=A
            LINK=2

            [0\STATE1]
            ID=2
            TYPE=object
            OBJECT=B
            """);
        EnbCurrentRetailRslClassActivationPlan plan =
            EnbCurrentRetailRslClassActivationRuntime.Resolve(
                graph,
                EnbCurrentRetailRslLinkActivationRuntime.Resolve(graph, 1),
                initialCurrentObjectStateId: 50);

        Assert.Equal(2, plan.FinalCurrentObjectStateId);
        Assert.Equal(50, plan.Steps[0].DeactivatedPreviousCurrentObjectStateId);
        Assert.Equal(1, plan.Steps[1].DeactivatedPreviousCurrentObjectStateId);
    }

    private static EnbResolvedRslLibraryResourceGraph ParseGraph(string text)
    {
        EnbRetailRenderStateCatalog catalog = EnbRetailRenderStateCatalog.ParseText(text);
        Assert.True(catalog.TryGetLibrary(0, out EnbRslLibraryDefinition library));
        return new EnbRetailRslResourceResolver(resources: null).Resolve(library);
    }
}
