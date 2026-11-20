using EnbNext.Assets;
using EnbNext.Client;

namespace EnbNext.Engine;

internal enum EnbControlsNavigation { None, Main, Close }
internal readonly record struct EnbControlsCommandResult(EnbControlsNavigation Navigation = EnbControlsNavigation.None,
    bool UpdateError = false, string? Error = null);

internal sealed partial class EnbControlsPanel
{
    public EnbRetailControlOptions? Draft { get; private set; }
    public string Category { get; private set; } = "Targeting";
    public EnbRetailControlDefinition[] Rows { get; private set; } = [];
    public EnbControlCaptureState Capture { get; } = new(confirmation);
    public Action<EnbRetailControlOptions>? SaveControls { get; set; }
    public event Action<string>? SoundRequested;

    public void Begin(EnbRetailControlOptions draft)
    {
        Retire(); Draft = draft; Category = "Targeting"; SelectRows();
        ScrollInput.Configure(Rows.Length, 11); ScrollInput.SetValue(0); ResetOpening();
    }
    public void Retire() { Draft = null; Rows = []; Capture.Reset(); Keyboard.Cancel(); CancelInput(); }
    /// <summary>Modern UI is on: its own controls are listed and the classic-only ones are not.</summary>
    public bool Modern { get; set; }
    private void SelectRows() => Rows = Draft!.Definitions.Where(d => d.Context == Category && EnbNextControls.Shown(d.Action, Modern)).ToArray();
    public void SetScroll(double value)
    {
        ScrollInput.Configure(Rows.Length, 11); ScrollInput.SetValue(value); ScrollChanged();
    }
    public void ScrollChanged()
    {
        if (ScrollInput.NotchChanged) SoundRequested?.Invoke("slider_bar_1.wav");
        Capture.Reset();
    }
    private void ResetScroll()
    {
        ScrollInput.Configure(Rows.Length, 11);
        if (ScrollInput.NotchChanged) SoundRequested?.Invoke("slider_bar_1.wav");
        ScrollInput.SetValue(0);
        if (ScrollInput.NotchChanged) SoundRequested?.Invoke("slider_bar_1.wav");
        ScrollInput.Cancel();
    }
    public EnbControlsCommandResult Activate(string key)
    {
        if (Draft is not { } draft) return default;
        if (confirmation.Text is not null)
        {
            if (key is "control/dialog/yes" or "control/dialog/no") Capture.Answer(draft, key.EndsWith("/yes"));
            return default;
        }
        Capture.Reset();
        // 005CA9F0 excludes scrollbar action13 from the command selection cue.
        if (key is not ("control/UP" or "control/DOWN" or "control/TRACK" or "control/THUMB"))
            SoundRequested?.Invoke("Roll_Select02b.wav");
        if (key == "control/EXIT") { Retire(); return new(EnbControlsNavigation.Close); }
        if (key == "control/BUTTON03") { Retire(); return new(EnbControlsNavigation.Main); }
        if (key == "control/BUTTON00")
        {
            try
            {
                if (SaveControls is not { } save) throw new InvalidOperationException("A character is required to save controls.");
                save(draft); Retire(); return new(EnbControlsNavigation.Main, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            { return new(UpdateError: true, Error: "Could not save controls: " + ex.Message); }
        }
        if (key is "control/BUTTON01" or "control/BUTTON02") { draft.Default(key.EndsWith("02")); ResetScroll(); return default; }
        if (key.StartsWith("control/TOP_BUTTON"))
        {
            var label = Resources.Labels.FirstOrDefault(d => d.Attach.Equals(key[8..], StringComparison.OrdinalIgnoreCase))?.Definition;
            if (label is not null && Category != label.Properties["String0"])
            { Category = label.Properties["String0"]; SelectRows(); }
            ResetScroll(); return default;
        }
        if (key == "control/UP") { SetScroll(ScrollInput.Value - 1); return default; }
        if (key == "control/DOWN") { SetScroll(ScrollInput.Value + 1); return default; }
        if (key.Contains("_KEY") && int.TryParse(key.AsSpan(key.Length - 2), out int row) &&
            Rows.ElementAtOrDefault(ScrollInput.Offset + row) is { FixedKey: null } definition)
            Capture.Begin(definition.Action, key.Contains("PRIME") ? 1 : 2);
        return default;
    }
}
