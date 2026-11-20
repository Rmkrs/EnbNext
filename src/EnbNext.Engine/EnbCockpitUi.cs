using Point = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using EnbNext.Client;
using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

/// <summary>Authored TSI01A anchors and hit geometry. Artwork/text are drawn by the world GPU.</summary>
internal sealed partial class EnbCockpitUi : IDisposable
{
    internal EnbUiLayoutProfiles? UiLayout {get;set;}
    private EnbRetailAssetBundle? _assets;
    private EnbAuthoredUiAsset? _asset;
    private Matrix4x4[] _transforms=[];
    private EnbInterfaceLayoutCatalog? _layout;
    private EnbRetailIniDocument? _strings;
    private EnbRetailScannerTooltipDefinition? _tooltipDefinition;
    private EnbAuthoredTooltipFrame? _tooltipFrame;
    private readonly Dictionary<string, EnbRetailFontFace> _fonts = new();
    private readonly EnbRetailText[] _text = [new(), new()];
    private readonly List<EnbRetailText> _richTip = [];
    internal static readonly string[] HitNames;
    static EnbCockpitUi() => HitNames = ShortcutHitNames.Concat(new[] { "AUTOPILOT", "DAMAGE", "THROTTLE", "REVERSE", "WARP", "BAR00", "BAR01", "BAR02" }).Concat(BuffHitNames).Concat(ModernSlotNames).ToArray();
    public event Action<bool>? VisibilityChanged;
    public event Action<string,Rect?>? HitBoundsChanged;
    public event Action<string?>? CaptureRequested;
    private bool IsVisible { set => VisibilityChanged?.Invoke(value); }
    private readonly Dictionary<string, Rect> _hitBounds = new();
    private readonly Dictionary<string, (Vector2[] Points, IReadOnlyList<uint> Indices)> _hitGeometry = new();
    private readonly EnbCurrentRetailTooltipDelay _dwell = new();
    private IEnbCockpitFrame? _state;
    private Size _size;
    private double _scaling = 1;
    private Point _pointer;
    private string? _hovered;
    private bool _pressed;
    private string? _pressedName;
    private int? _pressedWarpState;
    private bool _interactive;
    private int _thrust;
    private readonly EnbWarpGaugeRuntime _warpGauge = new();
    public string? Error { get; private set; }
    public Func<uint?> Session { get; set; } = () => null;
    public event Action<int>? ThrustPressed;
    public event Action? ThrustReleased;
    /// <summary>The committed retail camera state (EnbWorldController's controller, client+12A8 index 1C).</summary>
    internal Func<EnbNext.Rendering.EnbRetailCameraState>? CameraState { get; set; }

    /// <summary>00582CF3..D1F/0079B600: while the committed camera state is Landing, Dock, Arrival or Takeoff (indices
    /// 5/6/7/9; not Warp) the speed reads the literal 100; otherwise the ship's speed (HUD-D01).</summary>
    internal string SpeedText(EnbNext.Presentation.IEnbRenderTransform? transform)
        => CameraState?.Invoke() is EnbNext.Rendering.EnbRetailCameraState.Landing or EnbNext.Rendering.EnbRetailCameraState.Dock
            or EnbNext.Rendering.EnbRetailCameraState.Arrival or EnbNext.Rendering.EnbRetailCameraState.Takeoff
            ? "100" : EnbCockpitThrustState.SpeedText(transform);

    private bool CanPressWarp => _state?.Warp is {} warp && (warp.CanActivate || warp.CanRequestRescue || warp.CanTakeoff);
    public event Action? WarpRequested;
    public event Action<EnbUiTooltipDraw?>? TooltipChanged;
    public EnbCockpitDraw? Presentation { get; private set; }

    public bool PressPointer(string name, Point point, int button)
    {
        bool handled=false;
        if (_interactive && name.StartsWith("BTTN_", StringComparison.Ordinal) &&
            button == 2)
        {
            handled = true; ClearHover();
            _pressedSkill = EnbShortcutSkillActivation.Capture(_shortcuts,SlotOf(name),_shortcutShipOwner);
            _pressedEquipment = EnbShortcutEquipmentActivation.Capture(_shortcuts,SlotOf(name),_shortcutShipOwner);
            ArmEquipmentNotification(_pressedEquipment);
            if (_pressedSkill is null && _pressedEquipment is null) return handled;
            _pressed = true; _pressedName = name; _pressedRight = true;
            CaptureRequested?.Invoke(name); return handled;
        }
        if (!_interactive || button != 1) return handled;
        if(name=="DAMAGE")
        {
            _pressed=true;_pressedName=name;_pointer=point;CaptureRequested?.Invoke(name);
            return true;
        }
        if(name=="AUTOPILOT")
        {
            if(!_autopilotActive && !_autopilotCanStart)return true;
            _autopilotWasStop=_autopilotActive;
            _pressed=true;_pressedName=name;_pointer=point;CaptureRequested?.Invoke(name);
            if(_autopilotWasStop)AutopilotRequested?.Invoke(false);
            return true;
        }
        if(name.StartsWith("BUFF",StringComparison.Ordinal))
        {handled=true;OpenBuffDescription(name,point);return handled;}
        handled = true; ClearHover();
        if (name.StartsWith("BTTN_", StringComparison.Ordinal))
        {
            _pressedSkill = EnbShortcutSkillActivation.Capture(_shortcuts,
                SlotOf(name), _shortcutShipOwner);
            _pressedEquipment = EnbShortcutEquipmentActivation.Capture(_shortcuts,
                SlotOf(name), _shortcutShipOwner);
            ArmEquipmentNotification(_pressedEquipment);
            PrepareShortcutDrag(name,point);
            if (_pressedSkill is null && _pressedEquipment is null && _shortcutDrag is null) return handled;
            _pressed = true; _pressedName = name; _pointer = point; CaptureRequested?.Invoke(name);
            return handled;
        }
        if (name.StartsWith("BANK_", StringComparison.Ordinal))
        {
            _pressed = true; _pressedName = name; _pointer = point; CaptureRequested?.Invoke(name);
            return handled;
        }
        if (name == "WARP" && CanPressWarp)
        {
            _pressedWarpState = _state?.Warp?.State;
            _pressed = true; _pressedName = name; _pointer = point; CaptureRequested?.Invoke(name); return handled;
        }
        if (name is not ("THROTTLE" or "REVERSE") || !CanThrust) return handled;
        _pressed = true; _pressedName = name; _pointer = point; CaptureRequested?.Invoke(name);
        ThrustPressed?.Invoke(name == "THROTTLE" ? 1 : -1);
        return handled;
    }
    public bool ReleasePointer(Point point,int button)
    {
        bool handled=false;
        if (!_pressed || button != (_pressedRight ? 2 : 1)) return handled;
        var pressed = _pressedName;
        var skill = _pressedSkill;
        var equipment = _pressedEquipment;
        bool right = _pressedRight;
        bool inside = pressed is not null && Contains(pressed, point);
        var move=_shortcutDragging&&_shortcutDrag is {} drag?drag with {Destination=ShortcutDropAddress(point)}:null;
        ReleasePointer();
        if(move is not null)
        {
            ShortcutSoundRequested?.Invoke("TextClick02.wav");
            // Retail drop handlers rebuild/attach the shortcut even when
            // returning to its original slot (00614360).
            if(move.Destination is not null)
                ShortcutSoundRequested?.Invoke("shortcut_created_sn_1.wav");
            if(MoveShortcutRequested?.Invoke(move)==true)
            {
                ResetShortcutFeedback(move.Source.Address);
                if(move.Destination is {} destination)ResetShortcutFeedback(destination);
            }
            handled=true;return handled;
        }
        if (inside && pressed == "AUTOPILOT" && !_autopilotWasStop && _autopilotCanStart) AutopilotRequested?.Invoke(true);
        if (inside && pressed == "DAMAGE") RequestDamageMeterToggle();
        if (inside && pressed == "WARP" && CanPressWarp) WarpRequested?.Invoke();
        if (right)
        {
            if (inside && pressed is not null && equipment is not null && equipment == EnbShortcutEquipmentActivation.Capture(_shortcuts,
                SlotOf(pressed),_shortcutShipOwner))
                ItemExamined?.Invoke(equipment.Assignment.Source == EnbRetailShortcutSource.Equipment ? (byte)2 : (byte)1, equipment.Assignment.SavedIndex!.Value,point);
            else if (inside && pressed is not null && skill is not null && skill == EnbShortcutSkillActivation.Capture(_shortcuts,
                SlotOf(pressed),_shortcutShipOwner)) OpenSkillMenu(pressed);
            handled = true; return handled;
        }
        if (inside && pressed?.StartsWith("BANK_", StringComparison.Ordinal) == true)
        {
            _pages.Toggle(pressed[^1] - '0');
            RebindShortcutKeys();
        }
        if (inside && skill is not null && Shown(skill.Assignment.Address))
        {
            if (EnbShortcutSkillActivation.TryResolve(skill, _shortcuts, _shortcutShipOwner, out uint ignoredAbility))
                SkillRequested?.Invoke(skill);
            // 0062D730: an ability that cannot be used while incapacitated
            // reports local type 0x11 instead of sending 0x0058.
            else if (_shortcuts is { IsIncapacitated: true } incapacitated &&
                EnbShortcutSkillActivation.TryResolve(skill, incapacitated with { IsIncapacitated = false }, _shortcutShipOwner, out _))
                IncapacitatedShortcutPressed?.Invoke();
        }
        if (inside && equipment is not null) DispatchEquipment(equipment);
        handled = true;
        return handled;
    }

    // 005821C0 enables the thrust buttons from MaxSpeed and LockSpeed only.
    public bool CanThrust => _state is { MaximumSpeed: > 0, IsAutoFollowing: false } && !SpeedLocked && !WarpEngaged;
    /// <summary>0057D6C0 held Forward/Backward admission: LockSpeed clear and MaxSpeed nonzero (HUD-D05).</summary>
    public bool CanHeldThrust => _state is { MaximumSpeed: > 0 } && !SpeedLocked;
    public bool WarpEngaged => _state?.Warp?.IsEngaged == true;
    public bool SpeedLocked { get; private set; }
    public bool IsThrustPointerInside => _pressed && (_pressedName is "THROTTLE" or "REVERSE") && Contains(_pressedName, _pointer);
    public void SetThrust(int value) => _thrust = value;
    public bool BlocksFlight(Point point) => _interactive && (_menuRequest is not null || HitNames.Any(name => Contains(name, point)));

    internal bool Contains(string name, Point point)
    {
        if(name is "AUTOPILOT" or "DAMAGE")
        {
            var round=name=="AUTOPILOT"?_autopilotBounds:_damageMeterBounds;
            if(!_interactive || (name=="AUTOPILOT"?AutopilotDraw:DamageMeterDraw) is null)return false;
            if(!Modern)point=UiLayout?.Inverse(EnbUiGroup.Cockpit,point)??point;
            double x=(point.X-round.Center.X)/(round.Width/2);
            double y=(point.Y-round.Center.Y)/(round.Height/2);
            return x*x+y*y<=1;
        }
        if (IsModernSlot(name))
            return Modern && _interactive && ModernSlotBounds?.Invoke(ModernIndex(name)) is { } slotBounds && slotBounds.Contains(point);
        // Modern UI: thrust and warp are the engine panel's buttons.
        if (Modern && name is "THROTTLE" or "REVERSE" or "WARP")
            return _interactive && ModernEngineBounds?.Invoke(name) is { } engine && engine.Contains(point);
        // Modern UI replaces the cockpit (buffs show under its unit frames).
        if (Modern) return false;
        if (!_interactive || _size.Width <= 0 || _size.Height <= 0 || !_hitGeometry.TryGetValue(name, out var geometry)) return false;
        point=UiLayout?.Inverse(name.StartsWith("BUFF",StringComparison.Ordinal)?EnbUiGroup.Buffs:EnbUiGroup.Cockpit,point)??point;
        var p = new Vector2((float)(point.X * 640 / _size.Width), (float)(point.Y * 480 / _size.Height));
        if (!_hitBounds[name].Contains(new Point(p.X,p.Y))) return false;
        float Cross(Vector2 a, Vector2 b) => a.X*b.Y-a.Y*b.X;
        for (int i = 0; i + 2 < geometry.Indices.Count; i += 3)
        {
            var a = geometry.Points[geometry.Indices[i]]; var b = geometry.Points[geometry.Indices[i+1]];
            var c = geometry.Points[geometry.Indices[i+2]];
            float x = Cross(b-a,p-a), y = Cross(c-b,p-b), z = Cross(a-c,p-c);
            if ((x >= 0 && y >= 0 && z >= 0) || (x <= 0 && y <= 0 && z <= 0)) return true;
        }
        return false;
    }

    public void Suspend()
    {
        AutopilotDraw=null;DamageMeterDraw=null;
        _interactive = false; IsVisible = false; SetShortcutShift(false); CancelPointerInput();
    }

    public void CancelPointerInput() => CancelPointerInputForOverlay(true);
    internal void CancelPointerInputForOverlay(bool closeSkillMenu)
    {
        CancelShortcutKeys();
        if(closeSkillMenu) CloseSkillMenu();
        else
        {
            CancelMenuCapture();
        }
        ClearHover(); ReleasePointer();
    }

    internal void ReleasePointer()
    {
        ClearShortcutDrag();
        if (!_pressed) return;
        bool thrust = _pressedName is "THROTTLE" or "REVERSE";
        _pressed = false; _pressedName = null; _pressedSkill = null; _pressedEquipment = null; _pressedRight = false; CaptureRequested?.Invoke(null);
        if (thrust) ThrustReleased?.Invoke();
    }

    public void Update(IEnbSceneFrame snapshot, EnbRetailAssetBundle? assets, Size size, double scaling, bool interactive)
        => UpdateAtTime(snapshot, assets, size, scaling, interactive, snapshot.ClientTime);

    public void UpdateAtTime(IEnbSceneFrame snapshot, EnbRetailAssetBundle? assets, Size size, double scaling, bool interactive, long currentClientTime)
    {
        if (!ReferenceEquals(assets, _assets))
        {
            Dispose(); _assets = assets; Error = null;
            if (assets is not null)
            {
                try
                {
                    _asset = EnbAuthoredUiAsset.Load(assets, "tsi01a.w3d");
                    if (_asset is not null)
                    {
                        _transforms = ResolveTransforms(_asset);
                        foreach (string name in HitNames.Except(ShortcutHitNames).Except(BuffHitNames).Except(ModernSlotNames).Where(n=>n is not "AUTOPILOT" and not "DAMAGE"))
                        {
                            _hitBounds[name] = MeshBounds(_asset, name);
                            var mesh = _asset.Resource.Meshes.First(m => m.Name == name);
                            var transform = _transforms[_asset.Hierarchy.Pivots.First(p => p.Name == name).Ordinal];
                            _hitGeometry[name] = (mesh.Positions.Select(p => {
                                var v = Vector3.Transform(p,transform); return new Vector2(v.X+320,240-v.Y);
                            }).ToArray(), mesh.Indices);
                        }
                    }
                    _layout = EnbInterfaceLayoutCatalog.Load(Path.Combine(assets.Paths.ClientIniDirectory, "fc_cpit.ini"));
                    LoadShortcuts(assets);
                    LoadBuffs(assets);
                    _strings = EnbRetailIniDocument.Load(Path.Combine(assets.Paths.ClientIniDirectory, "tooltip.ini"));
                    _tooltipDefinition = EnbRetailScannerTooltipDefinition.Load(assets.Paths.ClientIniDirectory);
                    _tooltipFrame = _tooltipDefinition is { } def ? EnbAuthoredTooltipFrame.Load(assets, def) : null;
                    if (_asset is null) Error = "Installed TSI01A cockpit unavailable.";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                { Error = ex.Message; _asset = null; _hitBounds.Clear(); _hitGeometry.Clear(); }
            }
        }
        _size = size; _scaling = scaling; _state = snapshot.Hud.Cockpit;
        if (_pressedName == "WARP" && (!CanPressWarp || _state?.Warp?.State != _pressedWarpState)) ReleasePointer();
        var ship = snapshot.Objects.FirstOrDefault(o => o.GameId == snapshot.LocalShipObjectId);
        _shortcuts = snapshot.Hud.Shortcuts;
        if (_shortcutOwner != _shortcuts?.Saved.CharacterKey || _shortcutShipOwner != (ship?.RenderIdentity ?? 0))
        { SetShortcutShift(false); CancelPointerInput(); _shortcutOwner = _shortcuts?.Saved.CharacterKey; _shortcutShipOwner = ship?.RenderIdentity ?? 0; }
        _pages.Update(_shortcuts);
        if(_shortcutDrag is {} drag&&(!ReferenceEquals(drag.Expected,_shortcuts?.Saved)||drag.Character!=Session()||
            drag.ShipOwner!=_shortcutShipOwner||!Shown(drag.Source.Address)))ReleasePointer();
        if (_pressedEquipment is not null && !EnbShortcutEquipmentActivation.TryResolve(_pressedEquipment, _shortcuts, _shortcutShipOwner, null, out _))
            ReleasePointer();
        foreach (var (key, request) in _equipmentKeyPresses)
            if (request is not null && (!Shown(request.Assignment.Address) ||
                !EnbShortcutEquipmentActivation.TryResolve(request, _shortcuts, _shortcutShipOwner, null, out _)))
                _equipmentKeyPresses[key] = null;
        foreach (var (key, request) in _shortcutKeyPresses)
            if (request is not null && (_shortcuts?.IsIncapacitated == true ||
                !Shown(request.Assignment.Address) ||
                !EnbShortcutSkillActivation.TryResolve(request, _shortcuts, _shortcutShipOwner, out _)))
                _shortcutKeyPresses[key] = null;
        SpeedLocked = snapshot.Hud.LocalShipLockSpeedActive;
        _inventory = snapshot.Hud.Inventory;
        _interactive = interactive && _asset is not null;
        if (!_interactive) { SetShortcutShift(false); CancelPointerInput(); }
        IsVisible = _interactive;
        UpdateCombat(currentClientTime);
        Presentation = null;
        if (_asset is null || _layout is null || size.Width <= 0 || size.Height <= 0) return;
        var buffs = BuildBuffs(snapshot.Hud.Buffs, currentClientTime);
        foreach (string name in HitNames)
        {
            HitBoundsChanged?.Invoke(name, _hitBounds.TryGetValue(name,out Rect r)
                ? new Rect(r.X*size.Width/640,r.Y*size.Height/480,r.Width*size.Width/640,r.Height*size.Height/480) : null);
        }
        var text = ImmutableArray.CreateBuilder<EnbUiTextDraw>();
        AddText("SpeedDisplay", SpeedText(ship?.Spatial?.Transform), 0);
        var warp = _state?.Warp;
        AddText(warp?.Incapacitated == true ? "DISTRESS_CPIT" : "WARP_CPIT", null, 1); // 00584110 selects the installed incapacitation label.
        float Percent(float? value) => value is float v && float.IsFinite(v) ? Math.Clamp(v, 0, 1) : 0;
        var vitals = _state?.Vitals;
        var gauge = _warpGauge.UpdateVisual(ship?.RenderIdentity ?? 0, warp, currentClientTime, _state?.WarpCommand);
        Presentation = new(_asset, size, text.ToImmutable(),
            Percent(_state?.EnergyPercent),
            vitals?.MaximumHullPoints >= 1 ? Percent(vitals.HullPoints / vitals.MaximumHullPoints) : 0,
            Percent(vitals?.ShieldPercent),
            ship?.RenderIdentity ?? 0, _thrust, _interactive ? _hovered : null)
            { Shortcuts = BuildShortcuts(), Buffs = buffs,
              WarpFill = gauge.Fill, WarpRed = gauge.Red,
              WarpButtonEmission = gauge.ButtonEmission,
              WarpEngaged = warp?.IsEngaged == true, WarpEnabled = CanPressWarp };
        if (_interactive && _hovered is not null) ShowTooltip();
        else if (!interactive) ClearHover();
        UpdateSkillMenu();

        void AddText(string id, string? value, int index)
        {
            if (!_layout.TryResolve(id, out var def) || !def.Properties.TryGetValue("Attach", out var attach) ||
                !def.Properties.TryGetValue("FontType", out var fontId) || Font(fontId) is not { } font) return;
            var pivot = _asset.Hierarchy.Pivots.FirstOrDefault(p => p.Name.Equals(attach, StringComparison.OrdinalIgnoreCase));
            if (pivot is null) return;
            var anchor = _transforms[pivot.Ordinal].Translation;
            var line = _text[index];
            line.SetText(value ?? def.Properties.GetValueOrDefault("String0", ""), font,
                def.Properties.GetValueOrDefault("Color") == "FA_YELLOW" ? new(1, 1, .1f) : new(.1f, 1, .1f));
            line.SetDisplayScale(1,1);
            if(_hitBounds.TryGetValue(attach.ToUpperInvariant(),out var gadget))
                line.FitTo(new(gadget.Width*size.Width/640,gadget.Height*size.Height/480),_scaling);
            if (line.Raster is { } raster) text.Add(new(raster, (new Rect(
                (anchor.X + 320) * size.Width / 640 - line.Width / 2,
                (240 - anchor.Y) * size.Height / 480 - line.Height / 2, line.Width, line.Height))));
        }
    }

    internal static Rect MeshBounds(EnbAuthoredUiAsset asset, string name) => EnbAuthoredUiAsset.MeshBounds(asset,name);
    internal static Matrix4x4[] ResolveTransforms(EnbAuthoredUiAsset asset) => EnbAuthoredUiAsset.ResolveTransforms(asset);

    private EnbRetailFontFace? Font(string id)
    {
        if (_assets is null || !EnbRetailFontFactory.TryResolveDefinition(_assets, id,
            (int)(_size.Width * _scaling), (int)(_size.Height * _scaling), out var definition) || definition.Resource is null) return null;
        if (_fonts.TryGetValue(definition.Resource, out var font)) return font;
        if (!EnbRetailFontFactory.TryCreate(_assets, definition, out font)) return null;
        return _fonts[definition.Resource] = font;
    }

    internal void Hover(string name, Point pointer)
    {
        _pointer = pointer;
        if (!_interactive) return;
        if (!Contains(name, pointer)) { ClearHover(); return; }
        if (_hovered != name) { _dwell.Reset(); _expandedBuff=null; CaptureBuffHover(name); }
        _hovered = name; _pointer = pointer; ShowTooltip();
    }
    internal void ClearHover()
    {
        if (_hovered is null) return;
        _hovered = null; _expandedBuff=null; _shortcutHoverIdentity = null; _dwell.Reset(); TooltipChanged?.Invoke(null);
    }
    public void SetImmediateTooltips(bool value) { _dwell.Immediate = value; ShowTooltip(); }
    private void ShowTooltip()
    {
        if (_hovered is null || _tooltipDefinition is not { } def || _tooltipFrame is null || Font(def.FontId) is not { } font) return;
        string? shortcutMessage = _hovered=="AUTOPILOT" ? (_autopilotActive ? "Turn off autopilot." : "Turn on autopilot.") : _hovered=="DAMAGE" ? (DamageMeterShown ? "Hide damage meter." : "Show damage meter.") : BuffTooltip(_hovered) ?? ShortcutTooltip(_hovered);
        object? hoverIdentity = shortcutMessage;
        if (_hovered.StartsWith("BTTN_",StringComparison.Ordinal) &&
            SlotOf(_hovered) is { } slot && EnbShortcutEquipmentActivation.IsEquipment(slot))
            // Distance and item status are live tooltip content. They must not
            // restart hover dwell; changing the actual assignment/item still does.
            hoverIdentity = (_shortcutOwner,_shortcutShipOwner,slot.Assignment,slot.ItemTemplateId);
        if (!Equals(_shortcutHoverIdentity,hoverIdentity)) { _shortcutHoverIdentity = hoverIdentity; _dwell.Reset(); }
        int delay = EnbCurrentRetailTooltipDelay.Milliseconds(_assets!.ResolvePlayerOptions(Session()).Options.InterfaceTooltipDelay);
        int hoverId = _hovered.StartsWith("ABILITY_",StringComparison.Ordinal)
            ? 100 + int.Parse(_hovered.AsSpan(8),CultureInfo.InvariantCulture) : Array.IndexOf(HitNames, _hovered) + 1;
        if (_expandedBuff!=_hovered && !_dwell.IsReady(hoverId, Environment.TickCount64, delay))
        { TooltipChanged?.Invoke(null); return; }
        string key = _hovered switch { "BAR00" => "ENERGY", "BAR01" => "HULL", "BAR02" => "SHIELDS", _ => IsModernSlot(_hovered) ? $"BTTN_{_hovered[5]}_0{_hovered[^1]}" : _hovered };
        if (_hovered == "WARP") key = _state?.Warp?.TooltipKey ?? "CANNOT_WARP";
        string section = _hovered.StartsWith("BAR", StringComparison.Ordinal) ? "UI_POWER" : "UI_CPIT";
        var v = _state?.Vitals;
        float? max = key == "ENERGY" ? _state?.MaximumEnergyPower : key == "HULL" ? v?.MaximumHullPoints : v?.MaximumShieldPower;
        float? value = key == "ENERGY" ? _state?.EnergyPercent * max : key == "HULL" ? v?.HullPoints : v?.ShieldPercent * max;
        if (section == "UI_POWER")
        {
            max ??= 0; value ??= 0;
            // Shared 5DC4A0: only shields have a no-capacity tooltip branch.
            if(key=="SHIELDS" && !(max>0))key="NO_SHIELDS";
            else if(key=="HULL") {if(max>=1)value=Math.Clamp(value.Value,0,max.Value);else {value=0;max=0;}}
            else value=value<0?0:value>max?max:value;
        }
        if (_strings?.TryGetSection(section, out var strings) != true) return;
        string message = strings.GetValueOrDefault(key, "").Replace("\\n", "\n");
        message = shortcutMessage ?? message;
        if (_hovered == "WARP" && _state?.Warp?.State is int warpState)
            message += warpState switch { 6 => "\nWarp failed - local gravity interference.", 7 => "\nWarp target lost.",
                8 => "\nWarp target reached.", 9 => "\nWarp followers disrupted.", 11 => "\nWarp interrupted - out of energy.", _ => "" };
        foreach (float number in new[] { value ?? 0, max ?? 0 })
        {
            int pos = message.IndexOf("%d", StringComparison.Ordinal);
            if (pos >= 0) message = message[..pos] + ((int)number).ToString(CultureInfo.InvariantCulture) + message[(pos + 2)..];
        }
        var presentation=EnbTooltipContent.Create(_tooltipFrame,font,message,_size,_scaling,new(.1f,1,.1f),_pointer,i=>
        {
            while(_richTip.Count<=i)_richTip.Add(new());
            return _richTip[i];
        });
        // Next Modern UI: an action bar shortcut shows its ability's or item's card.
        if(Modern&&ModernShortcutCard is {} cards&&_hovered.StartsWith("BTTN_",StringComparison.Ordinal)&&SlotOf(_hovered) is {} cardSlot&&cards(cardSlot,message) is {} card)
            presentation=presentation with {Card=card};
        TooltipChanged?.Invoke(presentation);
    }

    public void Dispose()
    {
        _autopilotText.Clear();_damageMeterText.Clear();AutopilotDraw=null;DamageMeterDraw=null;
        Suspend(); foreach (var line in _text) line.Clear();
        foreach (var line in _richTip) line.Clear();
        _richTip.Clear();
        foreach (var label in _menuLabels) label.Clear();
        _menuLabels.Clear(); _menuPanel = null; _menuCircle = null;
        _fonts.Clear(); _assets = null; _asset = null; _layout = null; Presentation = null;
        _strings = null; _tooltipDefinition = null; _tooltipFrame = null; _transforms = [];
        _hitBounds.Clear(); _hitGeometry.Clear();
        DisposeShortcuts();
        DisposeBuffs();
    }
}
