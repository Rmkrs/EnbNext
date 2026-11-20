using System.Numerics;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private EnbStationInteractionRequest? _stationConversationOwner;
    private Matrix4x4? _stationConversationCamera;
    private (Vector3 Position, Quaternion Orientation)? _stationConversationFrom;
    private long _stationConversationStarted;
    private ulong _stationConversationGeneration;
    private object? _stationConversationPreviousTree;
    public bool StationConversationCameraMoving => _stationConversationFrom is not null;
    public bool StationConversationCameraReady(EnbStationInteractionRequest owner)
        => _stationConversationOwner == owner && _stationConversationFrom is null;
    public object? StationConversationPreviousTree {get;set;}
    public EnbDialogueSpeechPage? StationDialogueSpeechPage {get;set;}

    private EnbStationTerminalOwner? _stationTerminalOwner;
    private Matrix4x4? _stationTerminalCamera;
    private (Vector3 Position, Quaternion Orientation)? _stationTerminalFrom;
    private long _stationTerminalStarted;
    public bool StationTerminalCameraMoving => _stationTerminalFrom is not null;
    public bool StationTerminalCameraReady(EnbStationTerminalOwner owner)
        => _stationTerminalOwner == owner && _stationTerminalFrom is null;

    /// <summary>Next Modern UI compact terminal: the avatar turns to the terminal, the camera stays where it is.</summary>
    public bool StationTerminalCompact {get;set;}
    public void SetStationTerminal(EnbStationTerminalOwner? owner)
    {
        if(owner==_stationTerminalOwner)return;
        bool compact=StationTerminalCompact || (owner is null && _stationTerminalCamera is null);
        _stationTerminalOwner=owner;
        _stationTerminalFrom=compact?null:_stationLastCameraPose;
        _stationTerminalStarted=_stationElapsedMilliseconds;
        _stationTerminalCamera=null;
        if(owner is null || _stationInterior is not {} room || room.RoomType!=owner.Room)return;
        // 0063B76B selects extra-hangar CAM_WTERMnn; 0063B80F selects CAM_TERMINALnn.
        // one second, authored orientation, no height adjustment.
        bool wall=room.RoomType==0 && room.HangarAttachments is not null;
        string camera=wall?$"CAM_WTERM{owner.Slot:00}":$"CAM_TERMINAL{owner.Slot:00}";
        var hierarchy=wall?room.HangarAttachments!:room.Resource.Hierarchies.Single();
        //Both camera paths (hangar +B8, 0069E6E0) ask the model for the named bone
        //transform; an unknown name resolves bone 0. 0063DCD0 turns the avatar
        //regardless of the camera.
        if(!EnbCurrentRetailAggregateBoneLookup.TryResolve(hierarchy,camera,out int cameraPivot))return;
        camera=hierarchy.Pivots[cameraPivot].Name;
        _stationTerminalCamera=compact?null:wall?room.HangarAttachmentTransform(camera):room.PivotTransform(camera);
        if(_stationMovement is {} player)
        {
            var terminal=wall?room.HangarAttachmentTransform($"WALLTERM{owner.Slot:00}"):room.PivotTransform($"TERMINAL{owner.Slot:00}");
            var direction=terminal.Translation-player.Position;
            player.Stop();
            player.Relocate(Matrix4x4.CreateRotationZ(MathF.Atan2(direction.Y,direction.X))*Matrix4x4.CreateTranslation(player.Position));
        }
    }

    public void SetStationConversation(EnbStationInteractionRequest? owner)
    {
        if(owner==_stationConversationOwner)return;
        QueueStationBooth(_stationConversationOwner,false);
        QueueStationBooth(owner,true);
        _stationConversationOwner=owner;StationDialogueSpeechPage=null;
        _stationConversationGeneration++;
        // Capture the page present when the request began, not a response that
        // may already have arrived before this UI frame observes the owner.
        _stationConversationPreviousTree=StationConversationPreviousTree;
        _stationConversationFrom=_stationLastCameraPose;
        _stationConversationStarted=_stationElapsedMilliseconds;
        _stationConversationCamera=null;
        if(owner is null || _stationInterior is not {} room || room.RoomType!=owner.Room ||
            _stationMovement is not {} player || _stationAvatarAsset is not {} avatar)return;
        // 634140's role-9 booth has neither ordinary nor variant avatar.
        // 63D2F0 therefore performs neither actor facing nor conversation camera.
        if(_stationScene?.StationDefinition?.RoomNpcs(owner.Room).Any(n=>n.Slot==owner.Slot && n.Role==7 && n.Variant==0)==true)
        {player.Stop();return;}
        var npc=_stationPeople.TryGetValue((true,owner.Slot),out var actor) && actor.Ambient is not null
            ? actor.World.Translation : room.PivotTransform($"AVATAR{owner.Slot:00}").Translation;
        var direction=npc-player.Position;direction.Z=0;
        if(direction.LengthSquared()<.0001f)return;
        direction=Vector3.Normalize(direction);
        player.Stop();
        player.Relocate(Matrix4x4.CreateRotationZ(MathF.Atan2(direction.Y,direction.X))*Matrix4x4.CreateTranslation(player.Position));
        // 0063D2F0 faces both actors. 00787450 positions the camera 3.5
        // behind and to the side of the player's skeleton reference, +8.5 Z,
        // looking at the actors' midpoint +7 Z (00B04750/4C, 00AFE150).
        var origin=(avatar.SkeletonReference*player.Transform).Translation;
        var side=Vector3.Cross(Vector3.UnitZ,direction);
        var eye=origin-direction*3.5f-side*3.5f+Vector3.UnitZ*8.5f;
        var target=(origin+npc)*.5f+Vector3.UnitZ*7;
        var forward=Vector3.Normalize(target-eye);
        var left=Vector3.Normalize(Vector3.Cross(Vector3.UnitZ,forward));
        var up=Vector3.Cross(forward,left);
        _stationConversationCamera=new(forward.X,forward.Y,forward.Z,0,left.X,left.Y,left.Z,0,
            up.X,up.Y,up.Z,0,eye.X,eye.Y,eye.Z,1);
    }

    private Matrix4x4 StationConversationNpcTransform(uint slot,Matrix4x4 authored,StationPerson person)
    {
        if(_stationConversationOwner?.Slot==slot && _stationMovement is {} player &&
            person.ConversationGeneration!=_stationConversationGeneration)
        {
            // 0063D2F0 writes the actor once; 00638DA0 does not restore its room pose.
            var direction=player.Position-authored.Translation;
            if(direction.LengthSquared()!=0)direction=Vector3.Normalize(direction);
            var transform=player.Transform;
            var up=new Vector3(transform.M31,transform.M32,transform.M33);
            var left=Vector3.Cross(up,direction);
            person.InteractionTransform=new(direction.X,direction.Y,direction.Z,0,
                left.X,left.Y,left.Z,0,up.X,up.Y,up.Z,0,
                authored.M41,authored.M42,authored.M43,1);
            person.ConversationGeneration=_stationConversationGeneration;
        }
        return person.InteractionTransform??authored;
    }
}
