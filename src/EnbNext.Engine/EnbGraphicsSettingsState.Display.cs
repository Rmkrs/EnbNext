using EnbNext.Presentation;

namespace EnbNext.Engine;

internal sealed partial class EnbGraphicsSettingsState
{
    public IReadOnlyList<EnbDisplayChoice> DisplayChoices { get; private set; } = [];
    public int DisplayChoiceIndex { get; private set; }
    public EnbDisplayScope DisplayScope { get; private set; }
    public bool ResolutionDropdown { get; set; }
    public bool UiScaleDropdown {get;set;}
    public bool TextureDropdown {get;set;}
    public string?[] TexturePacks {get;set;}=[""];
    public static string TextureLabel(string? name)=>name is null?"Legacy pack":name.Length==0?"Original":name;
    public double? UiScale {get;set;}
    public double SystemScale {get;set;}=1;
    public double?[] UiScales {get;set;}=[null,1,1.25,1.5,1.75,2];
    public string ScaleLabel(double? value)=>value is {} number?$"{number*100:0}%":$"System ({SystemScale*100:0}%)";
    public bool Dropdown=>ResolutionDropdown||UiScaleDropdown||TextureDropdown;
    public int ChoiceCount=>TextureDropdown?TexturePacks.Length:UiScaleDropdown?UiScales.Length:DisplayChoices.Count;

    public void OpenDisplay(Action? begin, Func<EnbDisplayScope>? scope,
        Func<EnbDisplayChoice>? current, Func<IReadOnlyList<EnbDisplayChoice>>? choices)
    {
        begin?.Invoke();
        DisplayScope = scope?.Invoke() ?? EnbDisplayScope.System;
        ReadDisplay(current?.Invoke() ?? new(1280,720,EnbDisplayMode.Windowed), choices, opening:true);
    }

    public bool RefreshDisplay(Func<EnbDisplayChoice>? current, Func<IReadOnlyList<EnbDisplayChoice>>? choices)
    {
        if (!PreviewActive || current?.Invoke() is not { } value) return false;
        ReadDisplay(value, choices, opening:false);
        return true;
    }

    public string? SelectDisplayScope(EnbDisplayScope requested, Action<EnbDisplayScope>? select,
        Func<EnbDisplayScope>? scope, Func<EnbDisplayChoice>? current,
        Func<IReadOnlyList<EnbDisplayChoice>>? choices)
    {
        try
        {
            select?.Invoke(requested);
            DisplayScope = scope?.Invoke() ?? requested;
            ReadDisplay(current?.Invoke() ?? new(1280,720,EnbDisplayMode.Windowed), choices, opening:false);
            ResolutionDropdown = false;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { return "Could not select display settings: " + ex.Message; }
    }

    public string? PreviewDisplay(int choice, Action<EnbDisplayChoice>? preview)
    {
        try
        {
            if (preview is null) throw new InvalidOperationException("Display settings are unavailable.");
            preview(DisplayChoices[choice]);
            DisplayChoiceIndex = choice;
            ResolutionDropdown = false;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { return "Could not preview resolution: " + ex.Message; }
    }

    private void ReadDisplay(EnbDisplayChoice current, Func<IReadOnlyList<EnbDisplayChoice>>? choices, bool opening)
    {
        DisplayChoices = choices?.Invoke() ?? [current];
        if (opening && DisplayChoices.Count == 0) DisplayChoices = [current];
        DisplayChoiceIndex = Math.Max(0, Enumerable.Range(0,DisplayChoices.Count)
            .FirstOrDefault(i => DisplayChoices[i] == current));
    }
}
