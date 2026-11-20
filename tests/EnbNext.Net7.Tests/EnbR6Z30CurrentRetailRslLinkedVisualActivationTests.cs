using EnbNext.Assets;

namespace EnbNext.Net7.Tests;

public sealed class EnbR6Z30CurrentRetailRslLinkedVisualActivationTests
{
    [Fact]
    public void ObjectThenAnimationThenSoundSelectsReplacementObjectAndLinkedAnimation()
    {
        EnbCurrentRetailRslVisualActivationPlan plan = Resolve("""
            [0\STATE0]
            ID=10
            TYPE=object
            OBJECT=ROOT_B
            LINK=11

            [0\STATE1]
            ID=11
            TYPE=anim
            SUFFIX=-2
            LINK=12

            [0\STATE2]
            ID=12
            TYPE=sound
            SOUND=HUM
            """, 10, initialObject: 3);

        Assert.True(plan.IsExecutable);
        Assert.True(plan.ChangesVisiblePresentation);
        Assert.True(plan.ReplacesCurrentRenderObject);
        Assert.Equal(10, plan.FinalCurrentObjectStateId);
        Assert.Equal(11, plan.FinalVisibleAnimationStateId);
        Assert.Empty(plan.IgnoredAnimationStateIdsBeforeFinalObjectReplacement);
    }

    [Fact]
    public void AnimationBeforeLaterObjectReplacementDoesNotControlFinalVisibleObject()
    {
        EnbCurrentRetailRslVisualActivationPlan plan = Resolve("""
            [0\STATE0]
            ID=20
            TYPE=anim
            SUFFIX=-1
            LINK=21

            [0\STATE1]
            ID=21
            TYPE=object
            OBJECT=ROOT_C
            """, 20, initialObject: 2);

        Assert.True(plan.IsExecutable);
        Assert.True(plan.ReplacesCurrentRenderObject);
        Assert.Equal(21, plan.FinalCurrentObjectStateId);
        Assert.Null(plan.FinalVisibleAnimationStateId);
        Assert.Equal(new[] { 20 }, plan.IgnoredAnimationStateIdsBeforeFinalObjectReplacement);
    }

    [Fact]
    public void MultipleObjectReplacementsSelectTheLastObject()
    {
        EnbCurrentRetailRslVisualActivationPlan plan = Resolve("""
            [0\STATE0]
            ID=30
            TYPE=object
            OBJECT=A
            LINK=31

            [0\STATE1]
            ID=31
            TYPE=object
            OBJECT=B
            LINK=32

            [0\STATE2]
            ID=32
            TYPE=anim
            SUFFIX=-1
            """, 30, initialObject: 1);

        Assert.True(plan.IsExecutable);
        Assert.Equal(31, plan.FinalCurrentObjectStateId);
        Assert.Equal(32, plan.FinalVisibleAnimationStateId);
    }

    [Fact]
    public void MultipleAnimationsAgainstFinalObjectAreReturnedInRetailActiveListOrder()
    {
        EnbCurrentRetailRslVisualActivationPlan plan = Resolve("""
            [0\STATE0]
            ID=40
            TYPE=object
            OBJECT=A
            LINK=41

            [0\STATE1]
            ID=41
            TYPE=anim
            SUFFIX=-1
            LINK=42

            [0\STATE2]
            ID=42
            TYPE=anim
            SUFFIX=-2
            """, 40, initialObject: 1);

        Assert.True(plan.IsExecutable, plan.Diagnostic);
        Assert.Equal(new[] { 41, 42 }, plan.FinalVisibleAnimationStateIds);
    }

    [Fact]
    public void SoundOnlyChainDoesNotChangeVisiblePresentation()
    {
        EnbCurrentRetailRslVisualActivationPlan plan = Resolve("""
            [0\STATE0]
            ID=50
            TYPE=sound
            SOUND=HUM
            """, 50, initialObject: 7);

        Assert.True(plan.IsExecutable);
        Assert.False(plan.ChangesVisiblePresentation);
        Assert.False(plan.ReplacesCurrentRenderObject);
        Assert.Equal(7, plan.FinalCurrentObjectStateId);
        Assert.Null(plan.FinalVisibleAnimationStateId);
    }

    private static EnbCurrentRetailRslVisualActivationPlan Resolve(
        string text,
        int startStateId,
        int? initialObject)
    {
        EnbRetailRenderStateCatalog catalog = EnbRetailRenderStateCatalog.ParseText(text);
        Assert.True(catalog.TryGetLibrary(0, out EnbRslLibraryDefinition library));
        EnbResolvedRslLibraryResourceGraph graph = new EnbRetailRslResourceResolver(resources: null).Resolve(library);
        EnbCurrentRetailRslLinkActivationPlan traversal =
            EnbCurrentRetailRslLinkActivationRuntime.Resolve(graph, startStateId);
        EnbCurrentRetailRslClassActivationPlan classPlan =
            EnbCurrentRetailRslClassActivationRuntime.Resolve(graph, traversal, initialObject);
        return EnbCurrentRetailRslVisualActivationRuntime.Resolve(classPlan);
    }
}
