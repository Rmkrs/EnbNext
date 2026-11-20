using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>Authored ambient actor construction, client 00642740.</summary>
public sealed record EnbStationAmbientNpcDefinition(bool Sequential,int MinWait,int MaxWait,
    int RelocateChance,int ActionChance,IReadOnlyList<Matrix4x4> Offsets,
    IReadOnlyList<Matrix4x4> Destinations,IReadOnlyList<EnbStationAmbientNpcAction> Actions)
{
    public Matrix4x4 InitialTransform=>Destinations.Count==0?Matrix4x4.Identity:Destinations[0];
    public static EnbStationAmbientNpcDefinition? Load(EnbRetailAssetBundle assets,uint station,
        EnbSceneStationNpc npc,EnbCurrentRetailStationInterior room)
    {
        if(room.ExtraAttachments is not {} hierarchy)return null;
        string prefix=station switch {0=>"t",1=>"j",2=>"p",_=>"n"};
        string path=Path.Combine(assets.Paths.ClientDataDirectory,"ini",prefix+"starbase_ambient.ini");
        if(!EnbNext.Installation.EnbPathCase.FileExists(path))return null;
        if(!EnbW3dHierarchyPose.TryEvaluate(hierarchy,Matrix4x4.Identity,out var pose,out var error))
            throw new InvalidDataException(error);
        return Read(EnbRetailIniDocument.Load(path),npc.Name,name=>
            pose[EnbCurrentRetailAggregateBoneLookup.Resolve(hierarchy,name)]);
    }
    public static EnbStationAmbientNpcDefinition Read(EnbRetailIniDocument ini,string name,Func<string,Matrix4x4> bone)
    {
        // Native snprintf's section and bone buffers are 32 bytes including NUL.
        ini.TryGetSection(Short(name),out var found);
        IReadOnlyDictionary<string,string> values=found??new Dictionary<string,string>();
        int Number(string key,int fallback)=>EnbRetailIniDocument.TryReadInt(values,key,out int value)?value:fallback;
        string Text(string key)=>values.TryGetValue(key,out var value)?value:"";
        List<Matrix4x4> offsets=[],destinations=[];
        string offset=Text("OffsetBoneName"),destination=Text("DestBoneName");
        for(int i=0;values.ContainsKey($"Location{i:00}");i++)
        {
            int location=Number($"Location{i:00}",0);
            if(offset.Length>0)offsets.Add(bone(Short($"{offset}{location:00}")));
            destinations.Add(bone(Short($"{destination}{location:00}")));
        }
        List<EnbStationAmbientNpcAction> actions=[];
        int high=-1;
        for(int i=0;values.ContainsKey($"Action{i:00}");i++)
        {
            int low=unchecked(high+1);
            high=unchecked(high+Number($"Chance{i:00}",1));
            actions.Add(new(Number($"Action{i:00}",200),low,high,Text($"ExtraName{i:00}"),Text($"ExtraAnim{i:00}")));
        }
        return new(EnbRetailIniDocument.ReadBool(values,"Sequential"),Number("MinWait",5),Number("MaxWait",15),
            Number("RelocateChance",33),Number("ActionChance",67),offsets,destinations,actions);
        static string Short(string value)=>value.Length>31?value[..31]:value;
    }
}
public sealed record EnbStationAmbientNpcAction(int Number,int Low,int High,string ExtraName,string ExtraAnimation);
public sealed record EnbStationAmbientNpcDecision(IReadOnlyList<Matrix4x4> Route,bool TrimHistory,EnbStationAmbientNpcAction? Action);

/// <summary>0078ACE0/0078B660 scheduler and 0078B810/BA20/BB60 selectors.
/// The actor supplies its previous update's idle flag; movement/animation owns completion.</summary>
public sealed class EnbStationAmbientNpcSchedule(EnbStationAmbientNpcDefinition definition,uint variant)
{
    private float elapsed,wait=1;
    private bool first=true;
    public int Location {get;private set;}
    public float Elapsed=>elapsed;
    public float Wait=>wait;
    public EnbStationAmbientNpcDecision? Advance(uint deltaMilliseconds,bool idle,bool interacting,Func<int,int,int>? random=null)
    {
        if(variant!=3 || !idle || interacting)return null;
        elapsed+=(float)deltaMilliseconds*.001f;
        if(elapsed<=wait)return null;
        random??=EnbRetailPresentationRandom.Shared.Next;
        int choice=random(0,unchecked(definition.RelocateChance+definition.ActionChance));
        EnbStationAmbientNpcAction? action=null;
        List<Matrix4x4> route=[];
        if(choice>definition.RelocateChance && (!first || definition.RelocateChance<1))
        {
            int high=definition.Actions.Select(a=>a.High).Prepend(0).Max();
            int selected=random(0,high);
            action=definition.Actions.FirstOrDefault(a=>selected>=a.Low && selected<=a.High);
        }
        else
        {
            int last=definition.Destinations.Count-1;
            int selected=random(0,last);
            if(first)selected=Math.Min(1,last);
            // Retail indexes an empty destination vector here. Keep that malformed
            // optional route local to this actor, without inventing a destination.
            if(selected>=0 && selected<definition.Destinations.Count && selected!=Location)
            {
                bool offsets=definition.Offsets.Count==definition.Destinations.Count;
                if(definition.Sequential)
                {
                    int step=selected>Location?1:-1;
                    for(int i=Location+step;;i+=step)
                    {
                        route.Add(offsets?definition.Offsets[i]:definition.Destinations[i]);
                        if(i!=selected)continue;
                        if(offsets)route.Add(definition.Destinations[i]);
                        break;
                    }
                }
                else
                {
                    if(offsets)route.Add(definition.Offsets[selected]);
                    route.Add(definition.Destinations[selected]);
                }
                Location=selected;
            }
        }
        elapsed=0;
        wait=random(definition.MinWait,definition.MaxWait);
        first=false;
        return new(route,!definition.Sequential,action);
    }
}
