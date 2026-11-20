namespace EnbNext.Presentation;

public enum EnbFriendsAction { Open, Close, Add, Remove, AddTarget, AddGroup, Restricted, Unrestricted }
public sealed record EnbFriendsRequest(uint Character, ulong ShipOwner, EnbFriendsAction Action, string? Identity = null);
