using System.Globalization;

namespace EnbNext.Assets;

public sealed partial class EnbRetailAssetBundle
{
    private readonly Dictionary<uint, EnbRetailSoundOptions> _soundPreviews = [];
    private readonly Dictionary<(uint Owner, int Profile), IReadOnlyList<EnbFontDefinition>> _fontPreviews = [];
    public long SettingsRevision { get; private set; }

    /// <summary>Forgets what was read of a character's options file (it was replaced on disk).</summary>
    public void ForgetPlayerOptions(uint owner)
    {
        lock (_sessionPresentationSelectionGate) { _playerOptionSelections.Remove(owner); _cameraSelections.Remove(owner); SettingsRevision++; }
    }

    public void SetSoundPreview(uint owner, EnbRetailSoundOptions? sound)
    {
        lock (_sessionPresentationSelectionGate)
        {
            if (sound is null) _soundPreviews.Remove(owner); else _soundPreviews[owner] = sound;
            SettingsRevision++;
        }
    }

    public void SetSoundPreview(uint owner,float music,float effects,float dialog,bool doppler)
    {
        lock(_sessionPresentationSelectionGate)
        {
            if(_soundPreviews.TryGetValue(owner,out var current) && current.Music==music && current.Effects==effects && current.Dialog==dialog && current.Doppler==doppler)return;
            _soundPreviews[owner]=new(music,effects,dialog,doppler);SettingsRevision++;
        }
    }

    private EnbRetailPlayerOptionsSelection WithSettingsPreview(uint owner, EnbRetailPlayerOptionsSelection selected)
        => _soundPreviews.TryGetValue(owner, out var sound) ? selected with { Options = selected.Options with
        { SoundMusic = sound.Music, SoundSfx = sound.Effects, SoundDialog = sound.Dialog } } : selected;

    public EnbFontDefinition ResolveChatFont(uint? owner, EnbFontProfile profile, EnbFontDefinition authored)
    {
        lock (_sessionPresentationSelectionGate)
        {
            if (owner is uint id && _fontPreviews.TryGetValue((id, profile.Index), out var preview) &&
                preview.FirstOrDefault(f => f.FontId == authored.FontId) is { } font) return font;
            return EnbRetailChatFonts.Resolve(profile, authored, ResolvePlayerOptions(owner).Options.FontOptions, Fonts?.AvailableFonts ?? []);
        }
    }

    public void SetChatFontPreview(uint owner, EnbFontProfile profile, IReadOnlyList<EnbFontDefinition>? fonts)
    {
        lock (_sessionPresentationSelectionGate)
        {
            if (fonts is null) _fontPreviews.Remove((owner, profile.Index)); else _fontPreviews[(owner, profile.Index)] = fonts.ToArray();
            SettingsRevision++;
        }
    }

    public void SaveChatFonts(uint owner, EnbFontProfile profile, IReadOnlyList<EnbFontDefinition> fonts)
        => SaveSettings(owner, text =>
        {
            var previous = EnbRetailPlayerOptionsCatalog.ParseText(text).FontOptions;
            return EnbRetailCinematicOptionsWriter.UpdateValues(text, fonts.Select(f =>
                ("STRING", f.FontId!, EnbRetailChatFonts.Update(previous.GetValueOrDefault(f.FontId!, ""), profile, f))).ToArray());
        });

    public void SaveSoundOptions(uint owner, EnbRetailSoundOptions sound)
        => SaveSettings(owner, text => EnbRetailCinematicOptionsWriter.UpdateValues(text,
            [("FLOAT", "Sound_Music", Number(sound.Music)), ("FLOAT", "Sound_SFX", Number(sound.Effects)),
             ("FLOAT", "Sound_Dialog", Number(sound.Dialog)), ("BOOL", "Doppler_Enabled", sound.Doppler ? "yes" : "no")]));

    public EnbCharacterInterfaceSettings CharacterInterface { get; set; } = new();

    public void SaveInterfaceOptions(uint owner, float delay, bool? autopilot = null)
        => SaveSettings(owner, text => EnbRetailCinematicOptionsWriter.UpdateValues(text,
            autopilot is bool enabled ? [("FLOAT", "Interface_Tooltip_Delay", Number(delay)), ("BOOL", "Next_Autopilot", enabled ? "yes" : "no")] : [("FLOAT", "Interface_Tooltip_Delay", Number(delay))]));

    public void SaveInventorySort(uint owner, bool vault, int mode, bool reverse, bool reverseChanged)
        => SaveSettings(owner, text => EnbRetailCinematicOptionsWriter.UpdateValues(text,
            reverseChanged ? [("BOOL",vault?"Inv_Sort_Vault_Reverse":"Inv_Sort_Cargo_Reverse",reverse?"yes":"no")]
            : [("INT",vault?"Inv_Sort_Vault":"Inv_Sort_Cargo",mode.ToString(CultureInfo.InvariantCulture))]));

    public void SaveInventoryFilter(uint owner, bool vault, string filter)
        => SaveSettings(owner, text => EnbRetailCinematicOptionsWriter.UpdateValues(text,
            [("STRING", vault ? "Inv_Filter_Vault" : "Inv_Filter_Cargo", filter)]));

    private static string Number(float value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private void SaveSettings(uint owner, Func<string, string> update)
    {
        if (owner == 0) throw new InvalidOperationException("A character is required to save settings.");
        lock (_sessionPresentationSelectionGate)
        {
            string path = Path.Combine(Paths.ClientOutputDirectory, $"player{EnbRetailPlayerOptionsCatalog.GetCurrentCompatibilitySelector(owner)}_options.ini");
            string text = update(EnbNext.Installation.EnbPathCase.FileExists(path) ? File.ReadAllText(EnbNext.Installation.EnbPathCase.Resolve(path)) : "");
            Directory.CreateDirectory(Paths.ClientOutputDirectory);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, text);
                File.Move(temporary, EnbNext.Installation.EnbPathCase.Resolve(path), true);
                _playerOptionSelections.Remove(owner);
                SettingsRevision++;
            }
            finally { if (EnbNext.Installation.EnbPathCase.FileExists(temporary)) File.Delete(temporary); }
        }
    }
}
