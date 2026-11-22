using EnbNext.Assets;
using EnbNext.Presentation;
using System.Globalization;

namespace EnbNext.Engine;

internal enum EnbModernOptionKind { Heading, Check, Slider, Choice, Button, Keys }

/// <summary>
/// One line of the Next Modern UI Options window: a heading, a check, a stepped slider, a choice from a list
/// (<see cref="Choices"/>, the chosen one at <see cref="Value"/>, shown as <see cref="ValueText"/>), a button or a row of
/// buttons (their labels in <see cref="Choices"/>, the tooltip in <see cref="ValueText"/>), or a control's two keys
/// (<see cref="Choices"/>: primary and alternate; <see cref="Value"/>: the slot waiting for a new key). A heading with
/// <see cref="Choices"/> names the key columns. A check can carry a second check at the right of its line
/// (<see cref="SideId"/>, <see cref="SideLabel"/>, <see cref="SideEnabled"/>, <see cref="SideOn"/>).
/// </summary>
internal sealed record EnbModernOptionRow(string Id, EnbModernOptionKind Kind, string Label, int Indent = 0, bool Enabled = true, bool On = false,
    int Value = 0, int Steps = 0, string ValueText = "", IReadOnlyList<string>? Choices = null,
    string? SideId = null, string SideLabel = "", bool SideEnabled = false, bool SideOn = false);

/// <summary>
/// Next Modern UI Options: one window with the categories in a sidebar. Each category uses the retail (or Next) settings
/// owner as the classic pages do: the same draft, live preview and save or rollback (camera, controls, interface, sound,
/// graphics, chat fonts and the Next sections). Changes show while the category is open; leaving it for another category or OK keeps them, Cancel
/// (or closing the window) restores the opening values.
/// </summary>
internal sealed partial class EnbOptionsUi
{
    private string _modernCategory = "";
    /// <summary>The Modern UI Options window opened, closed or changed category.</summary>
    public event Action? ModernOptionsChanged;
    internal bool ModernOptionsOpen => _modernCategory.Length > 0;
    internal string ModernOptionsCategory => _modernCategory;
    internal string? ModernOptionsError { get; private set; }
    // The menu labels and the open category's labels, each read once from its retail ini while it is in use.
    private (string? File, EnbInterfaceLayoutCatalog? Layout) _modernMenuLabels, _modernPageLabels;
    // Keys for a control waiting for its new key: the classic page's native event ring and keyboard gadget.
    private readonly EnbUiInputQueue _modernKeys = new();

    /// <summary>A retail Options label (by its Attach name in an fa_opt ini), trimmed.</summary>
    private string OptionLabel(string file, string attach, string fallback)
    {
        if (_assets is null) return fallback;
        ref var labels = ref file.Equals("fa_opt01a.ini", StringComparison.OrdinalIgnoreCase) ? ref _modernMenuLabels : ref _modernPageLabels;
        if (!file.Equals(labels.File, StringComparison.OrdinalIgnoreCase))
        {
            EnbInterfaceLayoutCatalog? loaded;
            try { loaded = EnbInterfaceLayoutCatalog.Load(Path.Combine(_assets.Paths.ClientIniDirectory, file)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { loaded = null; }
            labels = (file, loaded);
        }
        if (labels.Layout is not { } layout) return fallback;
        foreach (var definition in layout.Definitions)
            if (definition.Properties.GetValueOrDefault("Attach", "").Trim().Equals(attach, StringComparison.OrdinalIgnoreCase) &&
                definition.Properties.GetValueOrDefault("String0", "").Trim() is { Length: > 0 } text)
                return text;
        return fallback;
    }

    internal string ModernOptionsTitle => OptionLabel("fa_opt01a.ini", "help text", "Game Options");
    internal IReadOnlyList<(string Key, string Label)> ModernOptionCategories =>
    [
        ("camera", OptionLabel("fa_opt01a.ini", "bttn00", "Camera")), ("controls", OptionLabel("fa_opt01a.ini", "bttn01", "Control")),
        ("interface", OptionLabel("fa_opt01a.ini", "bttn02", "Interface")), ("graphics", OptionLabel("fa_opt01a.ini", "bttn03", "Graphics")),
        ("advanced", "Advanced Graphics"), ("sound", OptionLabel("fa_opt01a.ini", "bttn04", "Sound")), ("fonts", OptionLabel("fa_opt01a.ini", "bttn06", "Fonts")),
        ("social", "Social"), ("navigation", "Navigation"), ("quality", "Quality of Life"), ("modern", "Modern UI"), ("labels", "Object Labels"),
        ("credits", OptionLabel("fa_opt01a.ini", "bttn05", "Credits")),
    ];

    /// <summary>Opens the Modern UI Options window at a category (the classic menu, if one is open, closes).</summary>
    internal bool OpenModernOptions(string category = "camera")
    {
        if (!_interactive || _assets is null || _owner is not > 0 || _owner != session()) return false;
        if (ModernOptionsOpen) return true;
        if (IsOpen) CloseAll();
        if (!BeginModernCategory(category)) return false;
        Opened?.Invoke(); ModernOptionsChanged?.Invoke(); return true;
    }

    private bool BeginModernCategory(string key)
    {
        ModernOptionsError = null;
        try
        {
            switch (key)
            {
                case "camera": _cameraState.Begin(_assets!.ResolvePlayerOptions(_owner).Options); break;
                case "interface": BeginModernSettings(7, EnbSettingsState.RetailSection); break;
                case "sound": BeginModernSettings(8, EnbSettingsState.RetailSection); break;
                case "social": BeginModernSettings(7, EnbSettingsState.SocialSection); break;
                case "navigation": BeginModernSettings(7, EnbSettingsState.NavigationSection); break;
                case "quality": BeginModernSettings(7, EnbSettingsState.QualitySection); break;
                case "modern": BeginModernSettings(7, EnbSettingsState.ModernSection); break;
                case "labels": BeginModernSettings(7, EnbSettingsState.LabelsSection); break;
                case "graphics" or "advanced": BeginGraphics(key == "advanced"); break;
                case "fonts": BeginModernSettings(9, EnbSettingsState.RetailSection); break;
                case "controls": BeginControls(); break;
                default: return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        { ModernOptionsError = "Could not open settings: " + ex.Message; return false; }
        _modernCategory = key;
        return true;
    }

    /// <summary>The classic settings page's draft (OpenSettings) without its panel: the same owner, preview and Next state.</summary>
    private void BeginModernSettings(int page, int section)
    {
        if (_owner is not uint owner || owner == 0) throw new InvalidOperationException("A character is required for settings.");
        _settings.Begin(owner, page, (int)(_size.Width * _scaling), (int)(_size.Height * _scaling), SplitChatRequested?.Invoke() != false, section);
        _settings.Multibox = MultiboxRequested?.Invoke() ?? new(); _settings.SaveMultibox = SaveMultibox;
        _settings.QualityOfLife = QualityOfLifeRequested?.Invoke() ?? new();
        _settings.SaveQualityOfLife = SaveQualityOfLife; _settings.PreviewQualityOfLife = PreviewQualityOfLife; _settings.BeginQualityPreview();
        _settings.DiscordPresence = DiscordPresenceRequested?.Invoke() ?? new();
        _settings.SaveDiscordPresence = SaveDiscordPresence;
    }

    private bool SaveModernCategory()
    {
        bool saved; string? error;
        if (_modernCategory == "camera") saved = _cameraState.TrySave(SaveCinematicOptions, out error);
        else if (_modernCategory is "graphics" or "advanced") { saved = ApplyGraphics(); error = _graphicsState.Error; }
        else if (_modernCategory == "controls")
        {
            // The classic page's OK (BUTTON00): the draft is saved for the character and retired.
            try
            {
                if (_controlsPanel?.SaveControls is not { } save || _controlsPanel.Draft is not { } draft) throw new InvalidOperationException("A character is required to save controls.");
                save(draft); _controlsPanel.Retire(); saved = true; error = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            { saved = false; error = "Could not save controls: " + ex.Message; }
        }
        else { saved = _settings.TrySave(SaveInterfaceSettings, SaveSoundSettings, SaveFontSettings); error = _settings.Error; }
        ModernOptionsError = saved ? null : error;
        return saved;
    }

    private void EndModernOptions()
    {
        _modernCategory = ""; ModernOptionsError = null;
        ModernOptionsChanged?.Invoke(); Publish();
    }

    /// <summary>Another category: the one left is kept (saved), as OK on its classic page keeps it.</summary>
    internal void SelectModernCategory(string key)
    {
        if (!ModernOptionsOpen || key == _modernCategory) return;
        if (!SaveModernCategory()) { ModernOptionsChanged?.Invoke(); return; }
        InteractionSoundRequested?.Invoke("Roll_Select02b.wav");
        if (key == "credits") { EndModernOptions(); OpenInformationBrowser(5, "Credits"); return; }
        if (!BeginModernCategory(key)) { string? error = ModernOptionsError; EndModernOptions(); Error = error; return; }
        ModernOptionsChanged?.Invoke();
    }

    /// <summary>OK: keeps the open category's changes and closes; a failed save keeps the window open with the reason.</summary>
    internal bool ModernOptionsOk()
    {
        if (!ModernOptionsOpen) return true;
        if (!SaveModernCategory()) { ModernOptionsChanged?.Invoke(); return false; }
        InteractionSoundRequested?.Invoke("Roll_Select02b.wav");
        EndModernOptions(); return true;
    }

    /// <summary>Cancel (or closing the window): the open category's opening values are restored.</summary>
    internal void ModernOptionsCancel()
    {
        if (!ModernOptionsOpen) return;
        RetireModernCategory();
        InteractionSoundRequested?.Invoke("Roll_Select02b.wav");
        EndModernOptions();
    }

    /// <summary>The open category's opening values back, as its classic Cancel restores them.</summary>
    private void RetireModernCategory()
    {
        if (_modernCategory is "graphics" or "advanced") RollbackGraphicsPreview();
        else if (_modernCategory == "controls") { _controlsPanel?.Retire(); _modernKeys.Clear(); }
        else if (_modernCategory != "camera") _settings?.Retire();
    }

    /// <summary>A reason from the classic graphics page (preview, display or texture choice) shown in the window instead.</summary>
    private void TakeGraphicsError()
    {
        ModernOptionsError = Error ?? _graphicsState.Error;
        Error = null;
    }

    internal IReadOnlyList<EnbModernOptionRow> ModernOptionRows()
    {
        var rows = new List<EnbModernOptionRow>();
        // Next: a section's settings are each character's own or one set for all characters (every character, old and new).
        if (ScopeSection(_modernCategory) is { } scope && SettingsScope?.Invoke(scope.Key) is bool all)
            rows.Add(new("scope/" + scope.Key, EnbModernOptionKind.Choice, scope.Label, Value: all ? 1 : 0,
                ValueText: all ? "All characters" : "Each character", Choices: ["Each character", "All characters"]));
        switch (_modernCategory)
        {
            case "camera":
                string[] cameras = ["Always Keep Ship in View", "Warping Camera", "Looting Camera", "Gating Camera", "Landing Camera", "Docking Camera", "Bridge Camera"];
                for (int i = 0; i < cameras.Length; i++)
                    rows.Add(new("camera/" + i.ToString(CultureInfo.InvariantCulture), EnbModernOptionKind.Check,
                        OptionLabel("fa_opt03a-camera.ini", "text0" + i.ToString(CultureInfo.InvariantCulture), cameras[i]), On: _cameraState[i]));
                break;
            case "interface":
                rows.Add(SliderRow("slider/0", OptionLabel("fa_opt04a-interface.ini", "text01", "Tool Tip Delay"), 0));
                break;
            case "sound":
                rows.Add(SliderRow("slider/0", OptionLabel("fa_opt04a-sound.ini", "text01", "Music Volume"), 0));
                rows.Add(SliderRow("slider/1", OptionLabel("fa_opt04a-sound.ini", "text02", "Sound Effects"), 1));
                rows.Add(SliderRow("slider/2", OptionLabel("fa_opt04a-sound.ini", "text03", "Dialog Volume"), 2));
                rows.Add(new("doppler", EnbModernOptionKind.Check, OptionLabel("fa_opt04a-sound.ini", "text00", "Doppler Enabled"), On: _settings.Check));
                NextRows(rows);
                break;
            case "graphics":
            {
                const string ini = "fa_opt04a-graphics.ini";
                var state = _graphicsState;
                rows.Add(new("graphics/CHECK00", EnbModernOptionKind.Check, OptionLabel(ini, "text00", "Use recommended values"), On: state.Recommended));
                string[] names = ["Gamma Control", "Land Poly Budget", "Land Detail Level", "Space Poly Budget", "Texture Size"];
                for (int i = 0; i < names.Length; i++)
                {
                    int value = (int)Math.Round(state.Sliders[i].Value);
                    rows.Add(new("graphics/slider/" + i.ToString(CultureInfo.InvariantCulture), EnbModernOptionKind.Slider,
                        OptionLabel(ini, "text0" + (i + 1).ToString(CultureInfo.InvariantCulture), names[i]), Enabled: !state.Recommended,
                        Value: value, Steps: (int)state.Sliders[i].Maximum + 1, ValueText: value.ToString(CultureInfo.InvariantCulture)));
                }
                var displays = state.DisplayChoices;
                rows.Add(new("graphics/resolution", EnbModernOptionKind.Choice, OptionLabel(ini, "resolution", "Resolution"), Value: state.DisplayChoiceIndex,
                    ValueText: state.DisplayChoiceIndex < displays.Count ? displays[state.DisplayChoiceIndex].Label : "", Choices: [.. displays.Select(d => d.Label)]));
                string character = GraphicsCharacterDisplayName();
                rows.Add(new("graphics/scope", EnbModernOptionKind.Choice, "Display settings for", Value: state.DisplayScope == EnbDisplayScope.System ? 0 : 1,
                    ValueText: state.DisplayScope == EnbDisplayScope.System ? "System" : character, Choices: ["System", character]));
                // Next: the graphics card, where there is more than one. Windows changes it at once, like the
                // resolution; Linux at the next start.
                if (state.Adapters.Length > 2)
                    rows.Add(new("graphics/adapter", EnbModernOptionKind.Choice, "Graphics card", Value: state.AdapterIndex,
                        ValueText: state.AdapterLabel, Choices: [.. state.Adapters.Select(a => a.Label)]));
                break;
            }
            case "advanced":
            {
                var state = _graphicsState;
                var enhanced = state.Enhanced;
                rows.Add(new("graphics/ENHANCED", EnbModernOptionKind.Check, "Enhanced Graphics", On: enhanced.Enabled));
                rows.Add(new("graphics/BLOOM", EnbModernOptionKind.Check, "Bloom", 1, enhanced.Enabled, enhanced.Bloom));
                rows.Add(new("graphics/HDR", EnbModernOptionKind.Check, "HDR & Tone Mapping", 1, enhanced.Enabled, enhanced.Hdr));
                rows.Add(new("graphics/SUPERSAMPLING", EnbModernOptionKind.Check, "Supersampling (200%)", 1, enhanced.Enabled, enhanced.Supersampling));
                rows.Add(new("graphics/ANISOTROPIC", EnbModernOptionKind.Check, "Anisotropic Filtering (16x)", 1, enhanced.Enabled, enhanced.AnisotropicFiltering));
                rows.Add(new("graphics/BGCAP", EnbModernOptionKind.Check, "Background frame rate cap", On: state.BackgroundCapEnabled));
                int cap = (int)Math.Round(state.Background.Value);
                rows.Add(new("graphics/bgcap", EnbModernOptionKind.Slider, "Frames per second", 1, state.BackgroundCapEnabled, Value: cap,
                    Steps: EnbGraphicsSettingsState.BackgroundCaps.Length, ValueText: state.BackgroundCap.ToString(CultureInfo.InvariantCulture)));
                rows.Add(new("graphics/uiscale", EnbModernOptionKind.Choice, "UI Scale", Value: Array.IndexOf(state.UiScales, state.UiScale),
                    ValueText: state.ScaleLabel(state.UiScale), Choices: [.. state.UiScales.Select(state.ScaleLabel)]));
                rows.Add(new("graphics/textures", EnbModernOptionKind.Choice, "Textures", Value: Array.IndexOf(state.TexturePacks, state.TextureOverrides),
                    ValueText: EnbGraphicsSettingsState.TextureLabel(state.TextureOverrides), Choices: [.. state.TexturePacks.Select(EnbGraphicsSettingsState.TextureLabel)]));
                break;
            }
            case "controls":
                ControlRows(rows);
                break;
            case "fonts":
            {
                const string ini = "fc_option_font.ini";
                var fonts = _settings.Fonts;
                if (fonts.Length == 0) break;
                var font = fonts[Math.Clamp(_settings.FontIndex, 0, fonts.Length - 1)];
                if (fonts.Length > 1)
                {
                    // Split chat: the player chat and the game messages can each have their own font.
                    string[] windows = ["Player Chat Font", "Game Message Font"];
                    rows.Add(new("fonts/same", EnbModernOptionKind.Check, OptionLabel(ini, "text00", "Use same font in each window").TrimEnd('.'), On: _settings.Check));
                    rows.Add(new("fonts/window", EnbModernOptionKind.Choice, OptionLabel(ini, "text01", "Window"), Enabled: !_settings.Check, Value: _settings.FontIndex,
                        ValueText: windows[Math.Clamp(_settings.FontIndex, 0, 1)], Choices: windows));
                }
                // The retail font files by name and size: font_small11 reads "Small 11".
                static string FontName(string? resource)
                {
                    string name = Path.GetFileNameWithoutExtension(resource ?? "").Replace("font_", "", StringComparison.Ordinal);
                    int digits = name.Length;
                    while (digits > 0 && char.IsAsciiDigit(name[digits - 1])) digits--;
                    string face = name[..digits] switch { "med" => "Medium", var other => other.Length > 0 ? char.ToUpperInvariant(other[0]) + other[1..] : other };
                    return digits < name.Length && digits > 0 ? face + " " + name[digits..] : face.Length > 0 ? face : name;
                }
                rows.Add(new("fonts/font", EnbModernOptionKind.Choice, OptionLabel(ini, "text02", "Select Font"), Value: (int)Math.Round(_settings.Sliders[1].Value),
                    ValueText: FontName(font.Resource), Choices: [.. (_assets?.Fonts?.AvailableFonts ?? []).Select(FontName)]));
                rows.Add(FontSlider("fonts/scale", OptionLabel(ini, "text03", "Scale Font"), 2, font.Scale.ToString("0.0", CultureInfo.InvariantCulture)));
                rows.Add(FontSlider("fonts/spacing", OptionLabel(ini, "text04", "Letter Spacing"), 3, font.Spacing.ToString(CultureInfo.InvariantCulture)));
                rows.Add(FontSlider("fonts/line", OptionLabel(ini, "text05", "Line Spacing"), 4, font.LineSpacing.ToString(CultureInfo.InvariantCulture)));
                rows.Add(new("fonts/default", EnbModernOptionKind.Button, OptionLabel(ini, "resolution", "Default"), ValueText: "Restore the default font for this window."));
                break;
            }
            default:
                NextRows(rows);
                break;
        }
        return rows;
    }

    /// <summary>
    /// The classic Control page in rows: the key group, the two default layouts, then each control of the group with its
    /// primary and alternate key (fixed keys shown and not editable). A key waiting for its new key shows what is held.
    /// </summary>
    private void ControlRows(List<EnbModernOptionRow> rows)
    {
        if (_controlsPanel is not { Draft: { } draft } panel) return;
        string Label(string attach, string fallback) =>
            panel.Resources.Labels.FirstOrDefault(l => l.Attach.Equals(attach, StringComparison.OrdinalIgnoreCase))?.Definition.Properties.GetValueOrDefault("String0", "").Trim() is { Length: > 0 } text ? text : fallback;
        string[] groups = [.. panel.Resources.Labels.Where(l => l.Attach.StartsWith("TOP_BUTTON", StringComparison.OrdinalIgnoreCase))
            .Select(l => l.Definition.Properties.GetValueOrDefault("String0", "").Trim())];
        rows.Add(new("controls/group", EnbModernOptionKind.Choice, Label("LABEL_TITLEL", "Key Controls"), Value: Array.IndexOf(groups, panel.Category),
            ValueText: panel.Category, Choices: groups));
        rows.Add(new("controls/default", EnbModernOptionKind.Button, "", Choices: [Label("BUTTON01", "Default PSW"), Label("BUTTON02", "Default FPS")],
            ValueText: "Replace every key with this default layout."));
        rows.Add(new("controls/columns", EnbModernOptionKind.Heading, "", Choices: [Label("LABEL_TITLEM", "Primary"), Label("LABEL_TITLER", "Alternate")]));
        foreach (var definition in panel.Rows)
        {
            int waiting = panel.Capture.Action == definition.Action && _confirmation.Text is null ? panel.Capture.Slot : 0;
            string Key(int slot) => definition.FixedKey is { } fixedKey ? slot == 1 ? fixedKey : ""
                : waiting == slot ? panel.Keyboard.Enabled && panel.Keyboard.Preview != "????" ? panel.Keyboard.Preview : "Press a key"
                : draft.Binding(definition.Action, slot)?.Display ?? "";
            rows.Add(new("controls/key/" + definition.Action, EnbModernOptionKind.Keys, definition.Text, Enabled: definition.FixedKey is null, Value: waiting,
                Choices: [Key(1), Key(2)]));
        }
    }

    /// <summary>A click on a control's key: it waits for its new key (a second click stops waiting).</summary>
    internal void ModernOptionKey(string action, int slot)
    {
        if (_modernCategory != "controls" || _controlsPanel is not { Draft: not null } panel || _confirmation.Text is not null || slot is not (1 or 2)) return;
        if (panel.Rows.FirstOrDefault(d => d.Action == action) is not { FixedKey: null }) return;
        if (panel.Capture.Action == action && panel.Capture.Slot == slot) { panel.Capture.Reset(); panel.Keyboard.Cancel(); }
        else { panel.Capture.Begin(action, slot); panel.Keyboard.Begin(); _modernKeys.Clear(); }
        InteractionSoundRequested?.Invoke("Roll_Select02b.wav");
    }

    /// <summary>The question or notice of a key change (a key already in use, clearing a key, a key that is not allowed).</summary>
    internal (string Text, bool Notice)? ModernOptionsQuestion => _modernCategory == "controls" && _confirmation.Text is { } text ? (text, _confirmation.Notice) : null;

    /// <summary>Whether a settings section ("game", "navigation", "chat") is one set for all characters (null: no character).</summary>
    internal Func<string, bool?>? SettingsScope { get; set; }
    /// <summary>Makes a section one set for all characters (from the character in game) or each character's own again.</summary>
    internal Action<string, bool>? SettingsScopeRequested { get; set; }
    private static (string Key, string Label)? ScopeSection(string category) => category switch
    {
        "camera" or "interface" or "fonts" => ("game", "Game options for"),
        "navigation" => ("navigation", "Navigation and UI layout for"),
        "modern" => ("chat", "Chat tabs for"),
        _ => null,
    };

    internal void ModernOptionAnswer(bool yes)
    {
        if (_modernCategory != "controls" || _controlsPanel?.Draft is not { } draft || _confirmation.Text is null) return;
        _controlsPanel.Capture.Answer(draft, yes); _controlsPanel.Keyboard.Cancel(); _modernKeys.Clear();
        InteractionSoundRequested?.Invoke("Roll_Select02b.wav");
        ModernOptionsChanged?.Invoke();
    }

    /// <summary>
    /// Keys while a control waits for its new key: the classic page's event ring and keyboard gadget decide the key and its
    /// modifiers (taken when the key is released; a modifier alone is refused), then the classic capture assigns or asks.
    /// While a question is open, Enter answers yes (or closes a notice) and Escape answers no; other keys wait.
    /// </summary>
    private bool ModernControlKey(ushort code, ushort modifiers)
    {
        if (_modernCategory != "controls" || _controlsPanel is not { Draft: { } draft } panel) return false;
        if (_confirmation.Text is not null)
        {
            if ((modifiers & 0x1000) == 0 && code is 0x0d or 0x1b) ModernOptionAnswer(code == 0x0d);
            return true;
        }
        if (panel.Capture.Action is null || !panel.Keyboard.Enabled) return false;
        _modernKeys.Push(new(code, modifiers, 0, 0));
        var input = _modernKeys.TakeFrame();
        var frame = new EnbNext.Rendering.EnbUiPointerFrame(input.Code, input.Modifiers, false, false);
        panel.Keyboard.Handle(ref frame, frame.GadgetFlags, _modernKeys.HeldModifiers);
        if (!panel.Keyboard.Enabled) { panel.Capture.Complete(draft, panel.Keyboard.Code, panel.Keyboard.Modifiers); _modernKeys.Clear(); }
        ModernOptionsChanged?.Invoke();
        return true;
    }

    private EnbModernOptionRow FontSlider(string id, string label, int row, string text)
        => new(id, EnbModernOptionKind.Slider, label, Value: (int)Math.Round(_settings.Sliders[row].Value), Steps: _settings.Range(row), ValueText: text);

    private EnbModernOptionRow SliderRow(string id, string label, int row)
    {
        int value = (int)Math.Round(_settings.Sliders[row].Value);
        return new(id, EnbModernOptionKind.Slider, label, Value: value, Steps: _settings.Range(row), ValueText: value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>The Next rows of the open section, in the classic page's order; its dependent slider as a slider.</summary>
    private void NextRows(List<EnbModernOptionRow> rows)
    {
        var panel = _settingsPanel;
        foreach (var row in panel.Rows)
        {
            string key = row.Key;
            bool enabled = panel.RowEnabled(key);
            if (key == panel.NextSliderKey)
            {
                var q = _settings.QualityOfLife;
                rows.Add(key switch
                {
                    "settings/QOL16" => new(key, EnbModernOptionKind.Slider, "Arrow size", row.Indent, enabled,
                        Value: (q.ArrowSizePercent - EnbNext.Presentation.EnbQualityOfLifeOptions.ArrowSizeMinimum) / EnbNext.Presentation.EnbQualityOfLifeOptions.ArrowSizeStep,
                        Steps: EnbSettingsPanel.ArrowSizeSteps, ValueText: q.ArrowSizePercent.ToString(CultureInfo.InvariantCulture) + "%"),
                    "settings/QOL25" => new(key, EnbModernOptionKind.Slider, "Chat text size", row.Indent, enabled,
                        Value: q.ChatTextSize - EnbNext.Presentation.EnbQualityOfLifeOptions.ChatTextMinimum, Steps: EnbSettingsPanel.ChatTextSteps,
                        ValueText: q.ChatTextSize.ToString(CultureInfo.InvariantCulture)),
                    _ => new(key, EnbModernOptionKind.Slider, "Encounter pause", row.Indent, enabled,
                        Value: Math.Clamp(_settings.Multibox.EncounterPauseSeconds, 1, 60) - 1, Steps: 60,
                        ValueText: _settings.Multibox.EncounterPauseSeconds.ToString(CultureInfo.InvariantCulture) + " s"),
                });
                continue;
            }
            rows.Add(new(key, EnbModernOptionKind.Check, panel.RowText(key), row.Indent, enabled, panel.RowChecked(key)));
            // Under free station movement: how fast the mouse turns the view.
            if (key == "settings/QOL42")
            {
                var q = _settings.QualityOfLife; int speed = q.LookSpeedPercent;
                rows.Add(new("station/look", EnbModernOptionKind.Slider, "Mouse look speed", row.Indent + 1, q.StationFreeMovement,
                    Value: (speed - EnbNext.Presentation.EnbQualityOfLifeOptions.LookSpeedMinimum) / EnbNext.Presentation.EnbQualityOfLifeOptions.LookSpeedStep,
                    Steps: LookSpeedSteps, ValueText: speed.ToString(CultureInfo.InvariantCulture) + "%"));
            }
            // Modern UI: on each label category's line, whether its icon still shows. Only a category whose label is on can
            // drop its icon; with its label off the icon stays (the check shows it on and cannot be changed).
            if (_modernCategory == "labels" && key.StartsWith("settings/QOL", StringComparison.Ordinal) &&
                int.TryParse(key.AsSpan(12), NumberStyles.None, CultureInfo.InvariantCulture, out int labelRow) &&
                labelRow >= 27 && labelRow < 27 + EnbNext.Presentation.EnbObjectLabels.Count)
            {
                int kind = labelRow - 27; var q = _settings.QualityOfLife;
                bool labelled = q.AlwaysShowLabels && EnbNext.Presentation.EnbObjectLabels.Shows(q.LabelKinds, kind);
                rows[^1] = rows[^1] with { SideId = "labels/icon/" + kind.ToString(CultureInfo.InvariantCulture), SideLabel = "Show its icon",
                    SideEnabled = q.ModernUi && labelled, SideOn = !labelled || !EnbNext.Presentation.EnbObjectLabels.Shows(q.HiddenIconKinds, kind) };
            }
        }
        // Modern UI: the gutter rail at the screen edges (off, only what is in view shows; Local lists the rest).
        if (_modernCategory == "modern")
        {
            var q = _settings.QualityOfLife;
            rows.Add(new("modern/rail", EnbModernOptionKind.Check, "Gutter rail at the screen edges", 1, q.ModernUi, !q.HideGutterRail));
            rows.Add(new("modern/chat", EnbModernOptionKind.Heading, "Chat"));
            rows.Add(new("modern/chattimes", EnbModernOptionKind.Check, "Show the time of each message", 1, q.ModernUi, q.ChatTimes));
            rows.Add(new("modern/chathistory", EnbModernOptionKind.Check, "Keep chat history between sessions", 1, q.ModernUi, q.ChatKeepHistory));
            rows.Add(new("modern/chatlogs", EnbModernOptionKind.Check, "Save a chat log of every session", 1, q.ModernUi, q.ChatSaveLogs));
            rows.Add(new("modern/chatfolder", EnbModernOptionKind.Button, "Open chat logs folder", 1, q.ModernUi && ChatLogFolderRequested is not null,
                ValueText: "Each session's chat is a text file named after its start and the character."));
        }
    }

    internal void ModernOptionToggle(string id)
    {
        if (!ModernOptionsOpen) return;
        if (id.StartsWith("camera/", StringComparison.Ordinal) && int.TryParse(id.AsSpan(7), NumberStyles.None, CultureInfo.InvariantCulture, out int camera)) _cameraState.Toggle(camera);
        else if (id is "doppler" or "settings/CHECK00" or "fonts/same") _settings.ToggleCheck();
        else if (id == "settings/ROUTE") _settings.ToggleRoutePlanner();
        else if (id.StartsWith("labels/icon/", StringComparison.Ordinal) && int.TryParse(id.AsSpan(12), NumberStyles.None, CultureInfo.InvariantCulture, out int iconKind)) _settings.ToggleLabelIcon(iconKind);
        else if (id == "modern/rail") _settings.ToggleGutterRail();
        else if (id == "modern/chattimes") _settings.ToggleChatTimes();
        else if (id == "modern/chathistory") _settings.ToggleChatHistory();
        else if (id == "modern/chatlogs") _settings.ToggleChatLogs();
        else if (id.StartsWith("settings/QOL", StringComparison.Ordinal) && int.TryParse(id.AsSpan(12), NumberStyles.None, CultureInfo.InvariantCulture, out int row)) _settings.ToggleConvenience(row);
        else if (id == "graphics/CHECK00") { _graphicsPanel.ToggleRecommended(); PublishGraphicsPreview(); TakeGraphicsError(); }
        else if (id is "graphics/ENHANCED" or "graphics/BLOOM" or "graphics/HDR" or "graphics/SUPERSAMPLING" or "graphics/ANISOTROPIC" or "graphics/BGCAP")
        { ActivateGraphics(id); TakeGraphicsError(); }
        else return;
        if (_modernCategory == "sound") _settings.PreviewSound();
        if (_modernCategory is not ("camera" or "graphics" or "advanced")) _settings.PreviewQualityChanges();
        InteractionSoundRequested?.Invoke("fontclick.wav");
    }

    /// <summary>Opens the folder the Modern UI chat logs are written to.</summary>
    internal Action? ChatLogFolderRequested { get; set; }
    private static int LookSpeedSteps => (EnbNext.Presentation.EnbQualityOfLifeOptions.LookSpeedMaximum - EnbNext.Presentation.EnbQualityOfLifeOptions.LookSpeedMinimum) / EnbNext.Presentation.EnbQualityOfLifeOptions.LookSpeedStep + 1;
    /// <summary>A slider moved to a step (0 to its steps - 1).</summary>
    internal void ModernOptionSlide(string id, int value)
    {
        if (!ModernOptionsOpen || _modernCategory == "camera") return;
        int before = ModernOptionRows().FirstOrDefault(r => r.Id == id)?.Value ?? -1;
        if (_modernCategory is "graphics" or "advanced")
        {
            // The classic page's slider values: the five retail rows preview live; the background cap is kept on OK.
            if (id == "graphics/bgcap") _graphicsState.Background.SetValue(value);
            else if (id.StartsWith("graphics/slider/", StringComparison.Ordinal) && int.TryParse(id.AsSpan(16), NumberStyles.None, CultureInfo.InvariantCulture, out int slider) &&
                slider < _graphicsState.Sliders.Length)
            { _graphicsState.Sliders[slider].SetValue(value); PublishGraphicsPreview(); TakeGraphicsError(); }
            else return;
            if (before != value) InteractionSoundRequested?.Invoke("slider_bar_1.wav");
            return;
        }
        switch (id)
        {
            case "slider/0" or "slider/1" or "slider/2":
                _settings.Change(id[^1] - '0', value); return; // the retail slider plays its own notch sound
            case "fonts/scale" or "fonts/spacing" or "fonts/line":
                _settings.Change(id == "fonts/scale" ? 2 : id == "fonts/spacing" ? 3 : 4, value); ModernOptionsError = _settings.Error; return;
            case "settings/QOL10":
                _settings.Multibox = _settings.Multibox with { EncounterPauseSeconds = Math.Clamp(value + 1, 1, 60) }; break;
            case "settings/QOL16":
                _settings.SetArrowSize(EnbNext.Presentation.EnbQualityOfLifeOptions.ArrowSizeMinimum + Math.Clamp(value, 0, EnbSettingsPanel.ArrowSizeSteps - 1) * EnbNext.Presentation.EnbQualityOfLifeOptions.ArrowSizeStep); break;
            case "station/look":
                _settings.SetStationLookSpeed(EnbNext.Presentation.EnbQualityOfLifeOptions.LookSpeedMinimum + Math.Clamp(value, 0, LookSpeedSteps - 1) * EnbNext.Presentation.EnbQualityOfLifeOptions.LookSpeedStep); break;
            case "settings/QOL25":
                _settings.SetChatTextSize(EnbNext.Presentation.EnbQualityOfLifeOptions.ChatTextMinimum + Math.Clamp(value, 0, EnbSettingsPanel.ChatTextSteps - 1)); break;
            default: return;
        }
        _settings.PreviewQualityChanges();
        if (before != value) InteractionSoundRequested?.Invoke("slider_bar_1.wav");
    }

    /// <summary>A button row: Default restores the open chat window's font, as the classic page's Default does.</summary>
    internal void ModernOptionPress(string id)
    {
        if (_modernCategory == "controls" && id is "controls/default/0" or "controls/default/1" && _controlsPanel?.Draft is not null && _confirmation.Text is null)
        {
            // The classic Default buttons (BUTTON01 and BUTTON02) replace the whole draft.
            ApplyControlResult(_controlsPanel.Activate(id[^1] == '0' ? "control/BUTTON01" : "control/BUTTON02"));
            return;
        }
        if (ModernOptionsOpen && id == "modern/chatfolder") { ChatLogFolderRequested?.Invoke(); InteractionSoundRequested?.Invoke("fontclick.wav"); return; }
        if (!ModernOptionsOpen || _modernCategory != "fonts" || id != "fonts/default") return;
        _settings.DefaultFont(); ModernOptionsError = _settings.Error;
        InteractionSoundRequested?.Invoke("fontclick.wav");
    }

    /// <summary>A choice from a list (resolution, display scope, UI scale, textures): the classic page's own selection and preview.</summary>
    internal void ModernOptionChoose(string id, int index)
    {
        if (!ModernOptionsOpen || index < 0) return;
        if (id.StartsWith("scope/", StringComparison.Ordinal))
        {
            if (index <= 1 && SettingsScope?.Invoke(id[6..]) is bool all && all != (index == 1)) SettingsScopeRequested?.Invoke(id[6..], index == 1);
            InteractionSoundRequested?.Invoke("fontclick.wav");
            ModernOptionsChanged?.Invoke();
            return;
        }
        if (_modernCategory == "controls")
        {
            // A key group is the classic page's top button of that group.
            if (id != "controls/group" || _controlsPanel?.Draft is null || _confirmation.Text is not null) return;
            _controlsPanel.Activate("control/TOP_BUTTON" + index.ToString("00", CultureInfo.InvariantCulture));
            return;
        }
        if (_modernCategory == "fonts")
        {
            // The window and the font are the classic page's first two sliders.
            if (id is not ("fonts/window" or "fonts/font") || index >= _settings.Range(id == "fonts/window" ? 0 : 1)) return;
            _settings.Change(id == "fonts/window" ? 0 : 1, index); ModernOptionsError = _settings.Error;
            InteractionSoundRequested?.Invoke("fontclick.wav");
            return;
        }
        if (_modernCategory is not ("graphics" or "advanced")) return;
        var state = _graphicsState;
        state.ResolutionDropdown = state.UiScaleDropdown = state.TextureDropdown = state.AdapterDropdown = false;
        string choice = "graphics/choice" + index.ToString("00", CultureInfo.InvariantCulture);
        switch (id)
        {
            case "graphics/resolution" when index < state.DisplayChoices.Count && index != state.DisplayChoiceIndex: ActivateGraphics(choice); break;
            case "graphics/scope" when index <= 1: ActivateGraphics(index == 0 ? "graphics/scopeSystem" : "graphics/scopeCharacter"); break;
            case "graphics/uiscale" when index < state.UiScales.Length: state.UiScaleDropdown = true; ActivateGraphics(choice); break;
            case "graphics/textures" when index < state.TexturePacks.Length: state.TextureDropdown = true; ActivateGraphics(choice); break;
            case "graphics/adapter" when index < state.Adapters.Length: SelectGraphicsAdapter(index); break;
            default: return;
        }
        TakeGraphicsError();
        InteractionSoundRequested?.Invoke("fontclick.wav");
    }
}
