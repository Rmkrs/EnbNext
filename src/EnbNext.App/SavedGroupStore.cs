using System.Text.Json;
using EnbNext.Client.Models;

namespace EnbNext.App;

internal sealed class SavedGroupStore(string path)
{
    public static string DefaultPath => Path.Combine(Path.GetDirectoryName(SavedAccountStore.DefaultPath)!, "groups.json");
    public SavedLoginGroup[] Groups { get; private set; } = [];
    public void Load()
    {
        if (!File.Exists(path)) { Groups = []; return; }
        var groups = JsonSerializer.Deserialize<SavedLoginGroup[]>(File.ReadAllText(path));
        if (groups is null || groups.Any(g => g is null || LoginGroupRules.Validate(g) is not null) ||
            groups.Select(g => g.Id).Distinct().Count() != groups.Length ||
            groups.Select(g => g.Server.Trim()+"/"+g.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != groups.Length)
            throw new InvalidDataException("Saved groups could not be read. The existing file was left unchanged.");
        Groups = groups;
    }
    public SavedLoginGroup? Find(string server, string name) => Groups.FirstOrDefault(g => SavedAccountStore.Same(g.Server, server) && SavedAccountStore.Same(g.Name, name));
    public void Save(SavedLoginGroup group)
    {
        if (LoginGroupRules.Validate(group) is {} error) throw new InvalidDataException(error);
        var next = Groups.Where(g => g.Id != group.Id && !(SavedAccountStore.Same(g.Server, group.Server) && SavedAccountStore.Same(g.Name, group.Name)))
            .Append(group with { Name = group.Name.Trim() }).ToArray();
        Write(next);
    }
    public void Delete(Guid id)=>Write(Groups.Where(g=>g.Id!=id).ToArray());
    private void Write(SavedLoginGroup[] next)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(next)); File.Move(temporary,path,true); Groups=next; }
        finally { if(File.Exists(temporary))File.Delete(temporary); }
    }
    public static string DisplayPath(string preferencesPath, Guid group, Guid member) =>
        Path.Combine(Path.GetDirectoryName(preferencesPath)!, "groups", group.ToString("N"), member.ToString("N")+".json");
}
