using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;

namespace EnbNext.Assets;

public sealed record EnbRetailSkillFamily(int Id, string Name, string IconResource)
{
    public ImmutableArray<int> Ranges { get; init; } = [];
    public string Description { get; init; } = "";
    public string Category { get; init; } = "";
    public ImmutableDictionary<string,int> ClassMaximumLevels { get; init; } = ImmutableDictionary<string,int>.Empty;
    public int ClassMaximum(uint race,uint profession) => race<3 && profession<3 ? ClassMaximumLevels.GetValueOrDefault(ClassTemplates[race*3+profession]) : 0;
    private static readonly string[] ClassTemplates = ["Enforcer","Tradesman","Scout","Defender","Seeker","Explorer","Warrior","Privateer","Sentinel"];
    public ImmutableDictionary<string,string> AcquisitionDescriptions { get; init; } = ImmutableDictionary<string,string>.Empty;
    public string AcquisitionDescription(uint race, uint profession)
    {
        // 0082F350 / 0082F3C0: these are INI template names (Tradesman, not Trader).
        return race < 3 && profession < 3 ? AcquisitionDescriptions.GetValueOrDefault(ClassTemplates[race*3+profession], "Bug: Report Me") : "";
    }
    public ImmutableDictionary<int,string> LevelDescriptions { get; init; } = ImmutableDictionary<int,string>.Empty;
}
public sealed record EnbRetailSkillAbility(int Id, string Name, string FamilyName, bool Activated)
{
    public int MinimumLevel { get; init; }
    public int Cost { get; init; }
    public string Description { get; init; } = "";
    public Vector3 Color { get; init; } = Vector3.One;
    // Current client 006A6EC0: family level selects authored tint, not activation permission.
    public Vector3 IconTint(int? level) => level >= MinimumLevel ? Color : new(.2f);
}

/// <summary>Installed identity, family artwork and ability tint; INI values do not grant abilities.</summary>
public sealed class EnbRetailSkillCatalog
{
    public ImmutableArray<EnbRetailSkillFamily> Families { get; }
    public ImmutableArray<EnbRetailSkillAbility> Abilities { get; }

    private EnbRetailSkillCatalog(ImmutableArray<EnbRetailSkillFamily> families,
        ImmutableArray<EnbRetailSkillAbility> abilities) => (Families, Abilities) = (families, abilities);

    public static EnbRetailSkillCatalog Load(string iniDirectory) => Parse(
        EnbRetailIniDocument.Load(Path.Combine(iniDirectory, "cskill_t.ini")),
        EnbRetailIniDocument.Load(Path.Combine(iniDirectory, "cskill_ex_t.ini")),
        EnbRetailIniDocument.Load(Path.Combine(iniDirectory, "cskill_ability_t.ini")),
        EnbRetailIniDocument.Load(Path.Combine(iniDirectory, "cskill_ability_ex_t.ini")));

    public static EnbRetailSkillCatalog Parse(EnbRetailIniDocument skills, EnbRetailIniDocument extension,
        EnbRetailIniDocument abilities, EnbRetailIniDocument? abilityExtension = null)
    {
        var families = ImmutableArray.CreateBuilder<EnbRetailSkillFamily>();
        foreach (var (id, name) in Map(skills, "All Skills"))
        {
            string Value(string group,string key,string fallback="") =>
                extension.TryGetSection(group,out var ext) && ext.TryGetValue(key,out var value) ? value :
                skills.TryGetSection(group,out var basic) ? basic.GetValueOrDefault(key,fallback) : fallback;
            string Acquisition(string template)
            {
                string group=name+"\\"+template;
                if(!extension.TryGetSection(group,out _) && !skills.TryGetSection(group,out _))return "Bug: Report Me";
                string text="See "+Value(group,"Trainer","Unknown")+" at "+Value(group,"Location","Unknown")+" in "+Value(group,"Sector","Unknown")+", "+Value(group,"System","Unknown")+" System";
                if(int.TryParse(Value(group,"Learn Lvl"),out int level) && level!=0)text+=" upon reaching level "+Value(group,"Learn Lvl");
                return text+".";
            }
            string icon = extension.TryGetSection(name, out var section) ? section.GetValueOrDefault("Icon", "") : "";
            string range = extension.TryGetSection(name,out var extra) && extra.TryGetValue("Range",out var r) ? r :
                skills.TryGetSection(name,out var baseSection) ? baseSection.GetValueOrDefault("Range", "") : "";
            families.Add(new(id, name, string.IsNullOrWhiteSpace(icon) ? "skill_.tga" : icon)
                { Category = Value(name,"Category"),
                  ClassMaximumLevels = new[]{"Enforcer","Tradesman","Scout","Defender","Seeker","Explorer","Warrior","Privateer","Sentinel"}.ToImmutableDictionary(t=>t,t=>int.TryParse(Value(name,t),out int maximum)?Math.Max(0,maximum):0),
                  AcquisitionDescriptions = new[]{"Enforcer","Tradesman","Scout","Defender","Seeker","Explorer","Warrior","Privateer","Sentinel"}.ToImmutableDictionary(t=>t,Acquisition),
                  Description = Value(name,"Description"),
                  LevelDescriptions = Enumerable.Range(1,9).ToImmutableDictionary(i=>i,i=>Value(name,"Desc_"+i,"Skill level description not available.").Replace("\\n","\n")),
                  Ranges = new[] { 0 }.Concat(range.Split(',',StringSplitOptions.RemoveEmptyEntries).Select(s =>
                    int.TryParse(s,NumberStyles.Integer,CultureInfo.InvariantCulture,out int n) ? n : 0)).ToImmutableArray() });
        }
        var definitions = ImmutableArray.CreateBuilder<EnbRetailSkillAbility>();
        foreach (string sectionName in new[] { "Activated Abilities", "Passive Abilities" })
            foreach (var (id, name) in Map(abilities, sectionName))
            {
                string Value(string group, string key, string fallback = "") =>
                    abilityExtension?.TryGetSection(group, out var extra) == true && extra.TryGetValue(key, out var value) ? value :
                    abilities.TryGetSection(group, out var section) ? section.GetValueOrDefault(key, fallback) : fallback;
                int level = int.TryParse(Value(name, "MinLevel"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0;
                var rgb = Value(name, "Color", Value("AbilityColors", "Level_" + level, "1,1,1")).Split(',');
                var color = Vector3.One;
                if (rgb.Length == 3 && float.TryParse(rgb[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float r) &&
                    float.TryParse(rgb[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float g) &&
                    float.TryParse(rgb[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float b) &&
                    float.IsFinite(r) && float.IsFinite(g) && float.IsFinite(b)) color = Vector3.Clamp(new(r,g,b), Vector3.Zero, Vector3.One);
                definitions.Add(new(id, name, Value(name, "Skill"), sectionName == "Activated Abilities")
                    { MinimumLevel = level, Color = color, Description = Value(name,"Desc").Replace("\\n","\n"),
                        Cost = int.TryParse(Value(name,"Cost"),NumberStyles.Integer,CultureInfo.InvariantCulture,out int cost) ? cost : 0 });
            }
        return new(families.ToImmutable(), definitions.ToImmutable());
    }

    // 00818520 scans family names case-insensitively; 00815260 uses the ability
    // name map's SharedString comparison. Do not replace either with fuzzy matching.
    public EnbRetailSkillFamily? FindFamily(string? name) => Families.FirstOrDefault(
        f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    public EnbRetailSkillAbility? FindAbility(string? name) => Abilities.FirstOrDefault(
        a => a.Name.Equals(name, StringComparison.Ordinal));

    // Current client 00815720 / 00818340. Rank zero is inserted by the loader
    // before the authored Range values; out-of-table ranks return zero.
    public string Describe(EnbRetailSkillAbility ability, int level)
    {
        var family = FindFamily(ability.FamilyName);
        int rank = Math.Max(level, ability.MinimumLevel);
        int range = family is not null && (uint)rank < family.Ranges.Length ? family.Ranges[rank] : 0;
        string cost = ability.Cost < 0 ? "Variable" : ability.Cost.ToString(CultureInfo.InvariantCulture);
        string result = ability.Name + "\n\nEnergy cost: " + cost + "\nRange: " +
            (range / 1000.0).ToString("F2", CultureInfo.InvariantCulture) + "k units\n\n" +
            string.Join('\n', Wrap(ability.Description, 40)) + "\n";
        if (level < ability.MinimumLevel)
            result += "Requires Level " + ability.MinimumLevel.ToString(CultureInfo.InvariantCulture) + " Skill Upgrade\n";
        return result;
    }

    public ImmutableArray<EnbRetailSkillAbility> Choices(string family, int level) => Abilities.Where(a =>
        a.FamilyName.Equals(family, StringComparison.OrdinalIgnoreCase) && a.MinimumLevel <= level)
        .OrderBy(a => a.MinimumLevel).ToImmutableArray();

    public static IEnumerable<string> Wrap(string text, int columns)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        foreach (string paragraph in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string remaining = paragraph;
            while (remaining.Length > columns)
            {
                int split = remaining.LastIndexOf(' ', columns);
                if (split <= 0) split = columns;
                yield return remaining[..split];
                remaining = remaining[split..].TrimStart(' ');
            }
            yield return remaining;
        }
    }

    private static IEnumerable<(int Id, string Name)> Map(EnbRetailIniDocument document, string section)
    {
        if (!document.TryGetSection(section, out var values)) return [];
        return values.Select(p => (Id: int.TryParse(p.Key, NumberStyles.None, CultureInfo.InvariantCulture, out int id) ? id : -1,
            Name: p.Value)).Where(p => p.Id >= 0 && p.Name.Length > 0).OrderBy(p => p.Id);
    }
}
