using System.Text.Json;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Engine;

internal sealed class EnbUiLayoutProfile
{
    public Guid Id {get;set;}=Guid.NewGuid();
    public string Name {get;set;}="New profile";
    public Dictionary<EnbUiGroup,EnbUiPlacement> Docked {get;set;}=[];
    public Dictionary<EnbUiGroup,EnbUiPlacement> Space {get;set;}=[];
    internal EnbUiLayoutProfile Copy(bool newIdentity=false)=>new(){Id=newIdentity?Guid.NewGuid():Id,Name=Name,Docked=new(Docked),Space=new(Space)};
}
internal sealed class EnbUiLayoutDocument
{
    public int Version {get;set;}=1;
    public Guid Selected {get;set;}
    public List<EnbUiLayoutProfile> Profiles {get;set;}=[];
    /// <summary>Next panel layout options chosen in edit mode (EnbUiLayoutEditor.PanelOptions keys). They hold for every
    /// profile, Default included, so a display choice needs no profile of its own.</summary>
    public List<string> Options {get;set;}=[];
    internal EnbUiLayoutDocument Copy()=>new(){Selected=Selected,Profiles=Profiles.Select(p=>p.Copy()).ToList(),Options=new(Options)};
}

// Owns one committed document and, only while editing, one transactional draft.
// Guid.Empty is the built-in Default, which is never stored as an editable row.
internal sealed class EnbUiLayoutProfiles
{
    private string? path;
    private EnbCharacterInterfaceSettings? character;
    private EnbUiLayoutDocument committed=new();
    private EnbUiLayoutDocument? draft;
    private readonly EnbUiPlacement[] placements=new EnbUiPlacement[Enum.GetValues<EnbUiGroup>().Length];
    private ulong visibleGroups;
    internal bool Visible(EnbUiGroup group)=>!Editing||group==EnbUiGroup.None||(visibleGroups&(1ul<<(int)group))!=0;
    internal void Toggle(EnbUiGroup group){RequireEdit();visibleGroups^=1ul<<(int)group;}
    internal bool Preview(EnbUiGroup group)=>Editing&&Visible(group)&&(Listed?.Invoke(group)??true);
    /// <summary>Whether the editor's current element list holds a group (classic or Modern, docked or in space).</summary>
    internal Func<EnbUiGroup,bool>? Listed {get;set;}
    internal bool Editing=>draft is not null;
    internal bool Docked {get;private set;}
    internal EnbUiSize Size {get;private set;}
    internal string? Error {get;private set;}
    internal EnbUiLayoutDocument Document=>draft??committed;
    internal EnbUiLayoutProfile? Selected {get{var d=Document;foreach(var p in d.Profiles)if(p.Id==d.Selected)return p;return null;}}
    internal bool Locked=>Selected is null;
    internal EnbUiLayoutProfiles(string? path)
    {
        this.path=path;
        try
        {
            if(EnbNext.Installation.EnbPathCase.FileExists(path))
            {
                var loaded=JsonSerializer.Deserialize<EnbUiLayoutDocument>(File.ReadAllText(EnbNext.Installation.EnbPathCase.Resolve(path)))??throw new InvalidDataException("Empty layout file.");
                Validate(loaded);committed=loaded;
            }
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {Error="Could not load UI profiles: "+e.Message;}
        Refresh();
    }
    internal void OpenStore(string source, EnbCharacterInterfaceSettings? settings = null)
    {
        if(Editing)throw new InvalidOperationException("Cannot change profile storage during editing.");
        var loaded=new EnbUiLayoutProfiles(source);path=source;committed=loaded.committed;Error=loaded.Error;character=settings;
        if(character is not null) committed.Selected=character.LayoutProfile;
        NormalizeSelection(committed);if(character is not null)character.LayoutProfile=committed.Selected;Refresh();
    }
    /// <summary>The profiles and this character's choice were replaced on disk (shared settings): read them again, unless editing.</summary>
    internal void Reload(){if(!Editing&&path is not null)OpenStore(path,character);}
    internal static string DefaultPath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"EnbNext","ui-layouts.json");
    internal void SetContext(bool docked,EnbUiSize size){Size=size;if(Docked==docked)return;Docked=docked;Refresh();}
    internal EnbUiPlacement Placement(EnbUiGroup group)=>placements[(int)group];
    /// <summary>A panel layout option (for every profile).</summary>
    internal bool Option(string key)=>Document.Options.Contains(key);
    /// <summary>Sets a panel layout option. Keys of the form "Panel/Choice=value" are one choice among their siblings:
    /// turning one on turns the others off; turning it off leaves the panel's own default.</summary>
    internal void SetOption(string key,bool on)
    {
        RequireEdit();draft!.Options.Remove(key);
        int split=key.IndexOf('=');if(on&&split>0){string family=key[..(split+1)];draft.Options.RemoveAll(o=>o.StartsWith(family,StringComparison.Ordinal));}
        if(on)draft.Options.Add(key);
    }
    internal EnbUiRect Apply(EnbUiGroup group,EnbUiRect rect)=>Placement(group).Apply(rect,Size);
    internal EnbUiPoint Inverse(EnbUiGroup group,EnbUiPoint point)=>Placement(group).Inverse(point,Size);
    internal void Begin()
    {
        if(Editing)return;
        try{if(EnbNext.Installation.EnbPathCase.FileExists(path)){var latest=JsonSerializer.Deserialize<EnbUiLayoutDocument>(File.ReadAllText(EnbNext.Installation.EnbPathCase.Resolve(path)))??throw new InvalidDataException("Empty layout file.");Validate(latest);latest.Selected=committed.Selected;NormalizeSelection(latest);committed=latest;Error=null;}}
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException){Error="Could not load UI profiles: "+e.Message;}
        draft=committed.Copy();visibleGroups=0;foreach(var group in new[]{EnbUiGroup.Cockpit,EnbUiGroup.Buffs,EnbUiGroup.Scanner,EnbUiGroup.Chat,EnbUiGroup.Group,EnbUiGroup.Target,EnbUiGroup.Utility,EnbUiGroup.Multibox,EnbUiGroup.DamageMeter,EnbUiGroup.PlayerFrame,EnbUiGroup.TargetFrame,EnbUiGroup.ActionBar,EnbUiGroup.Engine,EnbUiGroup.CombatXp,EnbUiGroup.ExploreXp,EnbUiGroup.TradeXp,EnbUiGroup.TargetButtons,EnbUiGroup.Tooltip,EnbUiGroup.ModernChat,EnbUiGroup.ModernGroup})visibleGroups|=1ul<<(int)group;Refresh();
    }
    internal void Cancel(){draft=null;Error=null;Refresh();}
    internal void Select(Guid id)
    {RequireEdit();if(id!=Guid.Empty&&!draft!.Profiles.Any(p=>p.Id==id))throw new ArgumentException("Unknown profile.");draft!.Selected=id;Refresh();}
    internal void Create(string name,bool duplicate)
    {
        RequireEdit();name=ValidateName(name);
        if(Document.Profiles.Any(p=>p.Name.Equals(name,StringComparison.OrdinalIgnoreCase)))throw new ArgumentException("That profile name is already used.");
        var p=duplicate&&Selected is {} source?source.Copy(true):new EnbUiLayoutProfile();p.Name=name;
        draft!.Profiles.Add(p);draft.Selected=p.Id;Refresh();
    }
    internal void Rename(string name){RequireEdit();if(Selected is not {} p)throw new InvalidOperationException("Default is locked.");p.Name=ValidateName(name);}
    internal void Delete()
    {
        RequireEdit();if(Selected is not {} selected)throw new InvalidOperationException("Default cannot be deleted.");
        draft!.Profiles.Remove(selected);draft.Selected=Guid.Empty;Refresh();
    }
    internal void Set(EnbUiGroup group,EnbUiPlacement placement)
    {
        RequireEdit();if(group==EnbUiGroup.None||!Enum.IsDefined(group)||!placement.IsValid)throw new ArgumentException("Invalid placement.");
        if(Selected is not {} p)throw new InvalidOperationException("Duplicate Default to edit it.");
        (Docked?p.Docked:p.Space)[group]=placement;Refresh();
    }
    internal bool Save()
    {
        RequireEdit();if(path is null){Error="No UI profile store is configured.";return false;}string? temporary=null;
        try
        {
            Validate(draft!);Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using var writeLock=new FileStream(path+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            var latest=EnbNext.Installation.EnbPathCase.FileExists(path)?JsonSerializer.Deserialize<EnbUiLayoutDocument>(File.ReadAllText(EnbNext.Installation.EnbPathCase.Resolve(path)))??throw new InvalidDataException("Empty layout file."):new EnbUiLayoutDocument();
            Validate(latest);var saving=Merge(latest);
            temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {var stored=saving.Copy(); if(character is not null)stored.Selected=Guid.Empty;
                JsonSerializer.Serialize(stream,stored,new JsonSerializerOptions{WriteIndented=true});stream.Flush(true);}
            File.Move(temporary,EnbNext.Installation.EnbPathCase.Resolve(path),true);temporary=null;
            character?.Save(character.RoutePlanner,character.AutoPilot,saving.Selected);
            committed=saving;draft=null;Error=null;Refresh();return true;
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {Error="Could not save UI profiles: "+e.Message;return false;}
        finally{if(temporary is not null)try{File.Delete(temporary);}catch(IOException){}catch(UnauthorizedAccessException){}}
    }
    private EnbUiLayoutDocument Merge(EnbUiLayoutDocument latest)
    {
        var result=latest.Copy();result.Selected=draft!.Selected;result.Options=new(draft.Options);
        foreach(var removed in committed.Profiles)
        {
            if(draft.Profiles.Any(p=>p.Id==removed.Id))continue;
            int index=result.Profiles.FindIndex(p=>p.Id==removed.Id);
            if(index<0)continue;
            if(!Same(result.Profiles[index],removed))throw new InvalidDataException("Profile '"+removed.Name+"' changed in another client. Cancel and reopen before deleting it.");
            result.Profiles.RemoveAt(index);
        }
        foreach(var edited in draft.Profiles)
        {
            var before=committed.Profiles.FirstOrDefault(p=>p.Id==edited.Id);
            if(before is not null&&Same(before,edited))continue;
            int index=result.Profiles.FindIndex(p=>p.Id==edited.Id);
            if(index>=0&&before is not null&&!Same(result.Profiles[index],before)&&!Same(result.Profiles[index],edited))
                throw new InvalidDataException("Profile '"+edited.Name+"' changed in another client. Cancel and reopen Edit UI before changing that profile.");
            if(index<0&&before is not null)throw new InvalidDataException("Profile '"+edited.Name+"' was deleted in another client. Cancel and reopen before changing it.");
            if(index<0)result.Profiles.Add(edited.Copy());else result.Profiles[index]=edited.Copy();
        }
        Validate(result);return result;
    }
    private static bool Same(EnbUiLayoutProfile a,EnbUiLayoutProfile b)=>a.Name==b.Name&&Same(a.Docked,b.Docked)&&Same(a.Space,b.Space);
    private static bool Same(Dictionary<EnbUiGroup,EnbUiPlacement> a,Dictionary<EnbUiGroup,EnbUiPlacement> b)=>a.Count==b.Count&&a.All(p=>b.TryGetValue(p.Key,out var value)&&value==p.Value);
    private void Refresh()
    {
        Array.Fill(placements,EnbUiPlacement.Identity);
        if(Selected is {} p)foreach(var pair in Docked?p.Docked:p.Space)placements[(int)pair.Key]=pair.Value;
    }
    private void RequireEdit(){if(!Editing)throw new InvalidOperationException("Edit mode is closed.");}
    private string ValidateName(string name)
    {
        name=name.Trim();if(name.Length==0||name.Length>80||name.Any(char.IsControl)||name.Equals("Default",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Use a name of 1–80 characters other than Default.");
        if(Document.Profiles.Any(p=>p.Id!=Document.Selected&&p.Name.Equals(name,StringComparison.OrdinalIgnoreCase)))throw new ArgumentException("That profile name is already used.");return name;
    }
    private static void NormalizeSelection(EnbUiLayoutDocument d)
    { if(d.Selected!=Guid.Empty&&!d.Profiles.Any(p=>p.Id==d.Selected))d.Selected=Guid.Empty; }
    private static void Validate(EnbUiLayoutDocument d)
    {
        if(d.Version!=1||d.Profiles is null)throw new InvalidDataException("Unsupported layout file.");
        d.Options=d.Options?.Where(o=>!string.IsNullOrWhiteSpace(o)).Distinct(StringComparer.Ordinal).ToList()??[];
        var ids=new HashSet<Guid>();var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase){"Default"};
        foreach(var p in d.Profiles)
        {
            if(p is null||p.Id==Guid.Empty||!ids.Add(p.Id)||string.IsNullOrWhiteSpace(p.Name)||p.Name.Length>80||p.Name.Any(char.IsControl)||!names.Add(p.Name)||p.Docked is null||p.Space is null)throw new InvalidDataException("Invalid layout profile.");
            foreach(var pair in p.Docked.Concat(p.Space))if(pair.Key==EnbUiGroup.None||!Enum.IsDefined(pair.Key)||!pair.Value.IsValid)throw new InvalidDataException("Invalid group placement.");
        }
        NormalizeSelection(d);
    }
}
