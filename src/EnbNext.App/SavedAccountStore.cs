using System.Text.Json;
using EnbNext.Client.Models;

namespace EnbNext.App;

internal sealed class SavedAccountStore(string path)
{
    public static string DefaultPath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"EnbNext","accounts.json");
    public SavedGameAccount[] Accounts {get;private set;}=[];
    public void Load()
    {
        if(!File.Exists(path)){Accounts=[];return;}
        var value=JsonSerializer.Deserialize<SavedGameAccount[]>(File.ReadAllText(path));
        if(value is null || value.Any(a=>a is null || string.IsNullOrWhiteSpace(a.Login)||string.IsNullOrWhiteSpace(a.Server)||a.Characters is null)
            || value.Select(a=>a.Id).Distinct().Count()!=value.Length)throw new InvalidDataException("Saved accounts could not be read. The existing file has been left unchanged.");
        Accounts=value;
    }
    public void Save(SavedGameAccount[] value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllText(temporary,JsonSerializer.Serialize(value,new JsonSerializerOptions{WriteIndented=true}));File.Move(temporary,path,true);Accounts=value;}
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    public SavedGameAccount? Find(string server,string login)=>Accounts.FirstOrDefault(a=>Same(a.Server,server)&&Same(a.Login,login));
    internal static bool Same(string a,string b)=>string.Equals(a.Trim(),b.Trim(),StringComparison.OrdinalIgnoreCase);
    public static SavedGameCharacter[] Roster(IReadOnlyList<CharacterSummary> characters)=>characters.Where(c=>!c.IsEmpty)
        .Select(c=>new SavedGameCharacter(c.Slot,c.FullName,c.RaceName,c.ClassName,c.OverallLevel)).ToArray();
    public static (SavedGameAccount[] Accounts,string[] Changes) Preview(SavedGameAccount[] current,string server,SavedGameAccount[] incoming)
    {
        var result=current.ToList();var changes=new List<string>();
        foreach(var source in incoming)
        {
            int index=result.FindIndex(a=>Same(a.Server,server)&&Same(a.Login,source.Login));
            var prior=index<0?null:result[index];
            if(prior is null)changes.Add("Add account: "+source.Login);
            var characters=source.Characters.Select(c=>
            {
                var old=prior?.Characters.FirstOrDefault(p=>Same(p.Name,c.Name));
                return c with {Race=c.Race??old?.Race,Profession=c.Profession??old?.Profession,Level=c.Level??old?.Level};
            }).ToArray();
            foreach(var character in characters)
            {
                var old=prior?.Characters.FirstOrDefault(p=>p.Slot==character.Slot);
                if(old!=character)changes.Add($"{source.Login}, slot {character.Slot+1}: {character.Name} ({character.Profession??"class unknown"})");
            }
            foreach(var old in prior?.Characters??[])
                if(!characters.Any(c=>Same(c.Name,old.Name)))changes.Add($"{source.Login}: remove character {old.Name}");
            var next=new SavedGameAccount(prior?.Id??Guid.NewGuid(),server,source.Login,characters);
            if(index<0)result.Add(next);else result[index]=next;
        }
        int untouched=current.Count(a=>!Same(a.Server,server)||!incoming.Any(i=>Same(a.Login,i.Login)));
        changes.Add($"Other saved accounts left unchanged: {untouched}");
        return(result.ToArray(),changes.ToArray());
    }
}
