using System.Reflection;
using System.Runtime.CompilerServices;
using EnbNext.Engine;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.World;
using EnbNext.Presentation;

namespace EnbNext.Net7.Tests;

public sealed partial class Net7ManufacturingDomainTests
{
    [Fact]
    public void ManufacturingLabelTextMatchesTheRetailOpenAndUpdateFormats()
    {
        // 005B4B10 open: "%2.0f%%-%2.0f%%" / "%2.0f"; 005B5320 update:
        // "%2.0f%% to %2.0f%%" / "%2.0f%%"; -1 -> "N/A".
        Assert.Equal("50%-120%", EnbManufacturingLabelText.Range(0.5f, 1.2f, true));
        Assert.Equal("50% to 120%", EnbManufacturingLabelText.Range(0.5f, 1.2f, false));
        Assert.Equal(" 5", EnbManufacturingLabelText.Range(0.05f, 0.05f, true));
        Assert.Equal(" 5%", EnbManufacturingLabelText.Range(0.05f, 0.05f, false));
        Assert.Equal("N/A", EnbManufacturingLabelText.Range(-1f, 1f, false));
        Assert.Equal("N/A", EnbManufacturingLabelText.Range(1f, -1f, true));
        Assert.Equal(" 0% to 300%", EnbManufacturingLabelText.Range(-0.5f, 4f, false));
        Assert.Equal("80%", EnbManufacturingLabelText.Range(1.5f, 0.8f, false)); // inverted: min = max
        Assert.Equal("80%", EnbManufacturingLabelText.Range(float.NaN, 0.8f, false));
        Assert.Equal("95", EnbManufacturingLabelText.Expected(0.95f, true));
        Assert.Equal("95%", EnbManufacturingLabelText.Expected(0.95f, false));
        Assert.Equal(" 0%", EnbManufacturingLabelText.Expected(float.NaN, false));
        Assert.Equal("300%", EnbManufacturingLabelText.Expected(7f, false));
        Assert.Equal("N/A", EnbManufacturingLabelText.Expected(-1f, false));
        Assert.Equal("13", EnbManufacturingLabelText.Expected(0.125f, true)); // 12.5 rounds away from zero
    }

    [Fact]
    public void AnalysisChancesAreWholePercentagesRetainedBetweenUpdates()
    {
        // "%1.0f%%" (00B79AB0): 00544D80 on open, 00545190 for updated values.
        Assert.Equal("81%", EnbManufacturingLabelText.Probability(0.8061453f, false));
        Assert.Equal("8%", EnbManufacturingLabelText.Probability(0.08061454f, true));
        var (world, domain) = CreateLab();
        Apply(world, ("ManufacturingLab.SuccessProbability", F32(0.8061453f)));
        var ui = (EnbInventoryUi)RuntimeHelpers.GetUninitializedObject(typeof(EnbInventoryUi));
        var frame = new EnbSceneFrame();
        var owner = new EnbStationTerminalOwner(7, 1, 0, 0, 0, 1);
        Set(ui, "_scene", frame); Set(ui, "_terminal", owner);
        void Frame() { frame.StationTerminal = EnbStationTerminalSnapshot.Build(domain, null); Call(ui, "UpdateAnalysisLabels", owner); }
        Call(ui, "ResetManufacturingLabels");
        Frame();
        Assert.Equal("81%", Get<string>(ui, "_analysisChanceText"));
        Assert.Equal("0%", Get<string>(ui, "_analysisCriticalText")); // 00544FE3: absent critical reads 0 on open (INV-D76)
        Apply(world, ("ManufacturingLab.CriticalSuccessProbability", F32(0.125f)));
        Frame();
        Assert.Equal("81%", Get<string>(ui, "_analysisChanceText"));
        Assert.Equal("13%", Get<string>(ui, "_analysisCriticalText"));
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(13, "")]
    [InlineData(17, "")]
    [InlineData(4, "Missing sufficient credits")]
    [InlineData(5, "Missing required component(s)")]
    [InlineData(6, "Missing sufficient cargo room")]
    [InlineData(7, "Missing required skill level")]
    [InlineData(8, "Too difficult")]
    [InlineData(12, "Unique item already owned")]
    [InlineData(1, "Unexpected error.")]
    [InlineData(18, "Unexpected error.")]
    public void ManufacturingFailureReasonFollows005B6060(int validity, string reason)
        => Assert.Equal(reason, EnbManufacturingLabelText.FailureReason(validity));

    [Fact]
    public void LabPropertyRevisionsAdvanceOnlyForUpdatedProperties()
    {
        var (world, domain) = CreateLab();
        Apply(world, ("ManufacturingLab.MinimumQuality", F32(0.5f)));
        var first = domain.CurrentLab!;
        Assert.Equal(2, first.MinimumQualityState);
        Assert.Equal(0, first.MaximumQualityState);
        Apply(world, ("ManufacturingLab.MaximumQuality", F32(1f)), ("ManufacturingLab.Validity", I32(13)));
        var second = domain.CurrentLab!;
        Assert.Equal(first.MinimumQualityRevision, second.MinimumQualityRevision);
        Assert.NotEqual(first.MaximumQualityRevision, second.MaximumQualityRevision);
        Assert.NotEqual(first.ValidityRevision, second.ValidityRevision);
        Assert.Equal(first.ExpectedQualityRevision, second.ExpectedQualityRevision);
    }

    [Fact]
    public void EverySelectionConsumingUpdatedComponentsAdvancesTheReadyRevision()
    {
        var (world, domain) = CreateLab();
        domain.RecordModeSent(Net7ManufacturingMode.Manufacture);
        for (int i = 1; i <= 2; i++)
        {
            domain.RecordManufactureItemSent(500u + (uint)i);
            Assert.True(domain.ManufactureSelectionPending);
            Apply(world, ("ManufacturingLab.ManufactureComponents.0.ItemTemplateID", I32(100 + i)));
            domain.ReconcileAuthoritativeState();
            Assert.Equal((ulong)i, domain.SelectionReadyRevision);
            Assert.False(domain.ManufactureSelectionPending);
        }
    }

    [Fact]
    public void ManufacturingTerminalRetainsQualityTextsAndReportsUnusableSelections()
    {
        var (world, domain) = CreateLab();
        Apply(world, ("ManufacturingLab.MinimumQuality", F32(0.5f)), ("ManufacturingLab.MaximumQuality", F32(1.2f)),
            ("ManufacturingLab.Validity", I32(13)));
        var ui = (EnbInventoryUi)RuntimeHelpers.GetUninitializedObject(typeof(EnbInventoryUi));
        var frame = new EnbSceneFrame();
        var owner = new EnbStationTerminalOwner(7, 1, 0, 0, 0, 2);
        Set(ui, "_scene", frame); Set(ui, "_terminal", owner);
        var chat = new List<string>();
        ui.LocalChatMessageRequested = (_, type, text) => { Assert.Equal((byte)0x11, type); chat.Add(text); };
        void Frame(bool result = false)
        {
            frame.StationTerminal = EnbStationTerminalSnapshot.Build(domain, null);
            Call(ui, "UpdateManufacturingLabels", owner, result);
        }
        string Range() => Get<string>(ui, "_qualityRangeText");
        string Expected() => Get<string>(ui, "_expectedQualityText");
        bool Go() => Get<bool>(ui, "_manufactureGoEnabled");

        Call(ui, "ResetManufacturingLabels");
        Frame(); // 005B4B10 + 005B2480
        Assert.Equal("50%-120%", Range());
        Assert.Equal("N/A", Expected()); // absent at open
        Assert.True(Go());
        Frame();
        Assert.Equal("50%-120%", Range()); // nothing updated: retained
        Apply(world, ("ManufacturingLab.MinimumQuality", F32(0.4f)));
        Frame();
        Assert.Equal("50%-120%", Range()); // only one side updated this frame
        Apply(world, ("ManufacturingLab.MinimumQuality", F32(0.4f)), ("ManufacturingLab.MaximumQuality", F32(1.2f)),
            ("ManufacturingLab.ExpectedQuality", F32(0.9f)));
        Frame();
        Assert.Equal("40% to 120%", Range());
        Assert.Equal("90%", Expected());
        Assert.Empty(chat);

        Apply(world, ("ManufacturingLab.Validity", I32(5)));
        Frame();
        Assert.Equal(new[] { "Unable to create Unknown Item. Missing required component(s)." }, chat);
        // 005B12B2 -> 005B2480: an idle frame refreshes Go; Validity 5 disables it
        // and clears the quality texts (INV-D52).
        Assert.Equal("", Expected());
        Assert.False(Go());

        Assert.False(Call<bool>(ui, "ManufacturingEventIgnored", EnbTerminalCommand.Action, 2u)); // Go sets +BB4
        Assert.False(Go());
        Apply(world, ("ManufacturingLab.Validity", I32(7)));
        Frame();
        Assert.Single(chat);
        Assert.False(Call<bool>(ui, "ManufacturingEventIgnored", EnbTerminalCommand.Primary, 0u)); // any other event clears it
        Apply(world, ("ManufacturingLab.Validity", I32(7)));
        Frame();
        Assert.Equal(2, chat.Count);

        Frame(result: true); // 005B2480 with Validity 7: Go off, texts cleared
        Assert.False(Go());
        Assert.Equal("", Range());
        Assert.Equal("", Expected());
    }

    [Fact]
    public void AnalysisReportsAnUpdatedValidityReasonOnce()
    {
        var (world, domain) = CreateLab();
        domain.RecordModeSent(Net7ManufacturingMode.Analyze);
        Apply(world, ("ManufacturingLab.Mode", I32(2)), ("ManufacturingLab.Validity", I32(4)));
        var ui = (EnbInventoryUi)RuntimeHelpers.GetUninitializedObject(typeof(EnbInventoryUi));
        var frame = new EnbSceneFrame();
        var owner = new EnbStationTerminalOwner(7, 1, 0, 0, 0, 1);
        Set(ui, "_scene", frame); Set(ui, "_terminal", owner);
        var chat = new List<string>();
        ui.LocalChatMessageRequested = (_, type, text) => { Assert.Equal((byte)0x11, type); chat.Add(text); };
        void Frame() { frame.StationTerminal = EnbStationTerminalSnapshot.Build(domain, null); Call(ui, "UpdateAnalysisLabels", owner); }
        Frame(); // 00544D80 opening: no report
        Assert.Empty(chat);
        Apply(world, ("ManufacturingLab.Validity", I32(4)));
        Frame(); // 0054547F: updated Validity 4
        Frame();
        Assert.Equal(new[] { "Insufficient credits to analyze or dismantle item." }, chat);
        Apply(world, ("ManufacturingLab.Validity", I32(13)));
        Frame();
        Assert.Single(chat);
    }

    [Fact]
    public void AnAnalysisEndsOnAnyMaterializedValidityOtherThanValid()
    {
        var (world, domain) = CreateLab();
        domain.RecordModeSent(Net7ManufacturingMode.Analyze);
        Apply(world, ("ManufacturingLab.Mode", I32(2)), ("ManufacturingLab.Validity", I32(13)));
        domain.RecordActionSent(2);
        Assert.Equal(Net7ManufacturingActionContext.Analyze, domain.ActionContext);
        Apply(world, ("ManufacturingLab.Validity", I32(2)));
        domain.ReconcileAuthoritativeState();
        Assert.Equal(Net7ManufacturingActionContext.Analyze, domain.ActionContext); // countdown first
        domain.AdvanceActionTimer(2000);
        domain.ReconcileAuthoritativeState();
        Assert.Equal(Net7ManufacturingActionContext.Idle, domain.ActionContext); // 005443F0: +110 = 0
        Assert.Null(domain.LastResult);
        Assert.Equal(2000u, domain.ActionCountdownMilliseconds);
    }

    [Fact]
    public void ASelectionEndsAnAttemptStillWaitingForItsResult()
    {
        var (world, domain) = CreateLab();
        domain.RecordModeSent(Net7ManufacturingMode.Refining);
        Apply(world, ("ManufacturingLab.Mode", I32(4)));
        domain.RecordRefineryItemSent(500);
        Apply(world, ("ManufacturingLab.Validity", I32(13)), ("ManufacturingLab.ManufactureComponents.0.ItemTemplateID", I32(1)));
        domain.ReconcileAuthoritativeState();
        Assert.True(EnbStationTerminalSnapshot.Build(domain, null).CanAct);
        domain.RecordActionSent(3);
        Apply(world, ("ManufacturingLab.Validity", I32(6))); // not an attempt result: +AF0 stays 3
        domain.ReconcileAuthoritativeState();
        Assert.Equal(Net7ManufacturingActionContext.Refinery, domain.ActionContext);
        domain.RecordRefineryItemSent(500); // 005ED530: +AF0 = 1
        Assert.Equal(Net7ManufacturingActionContext.Idle, domain.ActionContext);
        Apply(world, ("ManufacturingLab.Validity", I32(13)));
        domain.ReconcileAuthoritativeState();
        Assert.False(EnbStationTerminalSnapshot.Build(domain, null).CanAct); // components not yet updated
        Apply(world, ("ManufacturingLab.ManufactureComponents.0.ItemTemplateID", I32(1)));
        domain.ReconcileAuthoritativeState();
        Assert.True(EnbStationTerminalSnapshot.Build(domain, null).CanAct);
    }

    [Fact]
    public void RefineryReportsAnUnusableSelectionUnlessTheLastEventWasARefine()
    {
        var (world, domain) = CreateLab();
        Apply(world, ("ManufacturingLab.Validity", I32(13)));
        var ui = (EnbInventoryUi)RuntimeHelpers.GetUninitializedObject(typeof(EnbInventoryUi));
        var frame = new EnbSceneFrame();
        var owner = new EnbStationTerminalOwner(7, 1, 0, 0, 0, 0);
        Set(ui, "_scene", frame); Set(ui, "_terminal", owner);
        var chat = new List<string>();
        ui.LocalChatMessageRequested = (_, _, text) => chat.Add(text);
        void Frame() { frame.StationTerminal = EnbStationTerminalSnapshot.Build(domain, null); Call(ui, "UpdateRefineryReport", owner); }
        Call(ui, "ResetManufacturingLabels");
        Frame();
        Apply(world, ("ManufacturingLab.Validity", I32(4)));
        Frame();
        Assert.Equal(new[] { "Unable to create Unknown Item. Insufficient credits to refine item.." }, chat);
        Call(ui, "RefineryEvent", EnbTerminalCommand.Action, 3u);
        Apply(world, ("ManufacturingLab.Validity", I32(6)));
        Frame();
        Assert.Single(chat);
        Assert.True(Get<bool>(ui, "_refineryDeselectPending"));
        Call(ui, "RefineryEvent", EnbTerminalCommand.Formula, 500u);
        Assert.False(Get<bool>(ui, "_refineryDeselectPending"));
        Apply(world, ("ManufacturingLab.Validity", I32(1)));
        Frame();
        Assert.Equal("Unable to create Unknown Item. Unexpected error..", chat[^1]);
    }

    private static void Set(object target, string name, object? value)
        => typeof(EnbInventoryUi).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static T Get<T>(object target, string name)
        => (T)typeof(EnbInventoryUi).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Call(object target, string name, params object[] args)
        => typeof(EnbInventoryUi).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static T Call<T>(object target, string name, params object[] args)
        => (T)typeof(EnbInventoryUi).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args)!;
}
