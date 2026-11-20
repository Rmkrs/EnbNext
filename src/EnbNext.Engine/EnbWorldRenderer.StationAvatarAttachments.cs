using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private bool _stationShadowAttempted;
    private EnbW3dRenderResource? _stationShadow;
    private string? _stationShadowDiagnostic;
    private IEnumerable<(string Key,Matrix4x4 World)> StationAvatarShadows()
    {
        // 00785F20 attaches the installed model to skel_reference. Station
        // construction enables it for ordinary NPCs (00634140), ambient NPCs
        // (00642740), remote players (0063CA50), and the local actor (0063BDB0).
        // 00636D50 removes it when leaving the room; it is not a world light shadow.
        foreach(var entry in _stationPeople)
            if(entry.Value.Failure is null && !entry.Value.TeleportHidden && entry.Value.LastPose is {} pose)
                yield return ($"avatar-shadow/{entry.Key.Npc}/{entry.Key.Id}",pose.SkeletonReference*entry.Value.World);
        if(_stationDepartureStarted is null && StationArrivalComplete &&
            _stationLastPose is {} local && _stationMovement is {} movement)
            yield return ("avatar-shadow/local",local.SkeletonReference*movement.Transform);
    }
    private void PrepareStationShadow()
    {
        if(_stationShadowAttempted)return;
        _stationShadowAttempted=true;
        try{_stationShadow=StationFixtureAsset("avatar_shadow");}
        catch(Exception ex) when(ex is InvalidDataException or IOException or InvalidOperationException or ArgumentException or NotSupportedException)
        {_stationShadowDiagnostic=ex.Message;}
    }
}
