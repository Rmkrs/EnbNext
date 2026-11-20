namespace EnbNext.Engine;

/// <summary>One known reputation reaction: killing a ship of <see cref="Killed"/> changes the standing with
/// <see cref="Affected"/> by <see cref="Multiplier"/> times what the kill costs with <see cref="Killed"/> itself.</summary>
internal readonly record struct EnbFactionKillReaction(string Affected, string Killed, float Multiplier, bool Estimated = false);

/// <summary>
/// Next faction details (Modern UI): the known reputation reactions of a kill. The game
/// does not tell the client these; they are the Net-7 Wiki "Improved Faction Chart" (revision of 19 Jul 2026, as
/// Net7 Client Manager carries it): rows are the faction whose standing changes, columns the primary faction of the
/// killed ship, multipliers relative to the loss with the killed ship's own faction (the server truncates fractions).
/// The wiki marks the Alliance column's other entries as estimates: no Alliance ships are known.
/// Keys are the retail faction keys (cfaction_t.ini sections); names come from the installed ini.
/// </summary>
internal static class EnbFactionKillReactions
{
    private const bool E = true;

    /// <summary>The chart, by affected faction.</summary>
    internal static readonly EnbFactionKillReaction[] All =
    [
        new("Anseria", "Anseria", -1), new("Anseria", "PW", .5f), new("Anseria", "TW", .5f), new("Anseria", "TerranPsi", -.5f), new("Anseria", "JE", .5f),
        new("Bogeril", "Bogeril", -1), new("Bogeril", "TW", .5f),
        new("PW", "PW", -1), new("PW", "PT", -.5f), new("PW", "TT", -.5f), new("PW", "PE", -.5f), new("PW", "JE", .5f),
        new("PW", "Mordana", .5f), new("PW", "Pirate", .5f), new("PW", "V'Rix", .5f),
        new("CHVZ", "CHVZ", -1), new("CHVZ", "Pirate", .5f),
        new("PT", "PW", -.5f), new("PT", "PT", -1), new("PT", "PE", .5f), new("PT", "JT", .5f), new("PT", "Mordana", .5f),
        new("PT", "Pirate", .5f), new("PT", "V'Rix", .5f),
        new("TW", "Anseria", .5f), new("TW", "Bogeril", .5f), new("TW", "TW", -1), new("TW", "TT", -.5f), new("TW", "TerranPsi", -.5f),
        new("TW", "Alliance", -.5f, E), new("TW", "V'Rix", .5f),
        new("GC", "GC", -1),
        new("TE", "Bogeril", .5f), new("TE", "TW", -.5f), new("TE", "TE", -1), new("TE", "TT", .5f), new("TE", "PE", -.5f),
        new("TE", "JE", -.5f), new("TE", "V'Rix", .5f),
        new("TT", "Anseria", .5f), new("TT", "Bogeril", .5f), new("TT", "PW", -.5f), new("TT", "TW", -.5f), new("TT", "TE", .5f),
        new("TT", "TT", -1), new("TT", "TerranPsi", .5f), new("TT", "JE", .5f), new("TT", "Alliance", .5f, E), new("TT", "V'Rix", .5f),
        new("TerranPsi", "PW", .5f), new("TerranPsi", "TE", -.5f), new("TerranPsi", "TT", .5f), new("TerranPsi", "TerranPsi", -1),
        new("TerranPsi", "JT", -.5f), new("TerranPsi", "Alliance", -.5f, E), new("TerranPsi", "Mordana", .5f),
        new("TerranPsi", "Pirate", -.5f), new("TerranPsi", "V'Rix", .5f),
        new("PE", "PW", -.5f), new("PE", "PT", .5f), new("PE", "TE", -.5f), new("PE", "PE", -1), new("PE", "Mordana", .5f), new("PE", "V'Rix", .5f),
        new("JE", "TerranPsi", -.5f), new("JE", "JE", -1), new("JE", "JT", -.5f), new("JE", "V'Rix", .5f),
        new("JT", "PT", .5f), new("JT", "TerranPsi", -.5f), new("JT", "JE", -.5f), new("JT", "JT", -1), new("JT", "V'Rix", .5f),
        new("JW", "JT", -.5f), new("JW", "JW", -1), new("JW", "Mordana", .5f), new("JW", "V'Rix", .5f),
        new("Alliance", "PW", .5f), new("Alliance", "TW", -.5f), new("Alliance", "TT", .5f), new("Alliance", "TerranPsi", -.5f),
        new("Alliance", "Alliance", -1), new("Alliance", "Pirate", -.5f), new("Alliance", "V'Rix", .5f),
        new("Mordana", "PW", .5f), new("Mordana", "PT", .5f), new("Mordana", "TW", .5f), new("Mordana", "TerranPsi", .5f),
        new("Mordana", "JW", .5f), new("Mordana", "Alliance", -.5f, E), new("Mordana", "Mordana", -1), new("Mordana", "V'Rix", -.5f),
        new("Pirate", "PW", .5f), new("Pirate", "CHVZ", .5f), new("Pirate", "PT", .5f), new("Pirate", "TerranPsi", -.5f),
        new("Pirate", "Alliance", -.5f, E), new("Pirate", "Pirate", -1), new("Pirate", "V'Rix", 1),
        new("V'Rix", "Bogeril", .5f), new("V'Rix", "PW", .5f), new("V'Rix", "PT", .5f), new("V'Rix", "TW", .5f), new("V'Rix", "TE", .5f),
        new("V'Rix", "TT", .5f), new("V'Rix", "TerranPsi", .5f), new("V'Rix", "PE", .5f), new("V'Rix", "JT", .5f), new("V'Rix", "JW", .5f),
        new("V'Rix", "Alliance", .5f, E), new("V'Rix", "Mordana", -.5f), new("V'Rix", "Pirate", 1), new("V'Rix", "V'Rix", -1),
        new("PC", "PC", -1), new("PC", "TA", .5f),
        new("TA", "PC", .5f), new("TA", "TA", -1),
    ];

    /// <summary>The kills that change a faction's standing (the chart's row), gains first; its own ships left out.</summary>
    internal static List<EnbFactionKillReaction> Changing(string affected)
    {
        var list = new List<EnbFactionKillReaction>();
        foreach (var r in All) if (r.Affected == affected && r.Killed != affected) list.Add(r);
        list.Sort((a, b) => b.Multiplier.CompareTo(a.Multiplier));
        return list;
    }

    /// <summary>The chart's net change of killing a faction's ships: the sum of its column (its own loss included).</summary>
    internal static float Net(string killed)
    {
        float sum = 0;
        foreach (var r in All) if (r.Killed == killed) sum += r.Multiplier;
        return sum;
    }
}
