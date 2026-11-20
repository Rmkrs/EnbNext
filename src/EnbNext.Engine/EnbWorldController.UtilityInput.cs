using Point = EnbNext.Presentation.EnbUiPoint;
using Rect = EnbNext.Presentation.EnbUiRect;
using Size = EnbNext.Presentation.EnbUiSize;
namespace EnbNext.Engine;

internal sealed partial class EnbWorldController
{
    private readonly Queue<string> _utilityInputHistory = new();
    private string _utilityInputDiagnostics = "";

    internal void RecordUtilityInput(string action, string edge)
    {
        _utilityInputHistory.Enqueue($"{Environment.TickCount64} {action}: {edge}; " +
            $"ready={CanUseGeneralControls} loading={_awaitingWorld} station={_stationActive} " +
            $"camera={_cameraController.ActiveModeIndex} bridge={_bridgeTransition.RequestedState}/{_bridgeTransition.CompletedState} " +
            $"options={_ui.Options.IsOpen} inventory=[{_ui.Inventory.UtilityInputState}]");
        while (_utilityInputHistory.Count > 24) _utilityInputHistory.Dequeue();
        Volatile.Write(ref _utilityInputDiagnostics, "\nUtility shortcut history (last 24 edges):\n" + string.Join('\n', _utilityInputHistory));
    }
}
