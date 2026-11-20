using System.Collections.Immutable;
using EnbNext.Assets;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.World;

namespace EnbNext.Presentation;

public sealed record EnbShortcutAssignmentRequest(uint Character, ulong ShipOwner, EnbRetailShortcuts Expected, EnbRetailShortcutAddress Address, int AbilityId);
public sealed record EnbShortcutItemAssignmentRequest(EnbInventoryItemRequest Item, EnbRetailShortcuts Expected, EnbRetailShortcutAddress Address);
public sealed record EnbShortcutMoveRequest(uint Character, ulong ShipOwner, EnbRetailShortcuts Expected,
    EnbRetailShortcutEntry Source, EnbRetailShortcutAddress? Destination);

public sealed record EnbSceneSkillFeedbackSnapshot(uint Validity, uint Targets, int Range,
    int RecycleTime, long LastActivation, int PowerupAbility, long PowerupStart, long PowerupEnd);

public enum EnbSceneShortcutResolution
{
    Empty, ConflictingAssignments, MalformedKey, InventoryPending, EmptyInventorySlot,
    ItemTemplatePending, SkillCatalogUnavailable, UnknownFamily, UnknownAbility, Resolved,
}

/// <summary>Detached identity/presentation only. Resolution is NOT permission to activate.</summary>
public sealed record EnbSceneShortcutSlotSnapshot(EnbRetailShortcutAddress Address,
    EnbRetailShortcutEntry? Assignment, EnbSceneShortcutResolution Resolution)
{
    public string? Name { get; init; }
    public int? ItemTemplateId { get; init; }
    public int? StackCount { get; init; }
    public int? IconBaseAsset { get; init; }
    public string? IconResource { get; init; }
    public int? FamilyId { get; init; }
    public int? AbilityId { get; init; }
    public int? SkillLevel { get; init; }
    public bool? HasAbility { get; init; }
    public bool CanBeUsedWhileIncapacitated { get; init; }
    public EnbSceneSkillFeedbackSnapshot? SkillFeedback { get; init; }
    public System.Numerics.Vector3 IconTint { get; init; } = System.Numerics.Vector3.One;
}

public sealed record EnbSceneShortcutSnapshot(EnbRetailShortcuts Saved,
    ImmutableArray<EnbSceneShortcutSlotSnapshot> Slots)
{
    // Retail SlotData dirty roots: 688B50/689100 and 6A4F00.
    public (ulong Equipped, ulong Ammo) EquipmentRevision { get; init; }
    public (ulong Skills, ulong Abilities) SkillRevision { get; init; }
    public bool IsIncapacitated { get; init; }
    /// <summary>The ability being powered up (SkillPowerupAbilityNumber, Start, End), whether or not it is on a bar.</summary>
    public (int Ability, long Start, long End) Powerup { get; init; }
    public int ResolvedCount => Slots.Count(s => s.Resolution == EnbSceneShortcutResolution.Resolved);
    public int AssignedCount => Slots.Count(s => s.Resolution != EnbSceneShortcutResolution.Empty);
}

public static class EnbSceneShortcutSnapshotBuilder
{
    /// <summary>Call inside the same mutation boundary as the main presentation snapshot.</summary>
    public static EnbSceneShortcutSnapshot Build(EnbRetailShortcuts saved, EnbRetailSkillCatalog? skills,
        Net7AuxDataSchema? ship, Net7AuxDataSchema localPlayer, Net7ItemTemplateCatalog? templates)
    {
        var result = ImmutableArray.CreateBuilder<EnbSceneShortcutSlotSnapshot>(12);
        var inventory = ship?.Name == "ShipAuxData" ? Child(ship.Root, "Inventory") : null;
        var rpg = Child(localPlayer.Root, "RPGInfo");
        var assignments = saved.Sections.SelectMany(s => s.Entries).GroupBy(e => e.Address)
            .ToDictionary(g => g.Key, g => g.ToArray());
        for (int bar = 0; bar < 2; bar++)
            for (int group = 0; group < 2; group++)
                for (int button = 0; button < 3; button++)
                {
                    var address = new EnbRetailShortcutAddress(bar, group, button);
                    if (!assignments.TryGetValue(address, out var entries))
                    { result.Add(new(address, null, EnbSceneShortcutResolution.Empty)); continue; }
                    // Contradictory saved records are diagnostic, not an arbitrary activation choice.
                    if (entries.Length != 1)
                    { result.Add(new(address, null, EnbSceneShortcutResolution.ConflictingAssignments)); continue; }
                    var entry = entries[0];
                    if (entry.SavedIndex is null)
                    { result.Add(new(address, entry, EnbSceneShortcutResolution.MalformedKey)); continue; }
                    result.Add(entry.Source == EnbRetailShortcutSource.Skill ? Skill(entry) : Item(entry));
                }
        return new(saved, result.MoveToImmutable())
            { IsIncapacitated = Bool(Child(ship?.Root, "IsIncapacitated")) == true,
                Powerup = (Child(rpg,"SkillPowerupAbilityNumber") is {State:not 0} powerupAbility ? Int(powerupAbility) ?? -1 : -1,
                    Child(rpg,"SkillPowerupStart") is {State:not 0} powerupStart ? Time(powerupStart) : 0,
                    Child(rpg,"SkillPowerupEnd") is {State:not 0} powerupEnd ? Time(powerupEnd) : 0),
                EquipmentRevision=(Child(inventory,"Equipped")?.UpdateRevision??0,Child(inventory,"Ammo")?.UpdateRevision??0),
                SkillRevision=(Child(rpg,"Skills")?.UpdateRevision??0,Child(rpg,"SkillAbilities")?.UpdateRevision??0) };

        EnbSceneShortcutSlotSnapshot Skill(EnbRetailShortcutEntry entry)
        {
            var slot = new EnbSceneShortcutSlotSnapshot(entry.Address, entry, EnbSceneShortcutResolution.SkillCatalogUnavailable);
            if (skills is null) return slot;
            // 006A6C00 replaces the serialized index with the named family's current index.
            var family = skills.FindFamily(entry.FamilyName);
            if (family is null) return slot with { Resolution = EnbSceneShortcutResolution.UnknownFamily };
            slot = slot with { FamilyId = family.Id, Name = family.Name, IconResource = family.IconResource,
                SkillLevel = Int(Child(At(Child(rpg, "Skills"), family.Id), "Level")) };
            var ability = skills.FindAbility(entry.AbilityName);
            if (ability is null) return slot with { Resolution = EnbSceneShortcutResolution.UnknownAbility };
            var abilityState = At(Child(rpg, "SkillAbilities"), ability.Id);
            var familyState = At(Child(rpg, "Skills"), family.Id);
            return slot with { Resolution = EnbSceneShortcutResolution.Resolved, Name = ability.Name,
                AbilityId = ability.Id, IconTint = ability.IconTint(slot.SkillLevel),
                SkillFeedback = new(Raw(Child(abilityState,"Validity")), Raw(Child(abilityState,"Targets")),
                    Int(Child(abilityState,"Range")) ?? 0, Int(Child(familyState,"RecycleTime")) ?? 0,
                    Time(Child(familyState,"LastActivation")),
                    Child(rpg,"SkillPowerupAbilityNumber") is {State:not 0} powerup ? Int(powerup) ?? 0 : 0,
                    Child(rpg,"SkillPowerupStart") is {State:not 0} start ? Time(start) : 0,
                    Child(rpg,"SkillPowerupEnd") is {State:not 0} end ? Time(end) : 0),
                CanBeUsedWhileIncapacitated = Bool(Child(At(Child(rpg, "SkillAbilities"), ability.Id), "CanBeUsedWhileIncapacitated")) == true,
                HasAbility = Bool(Child(At(Child(rpg, "SkillAbilities"), ability.Id), "HasAbility")) };
        }

        EnbSceneShortcutSlotSnapshot Item(EnbRetailShortcutEntry entry)
        {
            var slot = new EnbSceneShortcutSlotSnapshot(entry.Address, entry, EnbSceneShortcutResolution.InventoryPending);
            var collection = Child(inventory, entry.Source == EnbRetailShortcutSource.Equipment ? "Equipped" : "Cargo");
            if (collection is null) return slot;
            var item = At(collection, entry.SavedIndex!.Value);
            if (item is null) return slot with { Resolution = EnbSceneShortcutResolution.MalformedKey };
            int? id = Int(Child(item, "ItemTemplateID")), count = Int(Child(item, "StackCount"));
            slot = slot with { ItemTemplateId = id, StackCount = count };
            if (id is null || count is null) return slot;
            if (id < 0 || count <= 0) return slot with { Resolution = EnbSceneShortcutResolution.EmptyInventorySlot };
            if (templates is null || !templates.TryGet((uint)id.Value, out var template) || template is null)
                return slot with { Resolution = EnbSceneShortcutResolution.ItemTemplatePending };
            return slot with { Resolution = EnbSceneShortcutResolution.Resolved,
                Name = template.Name.EffectiveText, IconBaseAsset = template.IconBaseAsset };
        }
    }

    private static Net7AuxDataProperty? Child(Net7AuxDataProperty? parent, string name)
        => parent?.Children.FirstOrDefault(c => c.Path.AsSpan(c.Path.LastIndexOf('.') + 1).SequenceEqual(name));
    private static Net7AuxDataProperty? At(Net7AuxDataProperty? parent, int index)
        => parent is not null && (uint)index < (uint)parent.Children.Count ? parent.Children[index] : null;
    // State zero does not erase a typed retained value (same rule as cockpit vitals).
    private static int? Int(Net7AuxDataProperty? p) => p?.Value is int value ? value : p is { State: not 0 } ? 0 : null;
    private static bool? Bool(Net7AuxDataProperty? p) => p?.Value is byte value ? value != 0 : p is { State: not 0 } ? false : null;
    private static uint Raw(Net7AuxDataProperty? p) => p?.Value is byte[] {Length:4} bytes
        ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes) : 0;
    private static long Time(Net7AuxDataProperty? p) => p?.Value is Net7AuxDataTime value ? value.RelativeValue : 0;
}
