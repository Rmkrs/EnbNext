namespace EnbNext.Net7.Gameplay;

/// <summary>Presentation controller event (79B2B0), distinct from wire selector 0092.</summary>
public sealed record Net7CameraEvent(ulong Sequence, int Event, uint TargetId);
