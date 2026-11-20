namespace EnbNext.Net7.Gameplay;
public sealed record Net7DockCameraTransition(ulong Revision, uint TargetId, uint StartClientTime, bool IsDeparture = false);
