using EnbNext.Assets;
using EnbNext.Net7.Presentation;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>63CA50 construction and 63AC60 rank-triggered label refresh.</summary>
public sealed class EnbStationGuildLabel
{
    public string Text {get;private set;}="";
    public int Rank {get;private set;}=-1;
    public EnbRgb? Color {get;private set;}
    public EnbStationGuildLabel(Net7StationGuildState? initial)
    {
        if(initial?.Name is not null)Assign(initial,new(.33f,.33f,.99f));
    }
    public void Refresh(Net7StationGuildState? current,EnbRgb fallback)
    {
        if(current is null || current.Rank==Rank)return;
        if(current.Rank==-1){Rank=-1;Text="";return;}
        // Missing optional metadata cannot supply a drawable guild string.
        if(current.Name is not null)Assign(current,fallback);
    }
    private void Assign(Net7StationGuildState data,EnbRgb fallback)
    {
        Rank=data.Rank;
        bool same=data.Name is not null && data.LocalName is not null && string.Equals(data.Name,data.LocalName,StringComparison.Ordinal);
        Text=Rank==-1?"":same?$"[{data.Name} {data.RankName}]":$"[{data.Name}]";
        var color=same?data.SameColor:data.OtherColor;
        Color=color is {} value?new(value.X,value.Y,value.Z):fallback;
    }
    public static Net7StationGuildState? FromScene(IEnbSceneFrame scene,uint id)
    {
        var obj=scene.Objects.FirstOrDefault(o=>o.GameId==id);
        if(obj is not {ObjectType:<=1,PlayerPresentation:{} data})return null;
        return new(data.GuildName,data.GuildRank,data.GuildRankName,data.SameGuildTagColor,data.OtherGuildTagColor,
            scene.Objects.FirstOrDefault(o=>o.IsLocalShip)?.PlayerPresentation?.GuildName);
    }
}
