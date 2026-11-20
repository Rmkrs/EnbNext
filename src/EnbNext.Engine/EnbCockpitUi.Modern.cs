using Point = EnbNext.Presentation.EnbUiPoint;
using Rect = EnbNext.Presentation.EnbUiRect;
using System.Collections.Immutable;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

/// <summary>One Modern UI action-bar slot as the cockpit's shortcut owner resolves it.</summary>
internal readonly record struct EnbModernShortcut(EnbUiRaster? Icon, EnbUiRaster? Highlight, string Label, EnbCombatIcon? Feedback, bool Dragged);
/// <summary>Next cast bar: the ability being powered up, its progress (0..1) and the tenths of a second left.</summary>
internal readonly record struct EnbModernPowerup(string Name, float Progress, int TenthsLeft);

internal sealed partial class EnbCockpitUi
{
    // Modern UI action bar: bank A of both bars (keys 1-6) then bank B (Alt+1-6), always all twelve.
    // Names encode the explicit group: BTTN_<bar>_<group+1><button>; retail names keep '0' (visible group).
    internal static readonly string[] ModernSlotNames = Enumerable.Range(0, 12)
        .Select(i => $"BTTN_{i % 6 / 3}_{i / 6 + 1}{i % 3}").ToArray();
    private static bool IsModernSlot(string name) => name.Length == 9 && name.StartsWith("BTTN_", StringComparison.Ordinal) && name[7] != '0';
    private static int ModernIndex(string name) => (name[7] - '1') * 6 + (name[5] - '0') * 3 + (name[^1] - '0');
    private static EnbRetailShortcutAddress ModernAddress(int index) => new(index % 6 / 3, index / 6, index % 3);
    private EnbSceneShortcutSlotSnapshot? SlotOf(string name) => IsModernSlot(name)
        ? SlotAt(ModernAddress(ModernIndex(name)))
        : VisibleSlot(name[5] - '0', name[^1] - '0');
    /// <summary>Retail acts only on the visible bank; the Modern UI bar shows (and may act on) both.</summary>
    private bool Shown(EnbRetailShortcutAddress address) => Modern || _pages.Group(address.Bar) == address.Group;

    private bool modern;
    /// <summary>Next Modern UI: the cockpit draws nothing and takes no pointer input except its buff row; its shortcut
    /// logic serves the Modern UI action bar through <see cref="ModernSlotNames"/>.</summary>
    /// <summary>Next Modern UI: the card for an action bar shortcut (its ability, or the item it uses).</summary>
    internal Func<EnbSceneShortcutSlotSnapshot, string?, EnbNext.Rendering.EnbTooltipCard?>? ModernShortcutCard { get; set; }
    internal bool Modern
    {
        get => modern;
        set
        {
            if (modern == value) return;
            modern = value; CancelPointerInput();
            if (value) _pages.ShowFirstGroups();
        }
    }
    /// <summary>Screen rectangle of the Modern UI engine panel's THROTTLE, REVERSE or WARP button.</summary>
    internal Func<string, Rect?>? ModernEngineBounds { get; set; }
    /// <summary>Thrust and warp state for the Modern UI engine panel.</summary>
    internal EnbModernEngine ModernEngine => new(Presentation?.WarpFill ?? 0, WarpEngaged, CanPressWarp, _thrust, CanThrust,
        _interactive && _hovered is "THROTTLE" or "REVERSE" or "WARP" ? _hovered : null, _pressed ? _pressedName : null, _state?.Warp?.Incapacitated == true);
    /// <summary>Screen rectangle of a Modern UI action-bar slot (0-11), supplied by the bar.</summary>
    internal Func<int, Rect?>? ModernSlotBounds { get; set; }
    private readonly EnbCombatIcon?[] _modernFeedback = new EnbCombatIcon?[12];

    private EnbAuthoredUiAsset? _modernArtSource;
    private (EnbAuthoredUiAsset Button, Rect ButtonDesign, Rect IconDesign)? _modernArt;
    private readonly string?[] _modernLabels = new string?[12];
    private EnbRetailKeyBindings? _labelBindings;
    /// <summary>The authored shortcut button and its icon placement (design space), for the Modern UI bar.</summary>
    internal (EnbAuthoredUiAsset Button, Rect ButtonDesign, Rect IconDesign)? ModernSlotArt
    {
        get
        {
            if (ReferenceEquals(_modernArtSource, _shortcutAsset)) return _modernArt;
            _modernArtSource = _shortcutAsset; _modernArt = null; Array.Clear(_modernLabels);
            if (_shortcutAsset is null || _iconBounds.Width <= 0 || !_hitBounds.TryGetValue("BTTN_0_00", out var button)) return null;
            var mesh = _shortcutAsset.Resource.Meshes.FirstOrDefault(m => m.Name == "BTTN_0_00"); if (mesh is null) return null;
            var anchor = _shortcutTransforms[_shortcutAsset.Hierarchy.Pivots.First(p => p.Name == "BTTN_0_00").Ordinal].Translation;
            // Draw design space is centred (as the inventory's art); the icon keeps its authored offset from the button.
            var icon = new Rect(anchor.X + _iconBounds.X, -anchor.Y + _iconBounds.Y, _iconBounds.Width, _iconBounds.Height);
            return _modernArt = (_shortcutAsset with { Resource = _shortcutAsset.Resource.Copy().SetMeshes([mesh]) }, button.Translate(-320, -240), icon);
        }
    }

    private EnbSceneShortcutSlotSnapshot? SlotAt(EnbRetailShortcutAddress address)
    {
        if (_shortcuts is not { } shortcuts) return null;
        foreach (var slot in shortcuts.Slots) if (slot.Address == address) return slot;
        return null;
    }

    internal EnbModernShortcut ModernShortcut(int index)
    {
        var slot = SlotAt(ModernAddress(index));
        string name = ModernSlotNames[index];
        bool dragged = _shortcutDragging && slot?.Assignment == _shortcutDrag?.Source;
        EnbUiRaster? icon = null, highlight = null;
        if (slot?.Resolution == EnbSceneShortcutResolution.Resolved && !dragged)
        {
            icon = Icon(slot);
            if (_interactive && _hovered == name && (EnbShortcutSkillActivation.IsSkill(slot) || EnbShortcutEquipmentActivation.IsItem(slot)))
                highlight = Icon(slot with { IconTint = System.Numerics.Vector3.One });
        }
        // Each slot shows the key it is bound to (its first key; nothing when unbound).
        var bindings = _assets?.ResolveKeyBindings(Session());
        if (!ReferenceEquals(bindings, _labelBindings)) { _labelBindings = bindings; Array.Clear(_modernLabels); }
        if (_modernLabels[index] is not { } label)
        {
            string action = "Use Slot " + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var bound = bindings?.Bindings.Where(b => b.Action == action).OrderBy(b => b.Slot).FirstOrDefault();
            _modernLabels[index] = label = bound is null ? "" : (bound.Modifiers.HasFlag(EnbKeyModifiers.Control) ? "C" : "") +
                (bound.Modifiers.HasFlag(EnbKeyModifiers.Alt) ? "A" : "") + (bound.Modifiers.HasFlag(EnbKeyModifiers.Shift) ? "S" : "") + bound.Key;
        }
        return new(icon, highlight, label, dragged ? null : _modernFeedback[index], dragged);
    }

    /// <summary>Next cast bar: the powering-up ability from the player's SkillPowerup fields (6A4C20 progress).</summary>
    internal EnbModernPowerup? ModernPowerup
    {
        get
        {
            if (!Modern || !_interactive || _shortcuts?.Powerup is not { Ability: >= 0 } powerup) return null;
            uint now = unchecked((uint)_combatTime);
            float progress = EnbRetailSkillFeedback.Progress(powerup.Start, powerup.End, now);
            if (progress <= 0) return null;
            string name = _assets?.ResolveShortcutSkills()?.Abilities.FirstOrDefault(a => a.Id == powerup.Ability)?.Name ?? "Powering up";
            long left = Math.Max(0, Math.Clamp(powerup.End, int.MinValue, int.MaxValue) - (long)(int)now);
            return new(name, Math.Clamp(progress, 0, 1), (int)Math.Min(int.MaxValue, (left + 99) / 100));
        }
    }

    /// <summary>The dragged shortcut icon that follows the pointer.</summary>
    internal ImmutableArray<EnbUiTextDraw> ModernDragImages => Modern ? BuildShortcutDrag() : [];
    internal bool ModernHovered(int index) => _hovered == ModernSlotNames[index];
}
