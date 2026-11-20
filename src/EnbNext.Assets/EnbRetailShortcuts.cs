using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;

namespace EnbNext.Assets;

public enum EnbRetailShortcutSource { Equipment, Cargo, Skill }

/// <summary>Two independent bars, two groups per bar, three visible positions per group.</summary>
public readonly record struct EnbRetailShortcutAddress(int Bar, int Group, int Button)
{
    public bool IsValid => (uint)Bar < 2 && (uint)Group < 2 && (uint)Button < 3;
}

public sealed record EnbRetailShortcutEntry(EnbRetailShortcutSource Source,
    EnbRetailShortcutAddress Address, string Key, int? SavedIndex, string? FamilyName, string? AbilityName);

public sealed record EnbRetailShortcutSection(EnbRetailShortcutSource Source, string Name,
    bool Exists, bool PreviouslyCreated, ImmutableArray<EnbRetailShortcutEntry> Entries);

/// <summary>
/// Retail saved assignments, not an activation policy. Current retail 006136D0/00614980
/// select character sections; 006A8430 reads the item slot token; 006A6C00 restores skills
/// by family and ability NAME after reading the base numeric token. Missing sections must
/// not borrow another character's assignments or imply that first-run placement is closed.
/// </summary>
public sealed record EnbRetailShortcuts(string? CharacterKey, ImmutableArray<EnbRetailShortcutSection> Sections,
    ImmutableArray<string> Diagnostics)
{
    public bool TryMove(EnbRetailShortcutEntry source,EnbRetailShortcutAddress? destination,out EnbRetailShortcuts updated)
    {
        updated=this;
        if(!source.Address.IsValid||destination is {} target&&!target.IsValid||
            Sections.SelectMany(s=>s.Entries).Count(e=>e.Address==source.Address)!=1||
            !Sections.Any(s=>s.Source==source.Source&&s.Entries.Contains(source)))return false;
        if(destination==source.Address)return true;
        updated=this with {Sections=Sections.Select(s=>s with {Entries=s.Entries
            .Where(e=>e.Address!=source.Address&&e.Address!=destination)
            .Concat(destination is {} address&&s.Source==source.Source?[source with {Address=address}]:[])
            .ToImmutableArray()}).ToImmutableArray()};
        return true;
    }

    public void SaveOutput(string directory,uint character)
    {
        if(character==0||CharacterKey!=Resolve(EnbRetailIniDocument.Parse(""),character).CharacterKey)
            throw new ArgumentException("Shortcut storage requires the matching character identity.");
        var text=new System.Text.StringBuilder();
        string path=Path.Combine(directory,"shortcut.ini");
        // Replace only this character's three retail sections. Read the shared file
        // at save time so changes to other characters since session load survive.
        var expected=Resolve(EnbRetailIniDocument.Parse(""),character);
        var names=expected.Sections.Select(s=>s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if(Sections.Length!=3||Sections.Any(s=>!names.Contains(s.Name))||
            Sections.Select(s=>s.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=3)
            throw new InvalidDataException("Invalid shortcut sections.");
        if(EnbNext.Installation.EnbPathCase.FileExists(path))
        {
            bool keep=true;
            foreach(string line in File.ReadLines(EnbNext.Installation.EnbPathCase.Resolve(path)))
            {
                var header=EnbRetailIniDocument.Parse(line).SectionNames.FirstOrDefault();
                if(header is not null)keep=!names.Contains(header);
                if(keep)text.Append(line).Append("\r\n");
            }
            text.Append("\r\n");
        }
        foreach(var section in Sections)
        {
            text.Append("["+section.Name+"]").Append("\r\n").Append("ShortcutBeenCreated=true").Append("\r\n").Append("Num="+section.Entries.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            for(int i=0;i<section.Entries.Length;i++)
            {
                var entry=section.Entries[i];
                if(!entry.Address.IsValid||entry.Key.Contains('\r')||entry.Key.Contains('\n'))throw new InvalidDataException("Invalid shortcut assignment.");
                text.Append($"{i}_Key={entry.Key}").Append("\r\n").Append($"{i}_Bar={entry.Address.Bar}").Append("\r\n")
                    .Append($"{i}_Group={entry.Address.Group}").Append("\r\n").Append($"{i}_Button={entry.Address.Button}").Append("\r\n");
            }
            text.Append("\r\n");
        }
        Directory.CreateDirectory(directory);
        string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try { File.WriteAllText(temporary,text.ToString());File.Move(temporary,EnbNext.Installation.EnbPathCase.Resolve(path),true); }
        finally { if(EnbNext.Installation.EnbPathCase.FileExists(temporary))File.Delete(temporary); }
    }

    public static EnbRetailShortcuts Load(EnbInstalledAssetPaths paths, uint? fixedSessionId)
    {
        string path = Path.Combine(paths.ClientOutputDirectory, "shortcut.ini");
        if (!EnbNext.Installation.EnbPathCase.FileExists(path)) return Resolve(EnbRetailIniDocument.Parse(""), fixedSessionId);
        // Allow a retail reader/writer while loading the shared assignments.
        using var stream = new FileStream(EnbNext.Installation.EnbPathCase.Resolve(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return Resolve(EnbRetailIniDocument.Parse(reader.ReadToEnd()), fixedSessionId);
    }

    public static EnbRetailShortcuts Resolve(EnbRetailIniDocument document, uint? fixedSessionId)
    {
        string? character = fixedSessionId is > 0
            ? BinaryPrimitives.ReverseEndianness(fixedSessionId.Value).ToString(CultureInfo.InvariantCulture) : null;
        var sections = ImmutableArray.CreateBuilder<EnbRetailShortcutSection>();
        var diagnostics = ImmutableArray.CreateBuilder<string>();
        foreach (var (source, suffix) in new[] { (EnbRetailShortcutSource.Equipment, "PDA_EQUIP"),
            (EnbRetailShortcutSource.Cargo, "PDA_CARGO"), (EnbRetailShortcutSource.Skill, "Skills") })
        {
            string name = character is null ? suffix : character + "_" + suffix;
            var entries = ImmutableArray.CreateBuilder<EnbRetailShortcutEntry>();
            bool exists = character is not null && document.TryGetSection(name, out _);
            bool created = false;
            if (exists && document.TryGetSection(name, out var values))
            {
                created = EnbRetailIniDocument.ReadBool(values, "ShortcutBeenCreated");
                if (EnbRetailIniDocument.TryReadInt(values, "Num", out int count) && count > 0)
                {
                    // Enumerate present entry keys instead of looping an untrusted count. The
                    // retail Num bound is retained, including holes and recorded order.
                    var indices = values.Keys.Where(k => k.EndsWith("_Key", StringComparison.OrdinalIgnoreCase))
                        .Select(k => int.TryParse(k.AsSpan(0, k.Length - 4), NumberStyles.None,
                            CultureInfo.InvariantCulture, out int i) ? i : -1)
                        .Where(i => i >= 0 && i < count).Distinct().Order();
                    foreach (int i in indices)
                    {
                        if (!EnbRetailIniDocument.TryReadInt(values, $"{i}_Bar", out int bar) ||
                            !EnbRetailIniDocument.TryReadInt(values, $"{i}_Group", out int group) ||
                            !EnbRetailIniDocument.TryReadInt(values, $"{i}_Button", out int button) ||
                            !new EnbRetailShortcutAddress(bar, group, button).IsValid)
                        { diagnostics.Add($"{name}/{i}: invalid address"); continue; }
                        string key = values[$"{i}_Key"];
                        var tokens = Tokens(key);
                        int? index = tokens.Length > 0 && int.TryParse(tokens[0], NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out int parsed) ? parsed : null;
                        bool skill = source == EnbRetailShortcutSource.Skill;
                        if (index is null || tokens.Length != (skill ? 3 : 1))
                        { diagnostics.Add($"{name}/{i}: malformed saved key"); index = null; }
                        entries.Add(new(source, new(bar, group, button), key, index,
                            skill && tokens.Length == 3 ? tokens[1] : null,
                            skill && tokens.Length == 3 ? tokens[2] : null));
                    }
                }
            }
            sections.Add(new(source, name, exists, created, entries.ToImmutable()));
        }
        return new(character, sections.ToImmutable(), diagnostics.ToImmutable());
    }

    private static ImmutableArray<string> Tokens(string key)
    {
        var result = ImmutableArray.CreateBuilder<string>();
        int position = 0;
        while (position < key.Length)
        {
            if (key[position] != '<') return [];
            int end = key.IndexOf("/>", position + 1, StringComparison.Ordinal);
            if (end < 0) return [];
            result.Add(key[(position + 1)..end]); position = end + 2;
        }
        return result.ToImmutable();
    }
}
