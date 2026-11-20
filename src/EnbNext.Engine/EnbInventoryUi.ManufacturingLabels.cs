using EnbNext.Net7.Gameplay;
using EnbNext.Presentation;

namespace EnbNext.Engine;

// Retail manufacturing terminal (vtable 00AF2D4C) keeps Go, Quality_Range_tsm01a
// and Expected_Quality_tsm01a as retained widget state: 005B0BB0 writes them on
// open (005B4B10, then 005B2480), 005B1230 rewrites them per frame from lab
// properties updated that frame (005B5320) and refreshes Go after a consumed
// selection, a result or the countdown (005B2480), and the 005B16C0 events
// refresh (005B2480) or reset (005B4850) them.
internal sealed partial class EnbInventoryUi
{
    private bool _manufactureLabelsOpened, _manufactureGoEnabled, _manufactureGoEvent;
    private bool _manufactureCountdownSeen;
    private string _qualityRangeText = "", _expectedQualityText = "";
    private ulong _minimumQualitySeen, _maximumQualitySeen, _expectedQualitySeen, _validitySeen, _selectionReadySeen;

    // Refinery (005EDFD0): +B05 is set by Refine/Refine Stack (005EC990 cases
    // 2/3) and cleared by every other event; a Validity update with a 005EE7F0
    // reason is reported to chat when it is clear, otherwise the selected row
    // (+AF8) becomes -1 until 005EDA10 repopulates it (shown at the next
    // 005ECE20 redraw).
    private bool _refineryReportOpened, _refineEvent, _refineryDeselectPending;
    private ulong _refineryValiditySeen;

    // Analysis (00544D80 on open, 00545190 each frame for updated values):
    // retained Chance_tsm02a / Critical_tsm02a texts. On open an absent
    // success reads 0 and an absent critical repeats the success value.
    private bool _analysisLabelsOpened;
    private string _analysisChanceText = "", _analysisCriticalText = "";
    private ulong _analysisChanceSeen, _analysisCriticalSeen, _analysisValiditySeen;

    // 0054547F..005454D9: an updated Validity reports these reasons as local type 0x11 (INV-D77).
    internal static string? AnalysisFailureReason(int validity) => validity switch
    {
        1 => "Item may not be analyzed, dismantled or manufactured.",
        2 => "Item may not be dismantled.",
        4 => "Insufficient credits to analyze or dismantle item.",
        6 => "More empty cargo space required to dismantle item.",
        7 => "Not qualified to analyze or dismantle this item.",
        8 => "Item too difficult to analyze or dismantle.",
        12 => "Dismantling may result in a duplicate unique item.",
        _ => null,
    };

    private void UpdateAnalysisLabels(EnbStationTerminalOwner owner)
    {
        if (owner.Type != 1 || Terminal is not { Lab: {} lab }) return;
        if (!_analysisLabelsOpened)
        {
            _analysisLabelsOpened = true;
            double success = lab.SuccessProbabilityState != 0 ? (double)(lab.SuccessProbability * 100.0f) : 0.0;
            // 00544FAB/00544FE3: an absent critical chance reads 0 (the raw stack store), not the success chance (INV-D76).
            double critical = lab.CriticalSuccessProbabilityState != 0 ? (double)(lab.CriticalSuccessProbability * 100.0f) : 0.0;
            _analysisChanceText = EnbManufacturingLabelText.Percent(success);
            _analysisCriticalText = EnbManufacturingLabelText.Percent(critical);
        }
        else
        {
            if (lab.ValidityRevision != _analysisValiditySeen && lab.ValidityState != 0 && AnalysisFailureReason((int)lab.Validity) is {} reason)
                LocalChatMessageRequested?.Invoke(owner.Character, 0x11, reason);
            if (lab.SuccessProbabilityRevision != _analysisChanceSeen)
                _analysisChanceText = EnbManufacturingLabelText.Probability(lab.SuccessProbability, false);
            if (lab.CriticalSuccessProbabilityRevision != _analysisCriticalSeen)
                _analysisCriticalText = EnbManufacturingLabelText.Probability(lab.CriticalSuccessProbability, false);
        }
        _analysisChanceSeen = lab.SuccessProbabilityRevision; _analysisCriticalSeen = lab.CriticalSuccessProbabilityRevision; _analysisValiditySeen = lab.ValidityRevision;
    }

    private void ResetManufacturingLabels()
    {
        _analysisLabelsOpened = false; _analysisChanceText = _analysisCriticalText = "";
        _manufactureLabelsOpened = _manufactureGoEnabled = _manufactureGoEvent = _manufactureCountdownSeen = false;
        _qualityRangeText = _expectedQualityText = "";
        _refineryReportOpened = _refineEvent = _refineryDeselectPending = false;
    }

    private void UpdateRefineryReport(EnbStationTerminalOwner owner)
    {
        if (owner.Type != 0 || Terminal is not { Lab: {} lab } state) return;
        bool updated = _refineryReportOpened && lab.ValidityRevision != _refineryValiditySeen;
        _refineryReportOpened = true; _refineryValiditySeen = lab.ValidityRevision;
        if (!updated || EnbManufacturingLabelText.RefineryFailureReason((int)lab.Validity) is not { Length: > 0 } reason) return;
        if (!_refineEvent) LocalChatMessageRequested?.Invoke(owner.Character, 0x11, EnbManufacturingLabelText.UnableToCreate(state.TargetItemName, reason));
        else _refineryDeselectPending = true;
    }

    private void RefineryEvent(EnbTerminalCommand command, uint value)
    {
        if (_terminal?.Type != 0 || command is not (EnbTerminalCommand.Formula or EnbTerminalCommand.Action)) return;
        _refineEvent = command == EnbTerminalCommand.Action && value is 2 or 3;
        if (command == EnbTerminalCommand.Formula) _refineryDeselectPending = false;
    }

    private void UpdateManufacturingLabels(EnbStationTerminalOwner owner, bool resultPresented)
    {
        if (owner.Type != 2 || Terminal is not { Lab: {} lab } state) return;
        bool countdown = state.ActionCountdownMilliseconds > 0;
        if (!_manufactureLabelsOpened)
        {
            _manufactureLabelsOpened = true;
            _qualityRangeText = lab.MinimumQualityState != 0 && lab.MaximumQualityState != 0
                ? EnbManufacturingLabelText.Range(lab.MinimumQuality, lab.MaximumQuality, true) : EnbManufacturingLabelText.Unavailable;
            _expectedQualityText = lab.ExpectedQualityState != 0
                ? EnbManufacturingLabelText.Expected(lab.ExpectedQuality, true) : EnbManufacturingLabelText.Unavailable;
            SeeManufacturingLab(lab, state);
            _manufactureCountdownSeen = countdown;
            RefreshManufacturingGo(state);
            return;
        }
        bool minimum = lab.MinimumQualityRevision != _minimumQualitySeen, maximum = lab.MaximumQualityRevision != _maximumQualitySeen;
        bool expected = lab.ExpectedQualityRevision != _expectedQualitySeen, validity = lab.ValidityRevision != _validitySeen;
        bool ready = state.SelectionReadyRevision != _selectionReadySeen, expired = _manufactureCountdownSeen && !countdown;
        SeeManufacturingLab(lab, state);
        _manufactureCountdownSeen = countdown;
        // 005B5320: +BB4 (set by Go, cleared by every other event) silences the report.
        if (validity && !_manufactureGoEvent && EnbManufacturingLabelText.FailureReason((int)lab.Validity) is { Length: > 0 } reason)
            LocalChatMessageRequested?.Invoke(owner.Character, 0x11, EnbManufacturingLabelText.UnableToCreate(state.TargetItemName, reason));
        if (minimum && maximum) _qualityRangeText = EnbManufacturingLabelText.Range(lab.MinimumQuality, lab.MaximumQuality, false);
        if (expected) _expectedQualityText = EnbManufacturingLabelText.Expected(lab.ExpectedQuality, false);
        // 005B12B2..12B4 -> 005B14B0: with no countdown and +BA4 == 0 every update
        // refreshes Go, so an idle validity change is reflected (INV-D52).
        if (ready || resultPresented || expired || !countdown && !state.SelectionPending && state.ActionContext == Net7ManufacturingActionContext.Idle) RefreshManufacturingGo(state);
    }

    private void SeeManufacturingLab(Net7ManufacturingLabProjection lab, EnbStationTerminalSnapshot state)
    {
        _minimumQualitySeen = lab.MinimumQualityRevision; _maximumQualitySeen = lab.MaximumQualityRevision;
        _expectedQualitySeen = lab.ExpectedQualityRevision; _validitySeen = lab.ValidityRevision;
        _selectionReadySeen = state.SelectionReadyRevision;
    }

    // 005B2480: Go is enabled only with no pending selection or attempt
    // (+BA4 == 0) and Validity 13; otherwise both quality texts are cleared.
    private void RefreshManufacturingGo(EnbStationTerminalSnapshot? state, bool selectionSent = false)
    {
        _manufactureGoEnabled = !selectionSent && state?.Lab?.Validity == Net7ManufacturingValidity.Valid &&
            state.ActionContext == Net7ManufacturingActionContext.Idle && !state.SelectionPending;
        if (!_manufactureGoEnabled) _qualityRangeText = _expectedQualityText = "";
    }

    // 005B4850 (after 005B2480): ManufactureAction 0 is queued by the command;
    // Go is disabled and both quality texts cleared.
    private void ResetManufacturingSelection()
    {
        _manufactureGoEnabled = false;
        _qualityRangeText = _expectedQualityText = "";
    }

    // 005B16C0: events are ignored while the countdown (+BA8) runs; otherwise
    // +BB4 is cleared, except by Go (case 6) which sets it and disables Go
    // (ExecuteSelectedManufacturingFormula 005B3A70).
    private bool ManufacturingEventIgnored(EnbTerminalCommand command, uint value)
    {
        if (_terminal?.Type != 2 || command is not (EnbTerminalCommand.Browse or EnbTerminalCommand.Primary or EnbTerminalCommand.Category or
            EnbTerminalCommand.Subcategory or EnbTerminalCommand.Formula or EnbTerminalCommand.Action or EnbTerminalCommand.Recent)) return false;
        if (ManufacturingExitBlocked) return true;
        bool go = command == EnbTerminalCommand.Action && value == 2;
        _manufactureGoEvent = go;
        if (go) _manufactureGoEnabled = false;
        return false;
    }
}
