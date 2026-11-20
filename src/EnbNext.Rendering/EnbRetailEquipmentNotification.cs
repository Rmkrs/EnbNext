namespace EnbNext.Rendering;

/// <summary>Action-slot notification state: 68DC20, 68E340 and 68E360.</summary>
public sealed class EnbRetailEquipmentNotification
{
    public uint State { get; private set; }
    public bool Armed { get; private set; } = true;

    public void Arm() => Armed = true;

    public string? Update(uint itemType, uint state)
    {
        bool notify = Armed && (State & 1) != (state & 1);
        State = state;
        if (!notify) return null;
        Armed = false; // Even an unsupported type consumes the notification.
        return itemType switch
        {
            0 or 12 => (state & 1) != 0 ? "shortcut_activate_ability_sn_1.wav" : "shortcut_deactivate_ability.wav",
            1 or 5 or 14 or 15 or 16 or 17 => (state & 1) != 0 ? "shortcut_activate_weapon.wav" : "shortcut_deactivate_weapon.wav",
            _ => null
        };
    }
}
