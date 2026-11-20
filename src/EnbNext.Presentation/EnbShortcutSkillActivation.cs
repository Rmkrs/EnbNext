using EnbNext.Assets;

namespace EnbNext.Presentation;

/// <summary>Identity captured at pointer-down, never an optimistic gameplay-state change.</summary>
public sealed record EnbShortcutSkillRequest(string CharacterKey, ulong ShipOwner,
    EnbRetailShortcutEntry Assignment, int FamilyId, int AbilityId);

public static class EnbShortcutSkillActivation
{
    // 0062BC40 filters by current family rank; 0062C910 changes the shortcut only.
    public static EnbRetailSkillAbility? ResolveSelection(EnbShortcutSkillRequest request, int choiceId,
        EnbSceneShortcutSnapshot? current, ulong owner, EnbRetailSkillCatalog catalog)
    {
        var slot = current?.Slots.FirstOrDefault(s => s.Address == request.Assignment.Address);
        if (Capture(current, slot, owner) != request || slot?.SkillLevel is not int level) return null;
        return catalog.Choices(slot.Assignment!.FamilyName!, level).FirstOrDefault(a => a.Id == choiceId);
    }

    public static bool IsSkill(EnbSceneShortcutSlotSnapshot? slot) => slot is
    {
        Resolution: EnbSceneShortcutResolution.Resolved,
        Assignment.Source: EnbRetailShortcutSource.Skill,
        Assignment.SavedIndex: not null,
        FamilyId: >= 0 and < 64,
        AbilityId: >= 0 and < 170,
    } && slot.Address.IsValid && slot.Assignment.Address == slot.Address;

    public static EnbShortcutSkillRequest? Capture(EnbSceneShortcutSnapshot? shortcuts,
        EnbSceneShortcutSlotSnapshot? slot, ulong shipOwner)
        => shortcuts?.Saved.CharacterKey is { } character && shipOwner != 0 && IsSkill(slot)
            ? new(character, shipOwner, slot!.Assignment!, slot.FamilyId!.Value, slot.AbilityId!.Value) : null;

    public static bool TryResolve(EnbShortcutSkillRequest request, EnbSceneShortcutSnapshot? current,
        ulong shipOwner, out uint abilityId)
    {
        abilityId = 0;
        if (shipOwner == 0 || request.ShipOwner != shipOwner ||
            current?.Saved.CharacterKey != request.CharacterKey) return false;
        var slot = current.Slots.FirstOrDefault(s => s.Address == request.Assignment.Address);
        if (!IsSkill(slot) || slot!.Assignment != request.Assignment || slot.FamilyId != request.FamilyId ||
            slot.AbilityId != request.AbilityId) return false;
        // Current retail 0062D730: HasAbility, level, Validity, range and recharge
        // are not send-time predicates. The sole gameplay-local rejection is
        // incapacitated AND !CanBeUsedWhileIncapacitated (00750B60 / 00813E10).
        if (current.IsIncapacitated && !slot.CanBeUsedWhileIncapacitated) return false;
        abilityId = (uint)slot.AbilityId!.Value;
        return true;
    }
}
