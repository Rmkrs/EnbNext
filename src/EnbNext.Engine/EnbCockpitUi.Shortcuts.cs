using Point = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbCockpitUi
{
    //614360 reconstructs a gadget on successful assignment, including equal
    //source values. Snapshot publication preserves the assignment reference.
    private readonly ConditionalWeakTable<EnbRetailShortcutEntry,object> _shortcutGadgetOwners=new();
    public event Action<byte, int, Point>? ItemExamined;
    public EnbShortcutItemAssignmentRequest? CaptureItemDrop(EnbInventoryItemRequest item,Point point)
    {
        // 0059F470 gives only SHIP/PDA cargo and equipment panels a shortcut UI; 005A0740 records
        // the dragged slot (00685610), so a loaded weapon's ammunition layer names its equipment slot.
        if(!_interactive||_shortcuts is null||item.ShipOwner!=_shortcutShipOwner||item.Inventory is not (1 or 2)||_size.Width<=0||_size.Height<=0)return null;
        if(item.AmmoLayer)
        {
            if(_inventory?.Find(2,item.Slot) is not {} weapon)return null;
            item=item with {AmmoLayer=false,TemplateId=weapon.TemplateId};
        }
        return ShortcutDropAddress(point) is {} address?new(item,_shortcuts.Saved,address):null;
    }
    public EnbShortcutAssignmentRequest? CaptureSkillDrop(uint character,ulong ship,int ability,Point point)
    {
        if(!_interactive||_shortcuts is null||ship!=_shortcutShipOwner||_size.Width<=0||_size.Height<=0)return null;
        return ShortcutDropAddress(point) is {} address?new(character,ship,_shortcuts.Saved,address,ability):null;
    }
    private static readonly string[] ShortcutHitNames = ["BANK_0", "BANK_1",
        "BTTN_0_00", "BTTN_0_01", "BTTN_0_02", "BTTN_1_00", "BTTN_1_01", "BTTN_1_02"];
    private readonly EnbShortcutPages _pages = new();
    private EnbSceneShortcutSnapshot? _shortcuts;
    private EnbSceneInventorySnapshot? _inventory;
    private string? _shortcutOwner;
    private ulong _shortcutShipOwner;
    private EnbShortcutSkillRequest? _pressedSkill;
    private EnbShortcutEquipmentRequest? _pressedEquipment;
    public event Action<EnbShortcutEquipmentRequest>? EquipmentRequested;
    private readonly Dictionary<int, EnbShortcutEquipmentRequest?> _equipmentKeyPresses = [];
    public event Action<EnbShortcutSkillRequest>? SkillRequested;
    public event Action? IncapacitatedShortcutPressed;
    private readonly Dictionary<int, EnbShortcutSkillRequest?> _shortcutKeyPresses = [];

    public void PressShortcutKey(int key, int slot)
    {
        if (slot is < 0 or > 11 || slot > 5 && !Modern || _shortcutKeyPresses.ContainsKey(key)) return;
        // Next Modern UI: each key names its own slot of the twelve on the bar; retail resolves the visible bank.
        var target = Modern ? SlotAt(ModernAddress(slot)) : VisibleSlot(slot / 3, slot % 3);
        var request = _interactive && _menuRequest is null
            ? EnbShortcutSkillActivation.Capture(_shortcuts, target, _shortcutShipOwner) : null;
        // Keyboard's outer retail proxy rejects incapacitation before the
        // underlying skill handler (006120E0); mouse retains its ability exception.
        // 006120E0 reports the rejected press of an occupied slot as local
        // message type 0x11.
        if (_shortcuts?.IsIncapacitated == true && _interactive && _menuRequest is null &&
            target?.Assignment is not null)
            IncapacitatedShortcutPressed?.Invoke();
        _shortcutKeyPresses[key] = _shortcuts?.IsIncapacitated == true ? null : request;
        _equipmentKeyPresses[key] = _interactive && _menuRequest is null && _shortcuts?.IsIncapacitated != true
            ? EnbShortcutEquipmentActivation.Capture(_shortcuts, target, _shortcutShipOwner) : null;
        // 6120E0 sends the press through 670B20, whose renderer+3C callback rearms notification.
        ArmEquipmentNotification(_equipmentKeyPresses[key]);
    }

    // SKILL-D13: 00611F60 rebinds the bank's proxies but keeps their key latch (+6C);
    // the admitted release (0066B690/006120E0) goes to the gadget now bound there.
    private void RebindShortcutKeys()
    {
        foreach (int key in _shortcutKeyPresses.Keys)
        {
            var address = _shortcutKeyPresses[key]?.Assignment.Address ?? _equipmentKeyPresses.GetValueOrDefault(key)?.Assignment.Address;
            if (address is not {} held) continue;
            var slot = Modern ? SlotAt(held) : VisibleSlot(held.Bar, held.Button);
            bool admitted = _interactive && _menuRequest is null && _shortcuts?.IsIncapacitated != true;
            _shortcutKeyPresses[key] = admitted ? EnbShortcutSkillActivation.Capture(_shortcuts, slot, _shortcutShipOwner) : null;
            _equipmentKeyPresses[key] = admitted ? EnbShortcutEquipmentActivation.Capture(_shortcuts, slot, _shortcutShipOwner) : null;
        }
    }

    public void SuppressShortcutKey(int key) { _shortcutKeyPresses[key] = null; _equipmentKeyPresses[key] = null; }

    public bool ReleaseShortcutKey(int key)
    {
        // 006120E0 supplies event+2 bit 0x1000 only on release;
        // 0062D360 / 0062D400 require it for synthetic skill activation.
        if (!_shortcutKeyPresses.Remove(key, out var request)) return false;
        if (_equipmentKeyPresses.Remove(key, out var equipment) && equipment is not null)
            DispatchEquipment(equipment);
        if (_interactive && _menuRequest is null && _shortcuts?.IsIncapacitated != true && request is not null &&
            Shown(request.Assignment.Address) &&
            EnbShortcutSkillActivation.TryResolve(request, _shortcuts, _shortcutShipOwner, out _))
            SkillRequested?.Invoke(request);
        return true;
    }

    public void CancelShortcutKeys()
    {
        // Keep held physical keys suppressed until their releases; no repeat
        // may re-arm an activation after focus/loading/page/identity cancellation.
        foreach (int key in _shortcutKeyPresses.Keys) _shortcutKeyPresses[key] = null;
        foreach (int key in _equipmentKeyPresses.Keys) _equipmentKeyPresses[key] = null;
    }

    private void DispatchEquipment(EnbShortcutEquipmentRequest request)
    {
        if (!_interactive || _menuRequest is not null || !Shown(request.Assignment.Address))
            return;
        if (EnbShortcutEquipmentActivation.TryResolve(request, _shortcuts, _shortcutShipOwner, null, out _))
        {
            EquipmentRequested?.Invoke(request); // facade resolves the current target under the mutation lock
        }
        // 005A0740: using an item while incapacitated reports local type 0x11
        // instead of sending 0x005D.
        else if (_shortcuts is { IsIncapacitated: true } incapacitated &&
            EnbShortcutEquipmentActivation.TryResolve(request, incapacitated with { IsIncapacitated = false }, _shortcutShipOwner, null, out _))
        {
            IncapacitatedShortcutPressed?.Invoke();
        }
    }

    public void SetShortcutShift(bool shifted)
    {
        if ((shifted && !_interactive) || !_pages.SetShift(shifted)) return;
        CancelPointerInput();
    }
    private object? _shortcutHoverIdentity;
    private EnbAuthoredUiAsset? _shortcutAsset;
    private EnbInterfaceLayoutCatalog? _shortcutLayout;
    private Matrix4x4[] _shortcutTransforms=[];
    private Rect _iconBounds;
    private EnbRetailIniDocument? _fontColors;
    private readonly EnbRetailText[] _shortcutLabels = [new(),new(),new(),new(),new(),new()];
    // Static texture preparation, cached across snapshots and page changes. All drawing is GPU-backed.
    private readonly Dictionary<(string Resource, Vector3 Tint), EnbUiRaster?> _shortcutIcons = new();

    private void LoadShortcuts(EnbRetailAssetBundle assets)
    {
        _shortcutAsset = EnbAuthoredUiAsset.Load(assets, "tsi01a_short.w3d");
        if (_shortcutAsset is null) return;
        _shortcutTransforms = ResolveTransforms(_shortcutAsset);
        foreach (string name in ShortcutHitNames)
        {
            _hitBounds[name] = MeshBounds(_shortcutAsset, name);
            var mesh = _shortcutAsset.Resource.Meshes.First(m => m.Name == name);
            var transform = _shortcutTransforms[_shortcutAsset.Hierarchy.Pivots.First(p => p.Name == name).Ordinal];
            _hitGeometry[name] = (mesh.Positions.Select(p => {
                var v = Vector3.Transform(p,transform); return new Vector2(v.X+320,240-v.Y);
            }).ToArray(), mesh.Indices);
        }
        _shortcutLayout = EnbInterfaceLayoutCatalog.Load(Path.Combine(assets.Paths.ClientIniDirectory,"fc_scut.ini"));
        _fontColors = EnbRetailIniDocument.Load(Path.Combine(assets.Paths.ClientIniDirectory,"fontart.ini"));
        // Payload vtable +48 -> 0068B4D0 returns InvItem, not the inv_blank01 overlay mask.
        if (assets.RetailMixResources?.TryReadResource("invitem.w3d", out var bytes, out _) == true &&
            EnbW3dModel.TryDecode(bytes, out var model) && model.Meshes.FirstOrDefault() is { } icon)
        {
            var min = icon.Vertices.Aggregate(Vector3.Min); var max = icon.Vertices.Aggregate(Vector3.Max);
            _iconBounds = new(min.X, -max.Y, max.X-min.X, max.Y-min.Y);
        }
    }

    private EnbShortcutDraw? BuildShortcuts()
    {
        if (_shortcutAsset is null || _shortcutLayout is null) return null;
        var icons = ImmutableArray.CreateBuilder<EnbUiTextDraw>();
        var labels = ImmutableArray.CreateBuilder<EnbUiTextDraw>();
        double sx = _size.Width / 640, sy = _size.Height / 480;
        for (int bar = 0; bar < 2; bar++)
            for (int button = 0; button < 3; button++)
            {
                int index = bar*3+button;
                string name = $"BTTN_{bar}_0{button}";
                var anchor = _shortcutTransforms[_shortcutAsset.Hierarchy.Pivots.First(p => p.Name == name).Ordinal].Translation;
                var slot = VisibleSlot(bar,button);
                if (!(_shortcutDragging&&slot?.Assignment==_shortcutDrag?.Source)&&slot?.Resolution == EnbSceneShortcutResolution.Resolved && _iconBounds.Width > 0 && Icon(slot) is { } raster)
                    icons.Add(new(raster, new((anchor.X+320+_iconBounds.X)*sx, (240-anchor.Y+_iconBounds.Y)*sy,
                        _iconBounds.Width*sx, _iconBounds.Height*sy))
                        { GadgetOwner=slot.Assignment is {} assignment?_shortcutGadgetOwners.GetValue(assignment,static _=>new object()):null,
                          Highlight = _interactive && _hovered == name && (EnbShortcutSkillActivation.IsSkill(slot) || EnbShortcutEquipmentActivation.IsItem(slot))
                            ? Icon(slot with { IconTint = Vector3.One }) : null });
                if (!_shortcutLayout.TryResolve("SCUT_NUMBER_"+index, out var def) ||
                    Font(def.Properties.GetValueOrDefault("FontType", "")) is not { } font) continue;
                var offset = Vector(def.Properties.GetValueOrDefault("Offset", "0,0"));
                var color = Vector3.One;
                if (_fontColors is { } colors)
                    foreach (string section in colors.SectionNames)
                        if (colors.TryGetSection(section,out var values) && values.GetValueOrDefault("ColorID") == def.Properties.GetValueOrDefault("Color"))
                        { color = Vector(values.GetValueOrDefault("RGB", "255,255,255"))/255; break; }
                var text = _shortcutLabels[index];
                text.SetText(def.Properties.GetValueOrDefault("String0", ""),font,new(color.X,color.Y,color.Z));
                text.SetDisplayScale(1,1);
                if(_hitBounds.TryGetValue(def.Properties["Attach"].ToUpperInvariant(),out var gadget))
                    text.FitTo(new(gadget.Width*sx,gadget.Height*sy),_scaling);
                if (text.Raster is { } label) labels.Add(new(label,new(
                    (anchor.X+320+offset.X*640)*sx-text.Width/2,
                    (240-anchor.Y-offset.Y*480)*sy-text.Height/2,text.Width,text.Height)));
            }
        return new(_shortcutAsset,_pages.Group(0),_pages.Group(1),_pages.CanToggle(0),_pages.CanToggle(1),icons.ToImmutable(),labels.ToImmutable()) {Combat=_combatIcons,Drag=BuildShortcutDrag()};
    }

    private EnbSceneShortcutSlotSnapshot? VisibleSlot(int bar, int button) => _shortcuts?.Slots.FirstOrDefault(
        s => s.Address == new EnbRetailShortcutAddress(bar,_pages.Group(bar),button));

    private EnbUiRaster? Icon(EnbSceneShortcutSlotSnapshot slot)
    {
        string? resource = slot.IconResource;
        if (resource is null && slot.IconBaseAsset is int id)
            resource = _assets!.PresentationCatalog?.ResolveBaseAsset(id).Properties?.GetValueOrDefault("Icon");
        if (string.IsNullOrWhiteSpace(resource)) return null;
        resource = resource.Trim().Trim('"').ToLowerInvariant();
        var key = (resource,slot.IconTint);
        if (_shortcutIcons.TryGetValue(key,out var raster)) return raster;
        raster = null;
        if (_assets!.Textures.Resolve(resource).Image is { } image)
        {
            var pixels = ImmutableArray.CreateBuilder<byte>(image.Width*image.Height*4);
            foreach (var p in image.Pixels.Span)
            {
                pixels.Add((byte)Math.Clamp(p.B*slot.IconTint.Z,0,255));
                pixels.Add((byte)Math.Clamp(p.G*slot.IconTint.Y,0,255));
                pixels.Add((byte)Math.Clamp(p.R*slot.IconTint.X,0,255)); pixels.Add(p.A);
            }
            raster = new(image.Width,image.Height,pixels.MoveToImmutable());
        }
        return _shortcutIcons[key] = raster;
    }

    private string? ShortcutTooltip(string name)
    {
        if (name.StartsWith("ABILITY_",StringComparison.Ordinal))
        {
            int index = int.Parse(name.AsSpan(8),CultureInfo.InvariantCulture);
            return (uint)index < _menuChoices.Length && _strings?.TryGetSection("UI_SKILL_INFO",out var rowTemplates) == true
                ? rowTemplates.GetValueOrDefault("ABILITY","%s").Replace("\\n","\n").Replace("%s",_menuChoices[index].Description) : null;
        }
        if (name.StartsWith("BANK_",StringComparison.Ordinal))
        {
            int bar = name[^1]-'0';
            return _strings?.TryGetSection("UI_SCUT",out var section) == true
                ? section.GetValueOrDefault("GROUP_"+_pages.Group(bar),"").Replace("\\n","\n") : null;
        }
        if (!name.StartsWith("BTTN_",StringComparison.Ordinal)) return null;
        if (_menuSlotName == name && _strings?.TryGetSection("GD_SLOT_DATA",out var openTemplates) == true)
            return openTemplates.GetValueOrDefault("LOC_SHORTCUT_OPEN","").Replace("\\n","\n");
        var slot = SlotOf(name);
        return DescribeShortcut(slot);
    }
    private string? DescribeShortcut(EnbSceneShortcutSlotSnapshot? slot)
    {
        if (slot is null || slot.Resolution == EnbSceneShortcutResolution.Empty) return null; // Empty SlotRender virtual 28 (006A8610) supplies no tooltip key.
        if (EnbShortcutSkillActivation.IsSkill(slot) && _assets?.ResolveShortcutSkills() is { } catalog &&
            catalog.FindAbility(slot.Name) is { } ability && _strings?.TryGetSection("GD_SLOT_DATA", out var templates) == true)
            return templates.GetValueOrDefault("LOC_SHORTCUT", "%s").Replace("\\n", "\n")
                .Replace("%s", catalog.Describe(ability, slot.SkillLevel ?? 0));
        if (EnbShortcutEquipmentActivation.IsEquipment(slot))
            return _inventory?.Find(2,slot.Assignment!.SavedIndex!.Value) is {} item ? EquipmentCombatTooltip(item,_weaponFeedback.GetValueOrDefault(slot.Address).RangeValue) : slot.Name;
        if (slot.Assignment is { Source: EnbRetailShortcutSource.Cargo, SavedIndex: {} cargoIndex } &&
            slot.Resolution == EnbSceneShortcutResolution.Resolved && _inventory?.Find(1, cargoIndex) is {} cargo)
        {
            // Shortcut factory 00631000 -> 006A90A0(mode 1), cargo renderer
            // 0068D870 / AFCCA0+28 -> 0068CCC0 -> GD_SLOT_DATA/NORMAL_SLOT.
            string description = ItemTooltipDescription(cargo);
            return SlotTooltip("NORMAL_SLOT", description);
        }
        if (slot.Resolution == EnbSceneShortcutResolution.EmptyInventorySlot && slot.Assignment is { SavedIndex: {} itemIndex } assignment)
        {
            uint? mask = assignment.Source == EnbRetailShortcutSource.Cargo
                ? slot.ItemTemplateId == -2 ? 0u : uint.MaxValue
                : _inventory?.Mounts.FirstOrDefault(m => m.Slot == itemIndex)?.ItemTypeMask;
            if (mask is {} value)
            {
                string description = value switch { 0 => "Unused Slot", 4 => "Shields Slot", 0x40 => "Engine Slot", 0x80 => "Reactor Slot",
                    uint.MaxValue => "Empty Slot", _ => (value & 0x1c022) != 0 ? "Weapon Slot" : (value & 0x20809) != 0 ? "Device Slot" : "Empty Slot" };
                return SlotTooltip(assignment.Source == EnbRetailShortcutSource.Equipment ? "WEAPON_ON" : "NORMAL_SLOT", description);
            }
        }
        // An unresolved renderer cannot supply the native description/template.
        return null;
    }

    private static string ItemTooltipDescription(EnbSceneItemSnapshot cargo)
        => EnbRetailItemTooltip.Description(cargo);

    private string? SlotTooltip(string key, string description) => _strings?.TryGetSection("GD_SLOT_DATA", out var templates) == true
        ? templates.GetValueOrDefault(key, "").Replace("\\n", "\n").Replace("%s", description) : null;

    private static Vector3 Vector(string value)
    {
        var parts = value.Split(',');
        float Part(int i) => i < parts.Length && float.TryParse(parts[i],NumberStyles.Float,CultureInfo.InvariantCulture,out var n) && float.IsFinite(n) ? n : 0;
        return new(Part(0),Part(1),Part(2));
    }

    private void DisposeShortcuts()
    {
        _shortcutAsset = null; _shortcutLayout = null; _fontColors = null; _shortcuts = null; _inventory = null; _shortcutOwner = null;
        _iconBounds = default; _shortcutTransforms = []; _shortcutIcons.Clear(); _pages.Update(null);
        _shortcutShipOwner = 0; _pressedSkill = null;
        foreach (var label in _shortcutLabels) label.Clear();
        _dragLayout=null;_dragLevelText.Clear();
    }
}
