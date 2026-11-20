using EnbNext.Assets;
using EnbNext.Net7.Gameplay;

namespace EnbNext.Presentation;

public sealed record EnbShortcutEquipmentRequest(string CharacterKey, ulong ShipOwner,
    EnbRetailShortcutEntry Assignment, int ItemTemplateId);

/// <summary>Saved equipped-slot identity; gameplay results remain server-owned.</summary>
public static class EnbShortcutEquipmentActivation
{
    public static bool IsEquipment(EnbSceneShortcutSlotSnapshot? slot) => slot is
    {
        Resolution: EnbSceneShortcutResolution.Resolved,
        Assignment.Source: EnbRetailShortcutSource.Equipment,
        Assignment.SavedIndex: >= 0 and <= byte.MaxValue,
        ItemTemplateId: >= 0, StackCount: > 0,
    } && slot.Address.IsValid && slot.Assignment.Address == slot.Address;

    /// <summary>005A0740/0062E8B0: a Cargo shortcut is used (005D, inventory 1) and examined like an Equipment one (INV-D37).</summary>
    public static bool IsCargo(EnbSceneShortcutSlotSnapshot? slot) => slot is
    {
        Resolution: EnbSceneShortcutResolution.Resolved,
        Assignment.Source: EnbRetailShortcutSource.Cargo,
        Assignment.SavedIndex: >= 0 and <= byte.MaxValue,
        ItemTemplateId: >= 0, StackCount: > 0,
    } && slot.Address.IsValid && slot.Assignment.Address == slot.Address;

    public static bool IsItem(EnbSceneShortcutSlotSnapshot? slot) => IsEquipment(slot) || IsCargo(slot);

    public static EnbShortcutEquipmentRequest? Capture(EnbSceneShortcutSnapshot? current,
        EnbSceneShortcutSlotSnapshot? slot, ulong owner)
        => current?.Saved.CharacterKey is { } character && owner != 0 && IsItem(slot)
            ? new(character, owner, slot!.Assignment!, slot.ItemTemplateId!.Value) : null;

    public static bool TryResolve(EnbShortcutEquipmentRequest request, EnbSceneShortcutSnapshot? current,
        ulong owner, uint? clientShip, out Net7UseInventoryItemRequest packet)
    {
        packet = default!;
        var slot = current?.Slots.FirstOrDefault(s => s.Address == request.Assignment.Address);
        if (Capture(current, slot, owner) != request || current!.IsIncapacitated) return false;
        // 0059FE30 synthetic shortcut release: no effect/range/cooldown predicate.
        // 0059FEFA reads SClient+1130, the 0047 Client Ship (INV-D102), including zero before one.
        packet = new(clientShip ?? 0, (byte)(request.Assignment.Source == EnbRetailShortcutSource.Cargo ? Net7InventoryDomain.CargoInventory : Net7InventoryDomain.EquippedInventory),
            checked((byte)request.Assignment.SavedIndex!.Value));
        return true;
    }
}
