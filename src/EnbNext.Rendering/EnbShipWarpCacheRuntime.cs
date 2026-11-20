using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>006F3C60 frame ordering and 006FBEF0's SpaceObject+F8 cache.</summary>
public sealed class EnbShipWarpCacheRuntime
{
    private sealed class State
    {
        public int Value; public ulong Private, Global;
        public EnbSceneShipPresentationSnapshot? Source, Applied;
        public int Before;
        public System.Collections.Immutable.ImmutableArray<EnbSceneWarpPresentationChange> Callbacks = [];
    }
    private readonly Dictionary<ulong, State> states = [];
    private Guid stream;

    public EnbSceneFrame ApplyFrame(EnbSceneFrame scene)
    {
        if (stream != scene.ClientSoundStreamId) { states.Clear(); stream = scene.ClientSoundStreamId; }
        var seen = new HashSet<ulong>();
        var objects = scene.Objects;
        var poses = scene.RenderPoses;
        for (int i = 0; i < objects.Count; i++)
        {
            var obj = scene.MutableObject(i);
            if (obj.ObjectType is not (0 or 1) || obj.ShipPresentation is not {} ship) continue;
            seen.Add(obj.RenderIdentity);
            if (!states.TryGetValue(obj.RenderIdentity, out var state)) states.Add(obj.RenderIdentity, state = new());
            int before = state.Value;
            // SClientRunFrame drains packets before the object tick. Coalesced
            // updates run private then global, regardless of packet arrival order.
            if (ship.PrivateWarpRevision != state.Private && ship.PrivateWarpMaterialized) state.Value = ship.PrivateWarpState ?? 0;
            if (ship.GlobalWarpRevision != state.Global && ship.GlobalWarpMaterialized && !ship.GlobalWarpUpdatesSuppressed)
            {
                state.Value = ship.GlobalWarpState ?? 0;
                // All 6FBEF0 side effects share the same dirty-property gate,
                // not just the F8 cache. Preserve completed frame callbacks
                // for detached render/audio consumers, never intermediate
                // packet values or callbacks suppressed by 6FC950.
                state.Callbacks = state.Callbacks.Add(new(ship.GlobalWarpRevision, state.Value, ship.WarpPowerLevel, ship.EngineTrailType ?? 0));
            }
            state.Private = ship.PrivateWarpRevision; state.Global = ship.GlobalWarpRevision;
            // EngineThrustEffects runs before the cache write; subsequent effects
            // (including WarpTunnel) see the updated value during the same tick.
            poses.WriteWarp(obj.RenderPoseSlot,before,state.Value,state.Callbacks);
            if(!ReferenceEquals(state.Source,ship) || state.Before!=before || state.Applied?.CachedWarpState!=state.Value || state.Applied.WarpChanges!=state.Callbacks)
            {state.Source=ship;state.Before=before;state.Applied=ship with {EngineWarpState=before,CachedWarpState=state.Value,WarpChanges=state.Callbacks};}
            obj.ShipPresentation=state.Applied;
        }
        foreach (ulong key in states.Keys.Where(k => !seen.Contains(k)).ToArray()) states.Remove(key);
        return scene;
    }
}
