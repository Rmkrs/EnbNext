using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbG24GraphicsOptionTests
{
    private static readonly EnbRetailRenderBudgetSourceSettings Source = EnbRetailRenderBudgetSourceSettings.RetailExecutableDefaults;
    private static EnbRetailPlayerOptions Values(float value, bool defaults = false) => new(false, defaults, value, value, value, value, value);

    [Theory]
    [InlineData(1f)]
    [InlineData(-1f)]
    [InlineData(.25f)]
    [InlineData(-.25f)]
    public void DeadbandUsesLastRetainedInputsAcrossRepeatedOffers(float delta)
    {
        var state = new EnbRenderDetailOptionState();
        var initial = state.Update(Source, Values(50));
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(initial, state.Update(Source, Values(50 + delta)));
            Assert.False(state.Recomputed);
        }
        var changed = state.Update(Source, Values(50 + MathF.CopySign(1.01f, delta)));
        Assert.True(state.Recomputed);
        Assert.Equal(EnbRenderDetailRuntimeSettings.FromRetailInputs(Source, Values(50 + MathF.CopySign(1.01f, delta))), changed);
    }

    [Fact]
    public void IndependentInputsAreNotPromotedByAnotherInputsDirtyFlag()
    {
        var state = new EnbRenderDetailOptionState();
        state.Update(Source, Values(50));
        var offered = Values(51) with { GraphicsLandPoly = 52 };
        Assert.Equal(EnbRenderDetailRuntimeSettings.FromRetailInputs(Source, Values(50) with { GraphicsLandPoly = 52 }), state.Update(Source, offered));
    }

    [Fact]
    public void DefaultModeRetainsCustomChangesWithoutApplyingThemUntilToggle()
    {
        var state = new EnbRenderDetailOptionState();
        var initial = state.Update(Source, Values(50, true));
        Assert.Equal(initial, state.Update(Source, Values(75, true)));
        Assert.True(state.InputsChanged);
        Assert.False(state.Recomputed);
        Assert.Equal(EnbRenderDetailRuntimeSettings.FromRetailInputs(Source, Values(75)), state.Update(Source, Values(76)));
        Assert.True(state.DefaultChanged);
        Assert.Equal(initial, state.Update(Source, Values(76, true)));
    }

    [Fact]
    public void StartupDeadbandAppliesBeforeForcedCustomRecomputation()
    {
        var state = new EnbRenderDetailOptionState();
        Assert.Equal(EnbRenderDetailRuntimeSettings.FromRetailInputs(Source, Values(0)), state.Update(Source, Values(1)));
        Assert.True(state.Recomputed);
        Assert.Equal(EnbRenderDetailRuntimeSettings.FromRetailInputs(Source, Values(2)), state.Update(Source, Values(2)));
    }

    [Fact]
    public void ForcedReapplyRetainsInputsAndRefreshesDefaults()
    {
        var state = new EnbRenderDetailOptionState();
        var initial = state.Update(Source, Values(50, true));
        var source = Source with { PolyBudget = 30000 };
        Assert.Equal(initial, state.Update(source, Values(51, true)));
        Assert.Equal(30000, state.Update(source, Values(51, true), force: true).PolyBudget);
        Assert.Equal(EnbRenderDetailRuntimeSettings.FromRetailInputs(source, Values(50)), state.Update(source, Values(51), force: true));
    }

    [Fact]
    public void PreviewRollbackIsAnotherOfferAndNewOwnerStartsFresh()
    {
        var state = new EnbRenderDetailOptionState();
        var initial = state.Update(Source, Values(50));
        state.Update(Source, Values(90));
        Assert.Equal(initial, state.Update(Source, Values(50)));
        Assert.Equal(initial, state.Update(Source, Values(51)));
        Assert.NotEqual(initial, new EnbRenderDetailOptionState().Update(Source, Values(51)));
        Assert.Equal(initial, state.Update(Source, Values(float.NaN)));
    }
}
