using System.Collections.Immutable;
using System.Globalization;

namespace EnbNext.Presentation;

/// <summary>Current client 00522860 attribute rows and 007EB4D0 item-type names.</summary>
public static class EnbItemInformation
{
    // Presentation only: the server remains authoritative for item operations.
    // Current retail requirement visitors: 0068C680, 0052F010, 00531080.
    public static bool HasUnmetRequirements(EnbSceneItemSnapshot item, EnbSceneInventorySnapshot inventory)
    {
        var data=inventory.RequirementState;
        var attributes=item.Attributes;
        bool Has(uint id)=>attributes.Any(a=>a.Id==id);
        string Get(uint id)=>attributes.FirstOrDefault(a=>a.Id==id)?.Value??"";
        int Number(uint id,int fallback=0)=>int.TryParse(Get(id),NumberStyles.Integer,CultureInfo.InvariantCulture,out int n)?n:fallback;
        bool Skill(string name,int level)=>data.SkillsAvailable && data.SkillLevels.TryGetValue(name,out int current) && current>=level;
        // 522860 produces the secondary skill row on attribute28, and race on18.
        if(Has(11) && !Skill(Get(11),item.Level))return true;
        if(Has(28) && !Skill(Get(27),Number(28,1)))return true;
        foreach(var (id,key) in new[]{(4u,"CombatLevel"),(12u,"ExploreLevel"),(32u,"TradeLevel")})
            if(Number(id) is >0 and var required && (!data.Fields.TryGetValue(key,out int current)||current<required))return true;
        // 531080's overall-level branch only checks the RPG aggregate's availability.
        if(Number(14)>0 && !data.RpgAvailable)return true;
        int raceMask=Number(18) | (Number(17) switch {1=>4,2=>2,_=>0});
        foreach(var (present,mask,key) in new[]{(Has(18),raceMask,"Race"),(Has(3),Number(3),"Profession")})
            if(present && (mask&7)!=0 && (!data.Fields.TryGetValue(key,out int index)||(mask&(1<<index))!=0))return true;
        return false;
    }
    // Current client EmitItemCoreInfoRows (00521C10) and EmitItemTemplateAttributeInformation
    // (00522860). These strings are executable literals, not entries in tooltip.ini.
    public static string AttributeTooltip(EnbSceneItemSnapshot item, string name) => name switch
    {
        "Quality" => "Quality of this item compared to others of its kind",
        "Structure" => item.Structure switch
        {
            <= 0 => "This item is no longer operational and must be repaired.",
            <= .5f => "This item has been severely damaged but is still functioning.",
            < 1 => "This item has taken damage but is still functioning.",
            >= 1 => "This item is functioning perfectly.",
            _ => ""
        },
        "Mode" => "Which activities this Override item benefits",
        "Build Value" => "Improvement in Build/Refine ability when used.",
        "Critical" => "Improvement in chances for a Critical Success when used.",
        "Ammo" => "Ammunition required for this weapon",
        "Damage" => "Base damage inflicted by weapon",
        "Effect Range" => "Range of effect.",
        "Effect Radius" => "Radius of effect.",
        "Energy Use" or "Energy Drain" => "Energy required to activate item",
        "Maneuverability" => "Ability to defeat anti-missile systems",
        "Max Power" => "Maximum amount of power this reactor can hold",
        "Range" => "The maximum targeting range of the item.",
        "Recharge" => item.Attributes.Any(a => a.Id == 26)
            ? "The amount of shield energy regenerated per second."
            : "The amount of energy regenerated per second.",
        "Reload Time" => "The number of seconds that must elapse between activations.",
        "Rounds Fired" => "The number of rounds fired per burst.",
        "Shield Use" or "Shield Drain" => "Shield energy required to activate item",
        "Capacity" => "Maximum amount of damage this shield can absorb when fully charged.",
        "Signature" => "Specifies an increased distance that an NPC will notice the player when engine is equipped.",
        "Thrust" => "Higher thrust values will increase the ship's maximum speed.",
        "Warp" => "Shows how fast the ship will travel while warping with this engine equipped.",
        "Warp Drain" => "How fast engine will drain reactor when in free warp (warping with no target.)",
        _ => ""
    };
    public static readonly ImmutableArray<string> Filters = ["All", "Beams", "Missile Launchers", "Projectile Launchers", "Ammo", "Shields", "Reactors", "Engines", "Devices", "Components", "Resources", "Trade Goods"];
    public static string TypeName(uint type) => type switch
    { 100 => "Beam Weapon", 101 => "Projectile Weapon", 102 => "Missile Launcher", 103 => "Ammunition", 110 => "Device", 111 => "Drone",
      120 => "Reactor", 121 => "Engine", 122 => "Shield", 130 => "Consumable", 140 => "Software", 141 => "Electronic Item", 142 => "Computer",
      150 => "Power Converter", 151 => "Power Coupling", 152 => "Power Core", 153 => "Power Generator", 160 => "Casing", 161 => "Engine Frame",
      162 => "Drone Frame", 163 => "Mount", 170 => "Firing Mechanism", 171 => "Ammunition Feeder", 172 => "Beam Optic", 173 => "Weapon Barrel",
      180 => "Ammunition Slug", 181 => "Warhead", 182 => "Propellant", 183 => "Shell Casing", 200 => "Optic Gem", 201 => "Metal",
      202 => "Conductor", 203 => "Alloy", 204 => "Radioactive", 205 => "Catalyst", 206 => "Hydrocarbon", 207 => "Silicate", 208 => "Magnetic",
      209 => "Gemstone", 210 => "Gas", 211 => "Core Metal", _ => "Item" };
    public static bool Matches(EnbSceneItemSnapshot item, int filter) => filter switch
    { 0 => true, 1 => item.ItemType == 14, 2 => item.ItemType == 15, 3 => item.ItemType == 16, 4 => item.ItemType == 10,
      5 => item.ItemType == 2, 6 => item.ItemType == 7, 7 => item.ItemType == 6, 8 => item.ItemType == 11,
      9 => item.ItemType == 13 && !Matches(item,10) && !Matches(item,11),
      10 => item.ItemType == 13 && item.Flags == 0x80 && (item.Category == 80 || item.Category == uint.MaxValue && item.Subcategory == uint.MaxValue),
      11 => item.ItemType == 13 && item.Flags == 0x88, _ => false };
    public static ImmutableArray<(string Name, string Value)> Attributes(EnbSceneItemSnapshot item)
        => [.. AttributeRows(item).Select(r => (r.Name, r.Value))];

    /// <summary>The help of the attribute row at <paramref name="index"/>: each row keeps its own attribute's help (00522860),
    /// so an energy Recharge (20) and a shield Recharge (26) differ (INV-D44).</summary>
    public static string AttributeHelpAt(EnbSceneItemSnapshot item, int index)
    {
        var rows = AttributeRows(item);
        if ((uint)index >= (uint)rows.Count) return "";
        var row = rows[index];
        return row.Id switch
        {
            20 => "The amount of energy regenerated per second.",
            26 => "The amount of shield energy regenerated per second.",
            _ => AttributeTooltip(item, row.Name),
        };
    }

    /// <summary>00521C10/00522860 rows with their sort key (row+8; 005343A0 sorts by it): Quality 0, Damage 5, Structure 6,
    /// most attributes 6, Build Value 7, Critical 8, Ammo and Effect Range/Radius 10; and the native formats: distances
    /// value*0.001 as %1.1fK, floats %0.1f (Maneuverability and Max Power included), Reload %2.1f, percentages %0.0f%%
    /// (INV-D07).</summary>
    private static List<(string Name, string Value, uint Id, int Key)> AttributeRows(EnbSceneItemSnapshot item)
    {
        var rows = new List<(string Name, string Value, uint Id, int Key)>();
        if (item.Quality is >= 0) rows.Add(("Quality", FormattableString.Invariant($"{item.Quality * 100:0}%"), uint.MaxValue, 0));
        if (item.Structure is >= 0) rows.Add(("Structure", FormattableString.Invariant($"{item.Structure * 100:0}%"), uint.MaxValue - 1, 6));
        foreach (var a in item.Attributes.OrderBy(a => a.Id))
        {
            string? label = a.Id switch { 1 => "Ammo", 5 => "Damage", 7 => "Effect Range", 8 => "Effect Radius", 9 => "Energy Use",
                10 => "Energy Drain", 15 => "Maneuverability", 16 => "Max Power", 19 => "Range", 20 or 26 => "Recharge", 21 => "Reload Time",
                22 => "Rounds Fired", 23 => "Shield Use", 24 => "Capacity", 25 => "Shield Drain", 29 => "Signature", 31 => "Thrust",
                33 => "Warp", 34 => "Warp Drain", 35 => "Mode", 36 => "Build Value", 37 => "Critical", _ => null };
            if (label is null) continue;
            int key = a.Id switch { 1 or 7 or 8 => 10, 5 => 5, 36 => 7, 37 => 8, _ => 6 };
            string value = a.Value;
            if (a.Id == 35)
            {
                // 00522860 case 35: bits 1/2/4/8 name the modes, joined with ", "; zero omits the row (INV-D06).
                if (!uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint flags) || flags == 0) continue;
                string[] modes = ["Manufacturing", "Analyze", "Dismantle", "Refining"];
                value = string.Join(", ", modes.Where((_, bit) => (flags & (1u << bit)) != 0));
                if (value.Length == 0) continue;
            }
            else if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float n))
                value = a.Id switch
                {
                    7 or 8 or 19 or 29 => FormattableString.Invariant($"{(int)n * 0.001f:0.0}K units"),
                    9 or 15 or 16 or 23 => FormattableString.Invariant($"{n:0.0} units"),
                    10 or 20 or 25 or 26 or 34 => FormattableString.Invariant($"{n:0.0} units/sec"),
                    21 => FormattableString.Invariant($"{n:0.0} seconds"), 22 => $"{value} rounds",
                    36 => FormattableString.Invariant($"{n:0.0} levels"), 37 => FormattableString.Invariant($"{n * 100:0}%"),
                    5 or 24 or 31 => $"{value} units", _ => value
                };
            rows.Add((label, value, a.Id, key));
        }
        // Ammo (1) was shown last; it now sorts by its native key. A stable sort keeps the attribute order within a key.
        return [.. rows.Select((r, i) => (r, i)).OrderBy(x => x.r.Key).ThenBy(x => x.i).Select(x => x.r)];
    }
    public static ImmutableArray<EnbItemSpecial> Specials(EnbSceneItemSnapshot item, EnbItemRequirementState? state = null)
    {
        var rows = ImmutableArray.CreateBuilder<EnbItemSpecial>();
        string Get(uint id) => item.Attributes.FirstOrDefault(a => a.Id == id)?.Value ?? "";
        bool Has(uint id) => item.Attributes.Any(a => a.Id == id);
        int Number(uint id, int fallback = 0) => int.TryParse(Get(id), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : fallback;
        // The same predicates as HasUnmetRequirements, per row; without player data every row reads as met.
        bool Skill(string name, int level) => state is null || state.SkillsAvailable && state.SkillLevels.TryGetValue(name, out int current) && current >= level;
        bool Field(string key, int required) => state is null || state.Fields.TryGetValue(key, out int current) && current >= required;
        bool Mask(int mask, string key) => state is null || (mask & 7) == 0 || state.Fields.TryGetValue(key, out int index) && (mask & (1 << index)) == 0;
        if (Get(11) is { Length: > 0 } skill) rows.Add(new($"Requires {skill} level {item.Level}","You must have this skill level to equip this item.",!Skill(skill,item.Level)));
        if (Get(27) is { Length: > 0 } skill2) rows.Add(new($"Requires {skill2} level {Get(28)}","You must have this skill level to activate this item.",Has(28) && !Skill(skill2,Number(28,1))));
        int.TryParse(Get(18), out int restricted); int.TryParse(Get(17), out int raceOnly);
        restricted |= raceOnly == 1 ? 4 : raceOnly == 2 ? 2 : 0;
        string race = (restricted & 7) switch { 1 => "Terran Restricted", 2 => "Jenquai Restricted", 3 => "Progen Only", 4 => "Progen Restricted", 5 => "Jenquai Only", 6 => "Terran Only", 7 => "Race Restricted", _ => "" };
        if (race.Length > 0) rows.Add(new(race,(restricted&7) switch
        {1=>"May not be equipped on Terran ships.",2=>"May not be equipped on Jenquai ships.",3=>"May only be equipped on Progen ships.",
         4=>"May not be equipped on Progen ships.",5=>"May only be equipped on Jenquai ships.",6=>"May only be equipped on Terran ships.",_=>"Players cannot equip."},
         Has(18) && !Mask(restricted,"Race")));
        int.TryParse(Get(3), out int profession);
        string professionText=(profession&7) switch {1=>"Warriors Restricted",2=>"Trader Restricted",3=>"Explorers Only",4=>"Explorers Restricted",5=>"Tradesmen Only",6=>"Warriors Only",7=>"Profession Restricted",_=>""};
        if(professionText.Length>0)rows.Add(new(professionText,(profession&7) switch
        {1=>"May not be used by Warriors.",2=>"May not be used by Tradesmen.",3=>"May only be used by Explorers.",
         4=>"May not be used by Explorers.",5=>"May only be used by Tradesmen.",6=>"May only be used by Warriors.",_=>"Players cannot use."},
         Has(3) && !Mask(profession,"Profession")));
        foreach (var (id, name) in new[] { (4u, "Combat"), (12u, "Explore"), (14u, "Overall"), (32u, "Trade") })
            if (int.TryParse(Get(id), out int n) && n > 0) rows.Add(new($"Requires {name} level {n}",
                id==14?"You must be this level to use.":$"You must have this level of {name} to use this item.",
                id==14 ? state is not null && !state.RpgAvailable : !Field(name switch { "Combat" => "CombatLevel", "Explore" => "ExploreLevel", _ => "TradeLevel" }, n)));
        // 005285A0: template restrictions, independent of dynamic item state.
        foreach (var (mask, text, help) in new[] {
            (1u,"Not Tradable","This item cannot be traded or sold."),
            (2u,"Temporary","This item will disappear when player logs out."),
            (4u,"Unique","Player cannot have more than one of this item."),
            (8u,"Not Storable","This item cannot be placed in storage."),
            (16u,"Not Destroyable","This item cannot be destroyed."),
            (128u,"Not Manufacturable","This item cannot be analyzed or manufactured.") })
            if ((item.Flags & mask) != 0) rows.Add(new(text,help));
        rows.AddRange(item.Effects);
        // 00521C10/0052C770: the instance builder (player) wins; otherwise the definition's corporation (INV-D48).
        if (item.Builder.Length > 0) rows.Add(new("Manufactured by: " + item.Builder,"This item was manufactured by a player."));
        else if (item.CorporationBuilder.Length > 0) rows.Add(new("Manufactured by: " + item.CorporationBuilder,"This item was manufactured by a corporation."));
        return rows.ToImmutable();
    }

    // Next Modern UI comparison: lower is the better value for these attributes.
    private static readonly string[] LowerIsBetter = ["Reload Time", "Energy Use", "Energy Drain", "Shield Use", "Shield Drain", "Signature", "Warp Drain"];

    /// <summary>Next: how each shared numeric attribute would change replacing <paramref name="equipped"/> with
    /// <paramref name="item"/>; the change as text ("+12", "-0.5K") and whether it is an improvement.</summary>
    public static ImmutableArray<(string Name, string Change, bool Better)> Compare(EnbSceneItemSnapshot item, EnbSceneItemSnapshot equipped)
    {
        var theirs = Attributes(equipped);
        var result = ImmutableArray.CreateBuilder<(string, string, bool)>();
        foreach (var (name, value) in Attributes(item))
        {
            var match = theirs.FirstOrDefault(t => t.Name == name);
            if (match.Name is null || Numeric(value) is not { } mine || Numeric(match.Value) is not { } other) continue;
            double change = mine.Value - other.Value;
            if (Math.Abs(change) < 1e-6) continue;
            bool k = mine.Thousands || other.Thousands;
            string text = (change > 0 ? "+" : "-") + (k ? (Math.Abs(change) / 1000).ToString("0.0", CultureInfo.InvariantCulture) + "K"
                : Math.Abs(change).ToString("0.#", CultureInfo.InvariantCulture)) + (value.TrimEnd().EndsWith('%') ? "%" : "");
            result.Add((name, text, (change > 0) != LowerIsBetter.Contains(name)));
        }
        // Effects by name (Enhance Beam (Equip)): the first number of each one's explanation, so 30.0% against 19.5% reads
        // +10.5%. An effect only this item has is a gain; one only the equipped item has is a loss.
        static string Effect(EnbItemSpecial e) => e.Text.StartsWith("Effect: ", StringComparison.Ordinal) ? e.Text[8..] : e.Text;
        static string Amount(double value, bool percent) => value.ToString("0.#", CultureInfo.InvariantCulture) + (percent ? "%" : "");
        static (double Value, bool Percent)? EffectValue(EnbItemSpecial e)
        {
            if (Numeric(e.Tooltip) is not { } n) return null;
            int at = e.Tooltip.IndexOfAny("0123456789".ToCharArray());
            while (at >= 0 && at < e.Tooltip.Length && (char.IsAsciiDigit(e.Tooltip[at]) || e.Tooltip[at] is '.' or ',')) at++;
            return (n.Value, at >= 0 && at < e.Tooltip.Length && e.Tooltip[at] == '%');
        }
        var equippedEffects = equipped.Effects.GroupBy(Effect).ToDictionary(g => g.Key, g => g.First());
        var seen = new List<string>();
        foreach (var effect in item.Effects)
        {
            string name = Effect(effect);
            if (seen.Contains(name)) continue;
            seen.Add(name);
            var mine = EffectValue(effect);
            if (!equippedEffects.TryGetValue(name, out var worn))
            { if (mine is { } gain) result.Add((name, "+" + Amount(gain.Value, gain.Percent), true)); continue; }
            if (mine is not { } a || EffectValue(worn) is not { } b || Math.Abs(a.Value - b.Value) < 1e-6) continue;
            double change = a.Value - b.Value;
            result.Add((name, (change > 0 ? "+" : "-") + Amount(Math.Abs(change), a.Percent || b.Percent), change > 0));
        }
        foreach (var (name, effect) in equippedEffects)
            if (!seen.Contains(name) && EffectValue(effect) is { } loss)
                result.Add((name, "-" + Amount(loss.Value, loss.Percent), false));
        return result.ToImmutable();
    }

    /// <summary>The leading number of an attribute's text, with a K suffix read as thousands.</summary>
    private static (double Value, bool Thousands)? Numeric(string text)
    {
        int start = 0; while (start < text.Length && !(char.IsAsciiDigit(text[start]) || text[start] is '-' or '.')) start++;
        int end = start; while (end < text.Length && (char.IsAsciiDigit(text[end]) || text[end] is '-' or '.' or ',')) end++;
        if (end == start || !double.TryParse(text.AsSpan(start, end - start).ToString().Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double n)) return null;
        bool k = end < text.Length && text[end] is 'K' or 'k';
        return (k ? n * 1000 : n, k);
    }
}
