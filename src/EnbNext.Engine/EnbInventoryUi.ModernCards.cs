using System.Collections.Immutable;
using System.Globalization;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

/// <summary>
/// Next Modern UI tooltip cards: what an item or a skill is, from the same data its details window shows (the item
/// examination, the skill details pane), so a hover already tells what the right-click or the details pane would.
/// Live lines of the retail tooltip (price, range, ready time) that the details do not cover stay on the card.
/// </summary>
internal sealed partial class EnbInventoryUi
{
    /// <summary>An item: icon in its category frame, name, level and type, attributes, specials and description.</summary>
    internal EnbTooltipCard ItemCard(EnbSceneItemSnapshot item, string? retailText = null, string? footer = null)
    {
        var attributes = EnbItemInformation.Attributes(item);
        var sections = ImmutableArray.CreateBuilder<EnbTooltipCardSection>();
        // The retail tooltip's lines the details below do not already say (price, average cost, range, ready time...).
        var known = new HashSet<string>(attributes.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
        var extra = (retailText ?? "").Replace("\\n", "\n", StringComparison.Ordinal).Split('\n').Select(l => l.Trim())
            .Where(l => l.Length > 0 && l != item.Name && !known.Any(k => l.StartsWith(k + " ", StringComparison.OrdinalIgnoreCase)))
            .Select(l => l.StartsWith("Avg. Cost", StringComparison.Ordinal) ? "Average cost" + l[9..] : l).ToArray();
        if (extra.Length > 0) sections.Add(new("") { Text = string.Join('\n', extra), TextColor = new(.6f, 1, .6f) });
        if (attributes.Length > 0) sections.Add(new("Attributes") { Rows = [.. attributes.Select(a => new EnbTooltipCardRow(a.Name, a.Value))] });
        var specials = EnbItemInformation.Specials(item);
        // An effect's explanation is the item's own value (+39.9 Psionic Resist when equipped), which a tooltip cannot be
        // hovered for, so it stands under the effect; the other specials' explanations only define the line itself.
        if (specials.Length > 0)
        {
            var lines = ImmutableArray.CreateBuilder<EnbTooltipCardLine>();
            foreach (var special in specials)
            {
                lines.Add(new(special.Text, new(1, .78f, .45f)));
                if (special.Tooltip.Length > 0 && special.Tooltip != special.Text && item.Effects.Contains(special)) lines.Add(new(special.Tooltip, new(.86f, .9f, 1), true));
            }
            sections.Add(new("Specials") { Lines = lines.ToImmutable() });
        }
        if (item.Description is { Length: > 0 } description) sections.Add(new("Description") { Text = description });
        // Next: an unequipped item is compared with the equipped items of its kind (at most two), shown beside it.
        var compared = ImmutableArray.CreateBuilder<EnbTooltipCard>();
        if (ModernUi && item.Inventory != 2 && !item.IsAmmoLayer && item.ItemType != uint.MaxValue && Data is { } inventory)
            foreach (var equipped in inventory.Equipped)
            {
                if (compared.Count == 2) break;
                if (equipped.TemplateId < 0 || equipped.ItemType != item.ItemType || equipped.Subcategory != item.Subcategory) continue;
                var changes = EnbItemInformation.Compare(item, equipped);
                if (changes.Length > 0)
                    sections.Add(new("Instead of " + equipped.Name)
                    { Lines = [.. changes.Select(c => new EnbTooltipCardLine(c.Name + "  " + c.Change, c.Better ? new(.45f, 1, .45f) : new(1, .45f, .45f)))] });
                compared.Add(ItemCard(equipped) with
                {
                    Subtitle = "Equipped  " + $"Level {equipped.Level.ToString(CultureInfo.InvariantCulture)} {EnbItemInformation.TypeName(equipped.Subcategory)}",
                    SubtitleColor = new(.75f, .8f, .92f),
                });
            }
        return new(item.Name)
        {
            Compare = compared.ToImmutable(),
            Subtitle = $"Level {item.Level.ToString(CultureInfo.InvariantCulture)} {EnbItemInformation.TypeName(item.Subcategory)}",
            Icon = Icon(item.IconBaseAsset),
            IconFrame = item.ItemType != uint.MaxValue ? Texture("icon_" + SlotColor(item) + ".tga") : null,
            Sections = sections.ToImmutable(), Footer = footer,
        };
    }

    /// <summary>A skill: its details pane (description, this level, the next level or how it is acquired).</summary>
    internal EnbTooltipCard SkillCard(EnbCharacterSkillSnapshot skill, string? footer = null)
    {
        var family = _assets?.ResolveShortcutSkills()?.FindFamily(skill.Name);
        var sections = ImmutableArray.CreateBuilder<EnbTooltipCardSection>();
        sections.Add(new("Description") { Text = family?.Description ?? skill.Description });
        AddLevels(sections, family, skill, skill.Level);
        return new(skill.Name)
        {
            TitleColor = ModernSkillColor(skill),
            Subtitle = skill.Level > 0 ? $"Level {skill.Level} of {skill.Maximum}" : "Not trained",
            SubtitleColor = new(.75f, .8f, .92f),
            Icon = Texture(skill.Icon), Sections = sections.ToImmutable(), Footer = footer,
        };
    }

    private void AddLevels(ImmutableArray<EnbTooltipCardSection>.Builder sections, EnbRetailSkillFamily? family, EnbCharacterSkillSnapshot? skill, int level)
    {
        if (level == 0)
        {
            var stats = Data?.CharacterStats;
            uint Stat(string key) => uint.TryParse(stats?.GetValueOrDefault(key, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
            uint race = Stat("Race"), profession = Stat("Profession");
            sections.Add(new("Not acquired") { Text = family?.AcquisitionDescription(race, profession) ?? "" });
        }
        else sections.Add(new("Current level") { Text = family?.LevelDescriptions.GetValueOrDefault(level, "") ?? "" });
        string next = ((skill?.Availability?.Raw1 ?? 0) & 0x40) != 0 ? "You have already attained the maximum level for this skill."
            : family?.LevelDescriptions.GetValueOrDefault(level + 1, "") ?? "";
        sections.Add(new("Next level") { Text = next });
    }

    /// <summary>An ability: its energy cost and range at the skill's level, what it does, and the skill's levels.</summary>
    internal EnbTooltipCard? AbilityCard(string abilityName, int level, string? footer = null)
    {
        var catalog = _assets?.ResolveShortcutSkills();
        if (catalog?.FindAbility(abilityName) is not { } ability) return null;
        var family = catalog.FindFamily(ability.FamilyName);
        var skill = Data?.Skills.FirstOrDefault(s => s.Name.Equals(ability.FamilyName, StringComparison.OrdinalIgnoreCase));
        int rank = Math.Max(level, ability.MinimumLevel);
        int range = family is not null && (uint)rank < family.Ranges.Length ? family.Ranges[rank] : 0;
        var sections = ImmutableArray.CreateBuilder<EnbTooltipCardSection>();
        sections.Add(new("")
        {
            Rows = [new("Energy cost", ability.Cost < 0 ? "Variable" : ability.Cost.ToString(CultureInfo.InvariantCulture)),
                new("Range", (range / 1000.0).ToString("F2", CultureInfo.InvariantCulture) + "k units")],
        });
        sections.Add(new("Ability") { Text = ability.Description });
        if (level < ability.MinimumLevel)
            sections.Add(new("") { Text = $"Requires level {ability.MinimumLevel.ToString(CultureInfo.InvariantCulture)} of {ability.FamilyName}", TextColor = new(1, .55f, .3f) });
        AddLevels(sections, family, skill, skill?.Level ?? level);
        return new(ability.Name)
        {
            TitleColor = new(1, .85f, .3f),
            Subtitle = $"{ability.FamilyName} level {level.ToString(CultureInfo.InvariantCulture)}",
            SubtitleColor = new(.75f, .8f, .92f),
            Icon = skill is not null ? Texture(skill.Icon) : null,
            Sections = sections.ToImmutable(), Footer = footer,
        };
    }

    /// <summary>The character's level of a skill (0: not acquired).</summary>
    internal int SkillLevel(string family) => Data?.Skills.FirstOrDefault(s => s.Name.Equals(family, StringComparison.OrdinalIgnoreCase))?.Level ?? 0;
    /// <summary>The item an action bar item shortcut uses (an equipped item, or cargo).</summary>
    internal EnbSceneItemSnapshot? ShortcutItem(EnbRetailShortcutEntry assignment) => assignment switch
    {
        { Source: EnbRetailShortcutSource.Equipment, SavedIndex: { } mount } => Data?.Find(2, mount),
        { Source: EnbRetailShortcutSource.Cargo, SavedIndex: { } cargo } => Data?.Find(1, cargo),
        _ => null,
    };
    /// <summary>An action bar shortcut: its skill ability, or the equipped or cargo item it uses.</summary>
    internal EnbTooltipCard? ShortcutCard(EnbSceneShortcutSlotSnapshot slot, string? retailText)
    {
        if (!ModernUi || slot.Resolution == EnbSceneShortcutResolution.Empty) return null;
        if (EnbShortcutSkillActivation.IsSkill(slot) && slot.Name is { } abilityName) return AbilityCard(abilityName, slot.SkillLevel ?? 0);
        if (EnbShortcutEquipmentActivation.IsEquipment(slot) && slot.Assignment?.SavedIndex is { } mount && Data?.Find(2, mount) is { } equipped)
            return ItemCard(equipped, retailText);
        if (slot.Assignment is { Source: EnbRetailShortcutSource.Cargo, SavedIndex: { } cargo } && slot.Resolution == EnbSceneShortcutResolution.Resolved && Data?.Find(1, cargo) is { } item)
            return ItemCard(item, retailText);
        return null;
    }

    /// <summary>The card for a hovered Modern UI hit, if it is an item, a skill or an ability.</summary>
    private EnbTooltipCard? HoverCard(string id, string text)
    {
        if (!ModernUi) return null;
        if (_items.GetValueOrDefault(id) is { } request && Resolve(request) is { } item)
        {
            // A loaded weapon's slot is the weapon; the Modern UI ammunition column is the ammunition.
            if (request.AmmoLayer && !id.StartsWith("mequip/ammo", StringComparison.Ordinal) && Data?.Find(request.Inventory, request.Slot) is { } weapon) item = weapon;
            return ItemCard(item, text);
        }
        if (id.StartsWith("mskill/row/", StringComparison.Ordinal) && int.TryParse(id.AsSpan(11), NumberStyles.None, CultureInfo.InvariantCulture, out int skillId) &&
            Data?.Skills.FirstOrDefault(s => s.Id == skillId) is { } skill)
            return SkillCard(skill, "Click for details.");
        int ability = id.StartsWith("ability/", StringComparison.Ordinal) ? 8 : id.IndexOf("/locked/", StringComparison.Ordinal) is var locked and >= 0 ? locked + 8 : -1;
        if (ability >= 0 && int.TryParse(id.AsSpan(ability), NumberStyles.None, CultureInfo.InvariantCulture, out int abilityId) &&
            _assets?.ResolveShortcutSkills()?.Abilities.FirstOrDefault(a => a.Id == abilityId) is { } found)
        {
            var owner = Data?.Skills.FirstOrDefault(s => s.Name.Equals(found.FamilyName, StringComparison.OrdinalIgnoreCase));
            int level = owner?.Level ?? 0;
            return AbilityCard(found.Name, level, level >= found.MinimumLevel ? "Drag this icon to your shortcut bar to activate or configure." : null);
        }
        return null;
    }
}
