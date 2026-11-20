using System.Collections.Immutable;
using System.Buffers.Binary;
using EnbNext.Assets;
using EnbNext.Net7.World;

namespace EnbNext.Presentation;

/// <summary>Detached data read by retail requirement predicates 52F010, 531080, 52E500 and 52D900.</summary>
public sealed record EnbItemRequirementState(bool RpgAvailable, bool SkillsAvailable,
    ImmutableDictionary<string,int> SkillLevels, ImmutableDictionary<string,int> Fields)
{
    public static EnbItemRequirementState Empty { get; } = new(false,false,
        ImmutableDictionary<string,int>.Empty,ImmutableDictionary<string,int>.Empty);

    public static EnbItemRequirementState Capture(Net7AuxDataSchema player, EnbRetailSkillCatalog? catalog)
    {
        Net7AuxDataProperty? Child(Net7AuxDataProperty? p,string name)=>p?.Children.FirstOrDefault(c=>c.Path[(c.Path.LastIndexOf('.')+1)..]==name);
        var rpg=Child(player.Root,"RPGInfo");var skills=Child(rpg,"Skills");
        var levels=ImmutableDictionary.CreateBuilder<string,int>(StringComparer.OrdinalIgnoreCase);
        foreach(var family in catalog?.Families ?? [])
        {
            if(skills is null || family.Id<0 || family.Id>=skills.Children.Count)continue;
            // The aggregate state gates lookup; the mapped child's retained level is then read directly.
            levels[family.Name]=Child(skills.Children[family.Id],"Level")?.Value is int level?level:0;
        }
        var fields=ImmutableDictionary.CreateBuilder<string,int>();
        foreach(string name in new[]{"CombatLevel","ExploreLevel","TradeLevel","Race","Profession"})
            if(Child(rpg,name) is {State:not 0} p)
                fields[name]=p.Value is int n?n:p.Value is byte[] {Length:4} bytes?BinaryPrimitives.ReadInt32LittleEndian(bytes):0;
        return new(rpg is {State:not 0},skills is {State:not 0},levels.ToImmutable(),fields.ToImmutable());
    }
}
