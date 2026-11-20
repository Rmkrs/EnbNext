using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

/// <summary>
/// Next Modern UI buff cards: a hovered buff as a card (its name, the time left, the game's own description from
/// buffdef.ini and the sources the server names), and the item behind one of the player's own buffs.
/// The server's buff does not say which item made it, so a buff that appears shortly after the player activates an
/// item from the action bar, and lasts as long as one of that item's effects, is credited to that item. A buff named
/// after an ability the player activated just before is credited to that ability; one named after a passive skill of the
/// player's (Combat Trance) to that skill and its level. A buff that cannot be matched that way (another player's, or two
/// that fit) names no source.
/// </summary>
internal sealed partial class EnbModernHud
{
    internal readonly record struct BuffSource(string Heading, string Line, string Detail);
    private sealed record Activation(string Item, List<EnbItemSpecial> Effects, long Time);
    private Activation? lastActivation;
    private (EnbNext.Assets.EnbRetailSkillAbility Ability, long Time)? lastSkill;
    /// <summary>The player's level of a skill (0: not acquired).</summary>
    internal Func<string, int>? OwnSkillLevel { get; set; }

    /// <summary>The player activated a skill ability from the action bar: a buff named after it may follow.</summary>
    internal void SkillActivated(int abilityId)
    {
        if (assets.ResolveShortcutSkills()?.Abilities.FirstOrDefault(a => a.Id == abilityId) is { } ability) lastSkill = (ability, clientTime);
    }
    /// <summary>A buff's names as a skill would be called (Firing_Tactics: Firing Tactics).</summary>
    private IEnumerable<string> BuffNames(EnbSceneBuffSnapshot buff)
    {
        yield return buff.Type.Replace('_', ' ').Trim();
        if (buffCatalog?.Definitions.GetValueOrDefault(buff.Type) is { } definition) yield return definition.Name.Trim();
    }
    private readonly Dictionary<(int Slot, string Type, long Removal), BuffSource?> buffSources = [];
    private const long ActivationWindow = 15_000, DurationTolerance = 15_000;
    private static readonly Regex Duration = new(@"for\s+([0-9]+(?:\.[0-9]+)?)\s+(second|minute|hour)s?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The player activated an item from the action bar: its timed effects may become buffs.</summary>
    internal void ItemActivated(string item, ImmutableArray<EnbItemSpecial> effects)
    {
        var timed = effects.Where(e => Seconds(e.Tooltip) is not null).ToList();
        if (timed.Count > 0) lastActivation = new(item, timed, clientTime);
    }

    private static double? Seconds(string text)
    {
        var match = Duration.Match(text);
        if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) return null;
        return value * (char.ToLowerInvariant(match.Groups[2].Value[0]) switch { 'm' => 60, 'h' => 3600, _ => 1 });
    }

    /// <summary>Each new buff of the player's is matched once, when it is first seen.</summary>
    private void ObserveBuffSources()
    {
        foreach (var buff in playerBuffs)
        {
            if (buff.IsPermanent || !buff.IsVisible(clientTime)) continue;
            var key = (buff.Slot, buff.Type, buff.RemovalTime);
            if (!buffSources.ContainsKey(key)) buffSources[key] = Match(buff);
        }
        if (buffSources.Count > 64)
            foreach (var key in buffSources.Keys.Where(k => k.Removal < clientTime).ToList()) buffSources.Remove(key);
    }

    private BuffSource? Match(EnbSceneBuffSnapshot buff) => MatchItem(buff) ?? MatchAbility(buff);

    private BuffSource? MatchAbility(EnbSceneBuffSnapshot buff)
    {
        if (lastSkill is not { } skill || clientTime - skill.Time is < 0 or > ActivationWindow) return null;
        var ability = skill.Ability;
        if (!BuffNames(buff).Any(n => n.Equals(ability.Name, StringComparison.OrdinalIgnoreCase) || n.Equals(ability.FamilyName, StringComparison.OrdinalIgnoreCase))) return null;
        lastSkill = null;
        int level = OwnSkillLevel?.Invoke(ability.FamilyName) ?? 0;
        return new("From your " + ability.Name, level > 0 ? $"{ability.FamilyName} level {level.ToString(CultureInfo.InvariantCulture)}" : ability.FamilyName, ability.Description);
    }

    /// <summary>A passive skill of the player's named like the buff (only the player's own skill can give it).</summary>
    private BuffSource? PassiveSkill(EnbSceneBuffSnapshot buff)
    {
        if (assets.ResolveShortcutSkills() is not { } catalog || OwnSkillLevel is null) return null;
        foreach (string name in BuffNames(buff))
        {
            if (catalog.FindFamily(name) is not { } family || OwnSkillLevel(family.Name) is not (> 0 and var level)) continue;
            if (catalog.Abilities.Any(a => a.Activated && a.FamilyName.Equals(family.Name, StringComparison.OrdinalIgnoreCase))) continue;
            return new("From your " + family.Name + " skill", "Level " + level.ToString(CultureInfo.InvariantCulture), family.LevelDescriptions.GetValueOrDefault(level, family.Description));
        }
        return null;
    }

    private BuffSource? MatchItem(EnbSceneBuffSnapshot buff)
    {
        if (lastActivation is not { } activation || clientTime - activation.Time is < 0 or > ActivationWindow) return null;
        long remaining = buff.RemovalTime - clientTime;
        var fits = activation.Effects.Where(e => Math.Abs(remaining - (long)(Seconds(e.Tooltip)!.Value * 1000)) <= DurationTolerance).ToList();
        // Two effects of the same length could each be this buff: no guess.
        if (fits.Count != 1) return null;
        var effect = fits[0];
        activation.Effects.Remove(effect);
        string name = effect.Text.StartsWith("Effect: ", StringComparison.Ordinal) ? effect.Text[8..] : effect.Text;
        return new("From " + activation.Item, name, effect.Tooltip);
    }

    private static string TimeLeft(long milliseconds)
    {
        long seconds = Math.Max(0, (milliseconds + 999) / 1000);
        if (seconds >= 3600) return $"{seconds / 3600}h {seconds % 3600 / 60}m left";
        if (seconds >= 60) return $"{seconds / 60}m {seconds % 60}s left";
        return $"{seconds}s left";
    }

    /// <summary>The hovered buff (buff/p/&lt;slot&gt; or buff/t/&lt;slot&gt;) as a card.</summary>
    private EnbTooltipCard? BuffCard(string key)
    {
        bool own = key[5] == 'p';
        var list = own ? playerBuffs : targetBuffs;
        if (buffCatalog is null || !int.TryParse(key.AsSpan(7), NumberStyles.Integer, CultureInfo.InvariantCulture, out int slot)) return null;
        foreach (var buff in list)
        {
            if (buff.Slot != slot || !buffCatalog.Definitions.TryGetValue(buff.Type, out var definition)) continue;
            var sections = ImmutableArray.CreateBuilder<EnbTooltipCardSection>();
            string description = definition.Description.Replace("\\n", "\n", StringComparison.Ordinal).Trim();
            if (description.Length > 0) sections.Add(new("") { Text = description });
            if (buff.ScrubTypeName.Length > 0) sections.Add(new("") { Text = "(" + buff.ScrubTypeName + ")", TextColor = new(.7f, .75f, .85f) });
            var sources = buff.Elements.Where(e => e.SourceEntity.Length > 0 || e.SourceObject.Length > 0)
                .Select(e => new EnbTooltipCardLine((e.SourceEntity.Length == 0 ? "" : e.SourceEntity + ": ") + e.SourceObject, e.IsActive ? new(.86f, .9f, 1) : new(.55f, .58f, .65f)))
                .ToImmutableArray();
            if (!sources.IsEmpty) sections.Add(new("Sources") { Lines = sources });
            if (own && (buffSources.GetValueOrDefault((buff.Slot, buff.Type, buff.RemovalTime)) ?? PassiveSkill(buff)) is { } source)
                sections.Add(new(source.Heading) { Lines = [new(source.Line, new(1, .78f, .45f)), new(source.Detail, new(.86f, .9f, 1), true)] });
            return new(definition.Name)
            {
                TitleColor = definition.IsGood ? new(.55f, 1, .55f) : new(1, .5f, .45f),
                Subtitle = buff.IsPermanent ? null : TimeLeft(buff.RemovalTime - clientTime),
                SubtitleColor = new(.75f, .8f, .9f),
                Sections = sections.ToImmutable(),
            };
        }
        return null;
    }
}
