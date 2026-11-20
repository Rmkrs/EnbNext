using EnbNext.Assets;

namespace EnbNext.Net7.Tests;

public sealed class EnbR6Z27CurrentRetailRslFiniteSoundCompletionTests
{
    [Fact]
    public void OrdinarySoundRemainsContinuousAndHasNoCompletionTransition()
    {
        EnbRslStateDefinition state = ParseSoundState("""
            [0\STATE0]
            ID=6
            TYPE=sound
            SOUND=IDLE
            """);
        EnbCurrentRetailRslSoundContract sound = EnbCurrentRetailRslSoundRuntime.Describe(state.Flags);
        EnbCurrentRetailRslSoundCompletionContract completion = EnbCurrentRetailRslSoundCompletionRuntime.Describe(state, sound);
        Assert.Equal(0, sound.HandleLoopCountOverride);
        Assert.False(completion.FiniteOnePass);
        Assert.False(completion.DeactivatesCurrentOnCompletion);
        Assert.False(completion.EntersGenericTransitionOnCompletion);
        Assert.Null(completion.CompletionLinkStateId);
        Assert.False(completion.AuthoredNextIgnoredBySoundFactory);
    }

    [Fact]
    public void DontLoopSoundIsOnePassAndDeactivatesWhenTheHandleCompletes()
    {
        EnbRslStateDefinition state = ParseSoundState("""
            [0\STATE0]
            ID=6
            TYPE=sound
            SOUND=ONE_SHOT
            RS_DONT_LOOP_SOUND=true
            """);
        EnbCurrentRetailRslSoundContract sound = EnbCurrentRetailRslSoundRuntime.Describe(state.Flags);
        EnbCurrentRetailRslSoundCompletionContract completion = EnbCurrentRetailRslSoundCompletionRuntime.Describe(state, sound);
        Assert.Equal(1, sound.HandleLoopCountOverride);
        Assert.True(completion.FiniteOnePass);
        Assert.True(completion.DeactivatesCurrentOnCompletion);
        Assert.False(completion.EntersGenericTransitionOnCompletion);
        Assert.Null(completion.CompletionLinkStateId);
    }

    [Fact]
    public void ActivateNextWithoutLinkStillRunsTheGenericCompletionPathAndDeactivates()
    {
        EnbRslStateDefinition state = ParseSoundState("""
            [0\STATE0]
            ID=6
            TYPE=sound
            SOUND=ONE_SHOT
            RS_ACTIVATE_NEXT=true
            """);
        EnbCurrentRetailRslSoundContract sound = EnbCurrentRetailRslSoundRuntime.Describe(state.Flags);
        EnbCurrentRetailRslSoundCompletionContract completion = EnbCurrentRetailRslSoundCompletionRuntime.Describe(state, sound);
        Assert.Equal(1, sound.HandleLoopCountOverride);
        Assert.True(completion.FiniteOnePass);
        Assert.True(completion.DeactivatesCurrentOnCompletion);
        Assert.True(completion.EntersGenericTransitionOnCompletion);
        Assert.Null(completion.CompletionLinkStateId);
    }

    [Fact]
    public void SoundCompletionUsesAuthoredLinkAndDoesNotUseAuthoredNext()
    {
        EnbRslStateDefinition state = ParseSoundState("""
            [0\STATE0]
            ID=6
            TYPE=sound
            SOUND=ONE_SHOT
            NEXT=7
            LINK=8
            RS_ACTIVATE_NEXT=true
            """);
        EnbCurrentRetailRslSoundContract sound = EnbCurrentRetailRslSoundRuntime.Describe(state.Flags);
        EnbCurrentRetailRslSoundCompletionContract completion = EnbCurrentRetailRslSoundCompletionRuntime.Describe(state, sound);
        Assert.Equal(7, state.NextStateId);
        Assert.Equal(8, state.LinkStateId);
        Assert.True(completion.FiniteOnePass);
        Assert.True(completion.EntersGenericTransitionOnCompletion);
        Assert.Equal(8, completion.CompletionLinkStateId);
        Assert.True(completion.AuthoredNextIgnoredBySoundFactory);
    }

    [Fact]
    public void OnePassCurrentRetailSoundIsAdmittedByTheBackendNeutral3dPlan()
    {
        EnbRetailSoundDefinition definition = ParseSingleSoundDefinition("""
            [OnePass]
            SoundFilename=one-pass.wav
            """);
        EnbRslStateDefinition state = ParseSoundState("""
            [0\STATE0]
            ID=6
            TYPE=sound
            SOUND=OnePass
            RS_DONT_LOOP_SOUND=true
            """);
        EnbCurrentRetailRslSoundContract sound = EnbCurrentRetailRslSoundRuntime.Describe(state.Flags);
        Assert.True(EnbCurrentRetailRsl3dSoundRuntime.TryCreatePlan(definition, ExactLooseResolution(definition), sound,
            out EnbCurrentRetailRsl3dSoundPlan plan, out EnbRetailSoundResolvedChoice? selected, out string? diagnostic), diagnostic);
        Assert.NotNull(selected);
        Assert.False(plan.LoopContinuously);
    }

    [Fact]
    public void ResourceGraphPreservesSoundNextForArchaeologyButDoesNotTreatItAsAnEdge()
    {
        EnbRetailRenderStateCatalog catalog = EnbRetailRenderStateCatalog.ParseText("""
            [0\STATE0]
            ID=6
            TYPE=sound
            SOUND=ONE_SHOT
            NEXT=999
            """);
        Assert.True(catalog.TryGetLibrary(0, out EnbRslLibraryDefinition library));
        EnbResolvedRslLibraryResourceGraph graph = new EnbRetailRslResourceResolver(resources: null).Resolve(library);
        EnbResolvedRslStateResource sound = Assert.Single(graph.States);
        Assert.Equal(999, sound.Definition.NextStateId);
        Assert.Empty(sound.ReferencedStateIds);
        Assert.Empty(sound.MissingReferencedStateIds);
        Assert.Empty(graph.MissingReferencedStateIds);
        Assert.True(graph.IsComplete);
    }

    private static EnbRslStateDefinition ParseSoundState(string text)
    {
        EnbRetailRenderStateCatalog catalog = EnbRetailRenderStateCatalog.ParseText(text);
        Assert.True(catalog.TryGetLibrary(0, out EnbRslLibraryDefinition library));
        return Assert.Single(library.States);
    }

    private static EnbRetailSoundDefinition ParseSingleSoundDefinition(string text)
    {
        EnbRetailSoundCatalog catalog = EnbRetailSoundCatalog.ParseText(text);
        return Assert.Single(catalog.Definitions);
    }

    private static EnbRetailSoundResolution ExactLooseResolution(EnbRetailSoundDefinition definition)
    {
        EnbRetailSoundFileChoice authored = Assert.Single(definition.Files);
        return new EnbRetailSoundResolution(definition,
            [new EnbRetailSoundResolvedChoice(authored, EnbRetailSoundResourceStatus.LooseFile,
                Path.Combine(Path.GetTempPath(), authored.LogicalResourceName ?? "sound.wav"), null, [])]);
    }
}
