namespace EnbNext.Net7.Gameplay;
public enum Net7LandCameraPhase { SpaceApproach, SurfaceArrival }
public sealed record Net7LandCameraTransition(ulong Revision,uint TargetId,uint StartClientTime,Net7LandCameraPhase Phase);
