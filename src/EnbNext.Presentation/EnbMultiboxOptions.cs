namespace EnbNext.Presentation;

public sealed record EnbMultiboxOptions(bool Enabled=false,bool CombinePresence=false,bool InviteAfterLaunch=false,bool FollowLeaderTarget=false,bool FollowLeaderFire=false,int ShortcutButtons=3,bool ShowDamageMeter=false,int EncounterPauseSeconds=10,
    bool FollowLeaderDock=false,bool FollowLeaderLaunch=false,int AutoFormation=0,
    bool FollowLeaderStation=false,bool SharedStationSpawn=false,int StationFollowDistance=15,bool EnableDamageMeter=false,bool ShowCharacterPanel=true,
    bool FollowLeaderWormhole=false,bool SharedHarvest=false,bool SharedReactor=false)
{
    /// <summary>ShowDamageMeter is the cockpit toggle's state; EnableDamageMeter (Interface options) shows that toggle.</summary>
    public bool DamageMeterVisible=>EnableDamageMeter&&ShowDamageMeter;
    /// <summary>Leader formation created after launching from a station: 0 none, 1 Pipe, 2 Slot, 3 Block.</summary>
    public static string FormationName(int value)=>value switch {1=>"Pipe",2=>"Slot",3=>"Block",_=>"Off"};
}
