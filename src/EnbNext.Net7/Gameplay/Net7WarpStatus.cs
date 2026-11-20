using EnbNext.Net7.World;

namespace EnbNext.Net7.Gameplay;

/// <summary>Retained retail cockpit policy (005821C0). Deadlines are already
/// corrected into the client clock by the Aux decoder.</summary>
public sealed record Net7WarpStatus(int? State, int? Availability, long? TriggerTime,
    bool Incapacitated, bool SpeedLocked, float? MaximumSpeed, int? PresentationMode)
{
    public Net7CockpitWarpAuxState? AuxCallbacks { get; init; }
    public bool IsEngaged => State is 1 or 2;
    public bool CanRequestRescue => Incapacitated && PresentationMode == 0;
    public bool CanTakeoff => !Incapacitated && PresentationMode == 1;
    public bool IsTimed => State is 1 or 3 or 6 or 7 or 8 or 9 or 11;
    public bool CanActivate => !Incapacitated && PresentationMode == 0 &&
        (IsEngaged || State == 0 && Availability == 2 && !SpeedLocked && MaximumSpeed > 0);
    public string TooltipKey => CanRequestRescue ? "RESCUE" : Incapacitated ? "CANNOT_WARP" : PresentationMode == 1 ? "ON_PLANET"
        : PresentationMode == 5 ? "GAS_GIANT" : State switch
        {
            1 or 2 => "WARP_DONE", 3 or 6 or 7 or 8 or 9 or 11 => "WARP_WAIT",
            4 => "WARP_LOCKED", 5 => "WARP_ENGINE_NOT_INSTALLED", 10 => "WARP_LOW",
            0 when Availability == 1 => "NO_WARP", 0 when CanActivate => "WARP",
            _ => "CANNOT_WARP"
        };
    public double RemainingSeconds(long now) => IsTimed && TriggerTime is long end
        ? Math.Max(0, (Math.Clamp(end, int.MinValue, int.MaxValue) - now) / 1000d) : 0;

    public static Net7WarpStatus Read(Net7AuxDataSchema? data, int? mode)
    {
        object? Value(int id)
        {
            if (data is not { Name: "ShipAuxData" } || data.Properties.Count <= id) return null;
            var property = data.Resolve(id);
            // State tracks callbacks, not value lifetime. State-two without a
            // value uses the retail constructor default; state-zero retains a
            // previously decoded value (same contract as the other ship HUD readers).
            if (property.Value is not null || property.State == 0) return property.Value;
            return property.Kind switch
            {
                Net7AuxDataValueKind.Int32LittleEndian => (object)0,
                Net7AuxDataValueKind.Float32LittleEndian => 0f,
                Net7AuxDataValueKind.BooleanByte => (byte)0,
                Net7AuxDataValueKind.Time32LittleEndianRelative => new Net7AuxDataTime(0),
                _ => null
            };
        }
        return new(Value(38) as int?, Value(40) as int?,
            Value(41) is Net7AuxDataTime time ? time.RelativeValue : null,
            Value(28) is byte incap && incap != 0, Value(23) is byte locked && locked != 0,
            Value(20) as float?, mode)
        {
            AuxCallbacks = data is { Name: "ShipAuxData" } ? new(
                data.Resolve(20).UpdateRevision, data.Resolve(20).State,
                data.Resolve(23).UpdateRevision, data.Resolve(23).State,
                data.Resolve(38).UpdateRevision, data.Resolve(38).State,
                data.Resolve(40).UpdateRevision, data.Resolve(40).State, data.Resolve(41).State) : null
        };
    }
}

public sealed record Net7CockpitWarpAuxState(
    ulong MaximumSpeedRevision, int MaximumSpeedState,
    ulong LockSpeedRevision, int LockSpeedState,
    ulong PrivateWarpRevision, int PrivateWarpState,
    ulong AvailabilityRevision, int AvailabilityState, int TriggerTimeState = 1);
