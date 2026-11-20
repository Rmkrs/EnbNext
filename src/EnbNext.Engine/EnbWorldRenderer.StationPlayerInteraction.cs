using System.Numerics;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    internal sealed record StationPlayerPick(uint Id,string FirstName,bool Busy,bool InRange,EnbRetailMeshPick[] Meshes)
    {public bool Departing {get;init;} public Vector3 Origin {get;init;}}
    private StationPlayerPick[] _stationPlayerPicks=[];
    internal StationPlayerPick? StationPlayer(uint id)=>Volatile.Read(ref _stationPlayerPicks).FirstOrDefault(p=>p.Id==id);
    internal StationPlayerPick? PickStationPlayer(double x,double y)
        =>PickStationInteraction(x,y) is {InRange:true} hit?hit.Player:null;

    private void PublishStationPlayerPicks(EnbResolvedRenderCamera camera)
    {
        // 63CA50 binds LogoWidget to the avatar render object.66C4C0/496960
        // use9671B0's render-object intersection, not its name rectangle.
        var picks=new List<StationPlayerPick>();
        foreach(var actor in _stationPopulation.InteractionOrdered)
        {
            var pose=actor.Pose;
            if(!_stationPeople.TryGetValue((false,pose.AvatarId),out var person))continue;
            bool busy=(pose.ActionFlag&0x3e)!=0 || actor.Motion.Remaining!=0;
            bool inRange=_stationMovement is {} movement && Vector3.Distance(movement.Position,person.World.Translation)<12;
            picks.Add(new(pose.AvatarId,person.Identity.FirstName,busy,inRange,person.TeleportHidden || person.Failure is not null?[]:
                StationPersonPicks(person,camera)){Departing=actor.Departing,Origin=person.World.Translation});
        }
        Volatile.Write(ref _stationPlayerPicks,picks.ToArray());
    }
}
