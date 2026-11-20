using EnbNext.Assets;

namespace EnbNext.Net7.Tests;

public sealed class EnbR6Z28CurrentRetailRslLinkActivationChainTests
{
    [Fact]
    public void GenericActivationStartsAtTargetAndFollowsAuthoredLinksInOrder()
    {
        EnbResolvedRslLibraryResourceGraph graph = ParseGraph("""
            [0\STATE0]
            ID=7
            TYPE=anim
            SUFFIX=-1
            LINK=8

            [0\STATE1]
            ID=8
            TYPE=sound
            SOUND=STEP
            LINK=9

            [0\STATE2]
            ID=9
            TYPE=object
            OBJECT=END
            """);

        EnbCurrentRetailRslLinkActivationPlan plan =
            EnbCurrentRetailRslLinkActivationRuntime.Resolve(graph, 7, firstTargetPreActivationSeconds: 0.0);

        Assert.Equal(EnbCurrentRetailRslLinkActivationTermination.NoFurtherLink, plan.Termination);
        Assert.Equal(0.0, plan.FirstTargetPreActivationSeconds);
        Assert.Collection(plan.Steps,
            step =>
            {
                Assert.Equal(0, step.ActivationOrdinal);
                Assert.Equal(7, step.StateId);
                Assert.Equal(EnbRslStateType.Animation, step.StateType);
                Assert.True(step.ReceivesPreActivationCallback);
            },
            step =>
            {
                Assert.Equal(1, step.ActivationOrdinal);
                Assert.Equal(8, step.StateId);
                Assert.Equal(EnbRslStateType.Sound, step.StateType);
                Assert.False(step.ReceivesPreActivationCallback);
            },
            step =>
            {
                Assert.Equal(2, step.ActivationOrdinal);
                Assert.Equal(9, step.StateId);
                Assert.Equal(EnbRslStateType.Object, step.StateType);
                Assert.False(step.ReceivesPreActivationCallback);
            });
    }

    [Fact]
    public void RetailDefaultBudgetActivatesAtMostFiftyOneStatesAndDoesNotDetectCycles()
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
            LINK=1
            """);

        EnbCurrentRetailRslLinkActivationPlan plan =
            EnbCurrentRetailRslLinkActivationRuntime.Resolve(graph, 1);

        Assert.Equal(0x32, plan.InitialLinkBudget);
        Assert.Equal(51, plan.Steps.Count);
        Assert.Equal(EnbCurrentRetailRslLinkActivationTermination.LinkBudgetExhausted, plan.Termination);
        Assert.Equal(2, plan.UnresolvedOrUnfollowedLinkStateId);
        Assert.Equal(1, plan.Steps[0].StateId);
        Assert.Equal(2, plan.Steps[1].StateId);
        Assert.Equal(1, plan.Steps[50].StateId);
        Assert.DoesNotContain(plan.Steps, static step => step.ReceivesPreActivationCallback);
    }

    [Fact]
    public void ZeroBudgetStillActivatesTheStartingTargetButDoesNotFollowItsLink()
    {
        EnbResolvedRslLibraryResourceGraph graph = ParseGraph("""
            [0\STATE0]
            ID=10
            TYPE=sound
            SOUND=FIRST
            LINK=11

            [0\STATE1]
            ID=11
            TYPE=sound
            SOUND=SECOND
            """);

        EnbCurrentRetailRslLinkActivationPlan plan =
            EnbCurrentRetailRslLinkActivationRuntime.Resolve(graph, 10, linkBudget: 0);

        EnbCurrentRetailRslLinkActivationStep step = Assert.Single(plan.Steps);
        Assert.Equal(10, step.StateId);
        Assert.False(step.ReceivesPreActivationCallback);
        Assert.Equal(EnbCurrentRetailRslLinkActivationTermination.LinkBudgetExhausted, plan.Termination);
        Assert.Equal(11, plan.UnresolvedOrUnfollowedLinkStateId);
    }

    [Fact]
    public void MissingResolvedLinkStopsAfterTheStateRetailAlreadyActivated()
    {
        EnbResolvedRslLibraryResourceGraph graph = ParseGraph("""
            [0\STATE0]
            ID=20
            TYPE=object
            OBJECT=FIRST
            LINK=999
            """);

        EnbCurrentRetailRslLinkActivationPlan plan =
            EnbCurrentRetailRslLinkActivationRuntime.Resolve(graph, 20);

        EnbCurrentRetailRslLinkActivationStep step = Assert.Single(plan.Steps);
        Assert.Equal(20, step.StateId);
        Assert.Equal(EnbCurrentRetailRslLinkActivationTermination.MissingLinkedTarget, plan.Termination);
        Assert.Equal(999, plan.UnresolvedOrUnfollowedLinkStateId);
    }

    [Fact]
    public void NegativeBudgetReturnsBeforeTheStartingStateIsActivated()
    {
        EnbResolvedRslLibraryResourceGraph graph = ParseGraph("""
            [0\STATE0]
            ID=30
            TYPE=object
            OBJECT=NEVER
            """);

        EnbCurrentRetailRslLinkActivationPlan plan =
            EnbCurrentRetailRslLinkActivationRuntime.Resolve(graph, 30, linkBudget: -1);

        Assert.Empty(plan.Steps);
        Assert.Equal(EnbCurrentRetailRslLinkActivationTermination.NegativeInitialBudget, plan.Termination);
    }

    private static EnbResolvedRslLibraryResourceGraph ParseGraph(string text)
    {
        EnbRetailRenderStateCatalog catalog = EnbRetailRenderStateCatalog.ParseText(text);
        Assert.True(catalog.TryGetLibrary(0, out EnbRslLibraryDefinition library));
        return new EnbRetailRslResourceResolver(resources: null).Resolve(library);
    }
}
