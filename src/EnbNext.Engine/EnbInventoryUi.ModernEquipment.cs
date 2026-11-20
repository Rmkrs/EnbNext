using System.Globalization;
using EnbNext.Assets;
using EnbNext.Presentation;
using Rect = EnbNext.Presentation.EnbUiRect;

namespace EnbNext.Engine;

/// <summary>
/// Next Modern UI equipment window: the retail equipment mounts (same per ship, profession and level rules, through
/// PlanEquipmentSlots) at the Modern UI inventory's slot size: weapons on the left, devices on the right, shield,
/// reactor and engine under a small ship viewer, and the ship stats and deflects in a column of their own. Slots keep
/// the retail item requests, drop targets, tooltips, examine and drag; only the presentation is new.
/// </summary>
internal sealed partial class EnbInventoryUi
{
    private bool _modernEquipment;
    private Rect _modernEquipmentBounds;
    /// <summary>Saved top-left as a fraction of the screen; null centres the window above the action bar.</summary>
    internal EnbUiPoint? ModernEquipmentPosition { get; set; }
    internal Action<EnbUiPoint>? ModernEquipmentMoved { get; set; }
    public bool ModernEquipmentOpen => _modernEquipment;

    public bool ToggleModernEquipment()
    {
        if (!_interactive || !ModernUi) return false;
        _modernEquipment = !_modernEquipment;
        if (_modernEquipment) { RaiseModern("mequip"); Opened?.Invoke(); }
        InteractionSoundRequested?.Invoke("TextClick03.wav");
        Publish(); return true;
    }

    private string AuthoredLabel(string id, string fallback) => _cargoFonts.TryGetValue(id, out var definition) && definition.Text.Length > 0 ? definition.Text : fallback;

    private static readonly (string Key, string Title, string Fallback)[] ShipStatRows =
    [
        ("HullPoints", "HullTitle", "Hull Strength"), ("Defense", "DefenseTitle", "Defense"), ("MissileDefense", "MissileTitle", "Missile Defense"),
        ("Speed", "SpeedTitle", "Speed"), ("WarpSpeed", "WarpTitle", "Warp Speed"), ("TurnRate", "TurnTitle", "Turn Rate"),
        ("ScanRange", "ScanRangeTitle", "Scan Range"), ("Visibility", "VisibilityTitle", "Visibility"),
    ];
    private static readonly string[] DeflectRows = ["Impact", "Explosive", "Plasma", "Energy", "EMP", "Chemical", "Psionic"];

    private string ShipStatValue(string key)
    {
        string value = Data?.ShipStats.GetValueOrDefault(key, "") ?? "";
        if (key == "HullPoints") value += "/" + (Data?.ShipStats.GetValueOrDefault("MaxHullPoints", "") ?? "");
        if (key is "ScanRange" or "Visibility" && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double n)) value = (n / 1000).ToString("0.0", CultureInfo.InvariantCulture) + "k";
        return value;
    }
    /// <summary>Retail stat colours, except that the retail neutral blue is lightened to read on the Modern UI panel.</summary>
    private EnbRgb ModernStatColor(string key)
    {
        var color = StatColor(key);
        return color == new EnbRgb(.33f, .33f, .99f) ? new(.78f, .84f, 1) : color;
    }

    private void ModernEquipment()
    {
        if (Data is not { } data) return;
        double u = ModernUnit, text = 14 * u, small = 13 * u, title = 16 * u, gap = 4 * u, pad = 10 * u, row = 24 * u, header = 20 * u, line = 19 * u;
        double slot = ModernInventoryMetrics().Slot;
        // Measured stats column: the longest authored label plus the widest value it is likely to show.
        double labelWidth = 0;
        foreach (var (_, titleId, fallback) in ShipStatRows) labelWidth = Math.Max(labelWidth, ModernMeasure(AuthoredLabel("equip/" + titleId, fallback), small));
        foreach (string key in DeflectRows) labelWidth = Math.Max(labelWidth, ModernMeasure(AuthoredLabel("equip/Resist" + key + "Title", key), small));
        double valueWidth = Math.Max(ModernMeasure("999999/999999", small), ModernMeasure(ShipStatValue("HullPoints"), small));
        double statsWidth = Math.Ceiling(labelWidth + valueWidth + 30 * u);
        // The centre column holds the ship viewer over the three core mounts, as tall as the weapon and device columns.
        // The retail equipment camera projects 4:3 (00542EE0), so the viewer keeps that shape.
        double columnHeight = 6 * slot + 5 * gap, viewerHeight = Math.Floor(columnHeight - gap * 2 - slot - header), viewerWidth = Math.Floor(viewerHeight * 4 / 3);
        double coresX = (viewerWidth - (3 * slot + 2 * gap)) / 2;
        double statsNeeded = 8 * u + (header + 2 * u) * 2 + 15 * line + line * .6 + 8 * u;
        double statsHeight = Math.Max(header + columnHeight, statsNeeded);
        double contentWidth = slot * 2 + gap + gap * 2 + viewerWidth + gap * 2 + slot + gap * 3 + statsWidth;
        double width = pad * 2 + contentWidth, height = pad * 2 + row + gap + statsHeight;
        double x, y;
        if (ModernEquipmentPosition is { } at) { x = at.X * _size.Width; y = at.Y * _size.Height; }
        else { x = (_size.Width - width) / 2; y = _size.Height * .14; }
        x = Snap(Math.Clamp(x, 0, Math.Max(0, _size.Width - width))); y = Snap(Math.Clamp(y, 0, Math.Max(0, _size.Height - height)));
        _modernEquipmentBounds = new(x, y, width, height);
        ModernFrame("DIALOG_BOX_WINDOW", _modernEquipmentBounds, "mequip/frame");
        Flush();
        double cx = x + pad, cy = y + pad;
        // The ship's own name (its decal); an unnamed ship's window is simply "Equipment".
        string shipName = _scene?.Objects.FirstOrDefault(o => o.IsLocalShip)?.NameDecal?.Name?.Trim() ?? "";
        ModernText("mequip/title", shipName.Length > 0 ? shipName : "Equipment", new(cx, cy, contentWidth - row, row), new(1, .85f, .3f), title);
        ModernCloseButton("mequip/close", new(x + width - pad - row, cy, row, row));
        cy += row + gap;
        double weaponsX = cx, ammoX = weaponsX + slot + gap, viewerX = ammoX + slot + gap * 2, devicesX = viewerX + viewerWidth + gap * 2, statsX = devicesX + slot + gap * 3;
        var muted = new EnbRgb(.75f, .8f, .92f);
        ModernText("mequip/weapons", AuthoredLabel("equip/Title_Weapons", "Weapons"), new(weaponsX - gap, cy, slot + gap * 2, header), muted, small, 1);
        ModernText("mequip/ammo", "Ammo", new(ammoX - gap, cy, slot + gap * 2, header), muted, small, 1);
        ModernText("mequip/devices", AuthoredLabel("equip/Title_Devices", "Devices"), new(devicesX - gap, cy, slot + gap * 2, header), muted, small, 1);
        double top = cy + header;
        // Ship viewer: the retail equipment preview, small, over a Modern UI panel (drag to turn, wheel to zoom).
        var viewer = new Rect(viewerX, top, viewerWidth, viewerHeight);
        int before = _draws.Count;
        ModernFrame("DIALOG_BOX_INFO", viewer);
        var inner = Inset(viewer, 3 * u);
        if (_draws.Count > before) _draws[^1] = _draws[^1] with { EquipmentPreview = _scene, EquipmentBounds = inner, EquipmentClipBounds = inner, EquipmentView = _previewView, EquipmentRotation = _previewRotation };
        Hit("shipPreview", FromPixels(viewer));
        Flush();
        // Slots: the retail mount plan, placed in the Modern UI columns.
        string[] cores = ["Shields", "Reactor", "Engines"];
        foreach (var plan in PlanEquipmentSlots(data))
        {
            Rect r = plan.Id switch
            {
                "Shields" or "Reactor" or "Engines" => new(viewerX + coresX + plan.Index * (slot + gap), top + viewerHeight + gap * 2, slot, slot),
                _ when plan.Weapon => new(weaponsX, top + plan.Index * (slot + gap), slot, slot),
                _ => new(devicesX, top + plan.Index * (slot + gap), slot, slot),
            };
            r = new(Snap(r.X), Snap(r.Y), r.Width, r.Height);
            string id = "mequip/" + plan.Id;
            var design = FromPixels(r);
            if (plan.Future)
            {
                Draw("cargo/FRAME00", design, id);
                Flush();
                ImageTexture("im_blank.tga", new(design.X + 1, design.Y + 2, design.Width - 2, design.Height - 4), System.Numerics.Vector3.Zero);
                _tips[id] = _equipmentTips?.GetValueOrDefault(plan.Weapon ? "FUTURE_WEAPON" : "FUTURE_DEVICE", "") ?? "";
                continue;
            }
            var mount = plan.Mount!;
            if (plan.Weapon)
            {
                // Weapon and ammunition side by side. Both are the same mount: a drop on either is the retail drop on
                // the weapon (load ammunition / equip a weapon); the ammo slot carries the retail ammo layer, so
                // dragging it is the retail unload. Dragging a loaded weapon picks up its ammunition, as the retail
                // slot (ammo on top) did; a click or right-click on the weapon is the weapon (examine, auto fire).
                var weapon = data.Find(2, mount.Slot);
                var ammo = weapon is { IsWeapon: true } ? data.Find(2, mount.Slot, true) : null;
                string ammoId = "mequip/ammo" + plan.Index.ToString(CultureInfo.InvariantCulture);
                var ammoDesign = FromPixels(Snap(ammoX), r.Y, r.Width, r.Height);
                if (weapon is null) EmptyMount(id, design, plan, mount);
                else
                {
                    Slot(id, design, weapon, compact: true, weaponAmmoCount: false);
                }
                _dropSlots[id] = (2, mount.Slot);
                ModernSlotHighlight(id, design);
                if (weapon is { ItemType: 15 or 16 })
                {
                    if (ammo is { Count: > 0 })
                    {
                        Slot(ammoId, ammoDesign, ammo with { IsAmmoLayer = true }, compact: true);
                        // The ammo column is the ammunition itself; the weapon slot beside it names the weapon.
                        _tips[ammoId] = SlotTip(EnbNext.Rendering.EnbRetailItemTooltip.Description(ammo));
                    }
                    else { Draw("cargo/FRAME00", ammoDesign, ammoId); _tips[ammoId] = "Drop ammunition here to load " + weapon.Name + "."; }
                    _dropSlots[ammoId] = (2, mount.Slot);
                    ModernSlotHighlight(ammoId, ammoDesign);
                }
                continue;
            }
            var (item, level) = EquippedItem(data, mount);
            if (item is null) EmptyMount(id, design, plan, mount);
            else Slot(id, design, item, level, compact: true);
            _dropSlots[id] = (2, mount.Slot);
            ModernSlotHighlight(id, design);
        }
        Flush();
        double labelsY = top + viewerHeight + gap * 2 + slot;
        for (int i = 0; i < 3; i++)
            ModernText("mequip/core" + i, AuthoredLabel("equip/Title_" + cores[i], cores[i]), new(viewerX + coresX + i * (slot + gap) - gap, labelsY, slot + gap * 2, header), muted, small, 1);
        // Ship stats and deflects: their own column.
        var stats = new Rect(statsX, cy, statsWidth, statsHeight);
        ModernFrame("DIALOG_BOX_INFO", stats);
        Flush();
        double sx = statsX + 12 * u, sw = statsWidth - 24 * u, sy = cy + 8 * u;
        ModernText("mequip/statsTitle", AuthoredLabel("equip/TITLE00_TSI07A", "Ship Stats"), new(sx, sy, sw, header), new(1, .85f, .3f), text);
        sy += header + 2 * u;
        foreach (var (key, titleId, fallback) in ShipStatRows)
        {
            ModernText("mequip/stat/" + key, AuthoredLabel("equip/" + titleId, fallback), new(sx, sy, sw, line), muted, small);
            ModernText("mequip/value/" + key, ShipStatValue(key), new(sx, sy, sw, line), ModernStatColor(key), small, 2);
            StatBreakdown("mequip/stat/" + key, key, AuthoredLabel("equip/" + titleId, fallback), new(sx - 4 * u, sy, sw + 8 * u, line));
            sy += line;
        }
        sy += line * .6;
        ModernText("mequip/deflectsTitle", AuthoredLabel("equip/TITLE01_TSI07A", "Deflects"), new(sx, sy, sw, header), new(1, .85f, .3f), text);
        sy += header + 2 * u;
        foreach (string key in DeflectRows)
        {
            ModernText("mequip/resist/" + key, AuthoredLabel("equip/Resist" + key + "Title", key), new(sx, sy, sw, line), muted, small);
            ModernText("mequip/resistValue/" + key, data.ShipStats.GetValueOrDefault("Resist" + key, "") ?? "", new(sx, sy, sw, line), ModernStatColor("Resist" + key), small, 2);
            StatBreakdown("mequip/resist/" + key, "Resist" + key, AuthoredLabel("equip/Resist" + key + "Title", key), new(sx - 4 * u, sy, sw + 8 * u, line));
            sy += line;
        }
        Flush();
    }

    /// <summary>An empty mount: the slot frame with the retail mount symbol centred at its own proportions.</summary>
    private void EmptyMount(string id, Rect design, EquipmentSlotPlan plan, EnbEquipmentMountSnapshot mount)
    {
        Draw("cargo/FRAME00", design, id);
        if (_art.TryGetValue("equip/" + plan.Icon, out var icon))
        {
            double scale = Math.Min(design.Width * .7 / Math.Max(1e-6, icon.Design.Width), design.Height * .7 / Math.Max(1e-6, icon.Design.Height));
            double w = icon.Design.Width * scale, h = icon.Design.Height * scale;
            Draw("equip/" + plan.Icon, new(design.Center.X - w / 2, design.Center.Y - h / 2, w, h));
        }
        _tips[id] = (_inventorySlotTips?.GetValueOrDefault("NORMAL_SLOT", "") ?? "").Replace("\\n", "\n").Replace("%s", EnbNext.Rendering.EnbRetailItemTooltip.EmptyDescription(mount.ItemTypeMask));
    }

    /// <summary>
    /// A stat row's tooltip: the server sends each stat's hull value as "Base.&lt;stat&gt;", so the tooltip shows the
    /// ship's own value and what equipment, skills and buffs add together (not per item: that is not sent).
    /// </summary>
    private void StatBreakdown(string id, string key, string label, Rect row)
    {
        if (Data?.ShipStats is not { } stats) return;
        Hit(id, FromPixels(row));
        bool Number(string name, out double value) => double.TryParse(stats.GetValueOrDefault(name, ""), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        string Format(double v) => key is "ScanRange" or "Visibility" ? (v / 1000).ToString("0.0", CultureInfo.InvariantCulture) + "k" : v.ToString("0.##", CultureInfo.InvariantCulture);
        if (!Number(key, out double total) || !Number("Base." + key, out double hull)) { _tips[id] = label; return; }
        double added = total - hull;
        _tips[id] = label + "\nShip: " + Format(hull) + "\nEquipment, skills and buffs: " + (added >= 0 ? "+" : "-") + Format(Math.Abs(added)) + "\nTotal: " + Format(total);
    }

    private bool ActivateModernEquipment(string id, bool right)
    {
        if (!id.StartsWith("mequip/", StringComparison.Ordinal)) return false;
        if (!right && id == "mequip/close") { _modernEquipment = false; InteractionSoundRequested?.Invoke("TextClick01.wav"); }
        return true;
    }
}
