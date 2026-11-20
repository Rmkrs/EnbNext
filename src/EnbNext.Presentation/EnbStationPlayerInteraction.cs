namespace EnbNext.Presentation;

public enum EnbStationPlayerAction { Cancel=1, Chat=2, Trade=3, GroupInvite=4 }

public sealed record EnbStationPlayerInteraction(uint Character,uint StarbaseId,ulong HandoffVersion,
    uint Room,uint AvatarId,string FirstName,EnbStationPlayerAction Action);
