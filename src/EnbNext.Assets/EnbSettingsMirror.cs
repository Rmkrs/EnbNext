using System.Text;

namespace EnbNext.Assets;

/// <summary>
/// One settings section kept as one set for all characters: a character's own settings file mirrors a shared file.
/// A change made in the own file (this client saved it) goes to the shared file; a change in the shared file (another
/// client) is merged into the own file. What was last synchronised is remembered, so a merge that leaves the own file
/// as it is, or the file this client wrote itself, never goes back and forth between clients.
/// </summary>
public sealed class EnbSettingsMirror(Encoding encoding, Func<string, string?, string> merge)
{
    private (string? Own, string? Shared)? last;

    public enum Change { None, Shared, Taken }

    /// <summary>Forgets what was synchronised (the section is each character's own again, or another character is selected).</summary>
    public void Forget() => last = null;

    /// <summary>Makes the own file the shared settings (the section was just set to all characters from here).</summary>
    public void Seed(string ownPath, string sharedPath)
    {
        string? mine = Read(ownPath);
        if (mine is not null) Write(sharedPath, mine);
        last = (mine, mine);
    }

    /// <summary>Takes the shared settings into the own file before a character's settings are read; true when it changed.</summary>
    public bool Take(string ownPath, string sharedPath)
    {
        if (Read(sharedPath) is not { } shared) { last = null; return false; }
        string? mine = Read(ownPath);
        string merged = merge(shared, mine);
        last = (merged, shared);
        if (merged == mine) return false;
        Write(ownPath, merged);
        return true;
    }

    /// <summary>One synchronisation: a change here is shared, or a shared change is taken (the caller then applies it).</summary>
    public Change Step(string ownPath, string sharedPath)
    {
        string? mine = Read(ownPath), shared = Read(sharedPath);
        if (last is null && shared is null)
        {
            // Nothing shared yet: these settings start it.
            if (mine is not null) Write(sharedPath, mine);
            last = (mine, mine);
            return mine is null ? Change.None : Change.Shared;
        }
        if (last is { } known && mine is not null && mine != known.Own)
        {
            if (mine != shared) Write(sharedPath, mine);
            last = (mine, mine);
            return Change.Shared;
        }
        if (shared is not null && (last is null || shared != last.Value.Shared))
        {
            string merged = merge(shared, mine);
            last = (merged, shared);
            if (merged == mine) return Change.None;
            Write(ownPath, merged);
            return Change.Taken;
        }
        return Change.None;
    }

    public string? Read(string path) => File.Exists(path) ? File.ReadAllText(path, encoding) : null;

    public void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, text, encoding); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
