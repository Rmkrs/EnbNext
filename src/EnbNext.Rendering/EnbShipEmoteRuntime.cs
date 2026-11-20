using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Net7.Gameplay;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>007E6650/007E6860/007E70A0: FIFO gestures over the current physical pose.
/// No animation transform is written back to gameplay or sent as movement.</summary>
public sealed class EnbShipEmoteRuntime
{
    private sealed class State
    {
        public readonly Queue<Net7ShipEmoteEvent> Pending = new();
        public Net7ShipEmoteEvent? Active;
        public uint Started;
        public bool Enabled = true;
    }
    private readonly Dictionary<ulong, State> _states = [];
    private long _sequence;
    private ulong? _owner;

    public void Clear() { _states.Clear(); _owner = null; }
    // 7E7700 unwraps the running history provider without clearing member34 FIFO.
    public void Interrupt(ulong owner)
    {
        if (_states.TryGetValue(owner, out var state)) { state.Active = null; state.Enabled = false; }
    }
    public void Resume(ulong owner)
    {
        if (_states.TryGetValue(owner, out var state)) state.Enabled = true;
    }
    public bool HasPending(ulong owner) => _states.TryGetValue(owner, out var state) &&
        state.Active is null && state.Pending.Count > 0;
    public bool IsRunning(ulong owner) => _states.TryGetValue(owner, out var state) && state.Active is not null;
    public static uint Duration(uint emote) => emote switch
    { 0 or 3 or 4 => 2000, 1 or 2 => 3000, 5 or 6 => 1000, _ => 0 };
    public static string? Sound(uint emote) => emote switch
    { 0 => "wave.wav", 1 or 2 => "loop.wav", 3 or 4 => "roll.wav", 5 or 6 => "spin.wav", _ => null };

    public bool Observe(IEnbSceneFrame scene)
    {
        var owner = scene.Objects.FirstOrDefault(o => o.IsLocalShip)?.RenderIdentity;
        if (_owner != owner) { _states.Clear(); _owner = owner; _sequence = 0; }
        if (scene.IsTransitioning || scene.ActiveStarbaseId is not null || scene.StationAvatars.Length > 0)
        {
            Clear();
            if (scene.ShipEmotes.Length > 0) _sequence = Math.Max(_sequence, scene.ShipEmotes[^1].Sequence);
            return false;
        }
        foreach (var item in scene.ShipEmotes)
        {
            if (item.Sequence <= _sequence) continue;
            _sequence = item.Sequence;
            var obj = scene.Objects.FirstOrDefault(o => o.GameId == item.GameId && o.WorldCreationOrdinal == item.CreationOrdinal);
            if (obj?.Spatial is null || Duration(item.Emote) == 0) continue;
            if (!_states.TryGetValue(obj.RenderIdentity, out var state)) _states[obj.RenderIdentity] = state = new();
            state.Pending.Enqueue(item);
        }
        return true;
    }

    public EnbSceneFrame Apply(EnbSceneFrame scene, uint now, bool localPlaybackAllowed = true)
    {
        if (!Observe(scene)) return scene;
        var objects = scene.Objects;
        var renderPoses = scene.RenderPoses;
        var present = new HashSet<ulong>();
        for (int i = 0; i < objects.Count; i++)
        {
            var obj = scene.MutableObject(i);
            present.Add(obj.RenderIdentity);
            if (obj.Spatial is not { } spatial || !_states.TryGetValue(obj.RenderIdentity, out var state)) continue;
            if (!state.Enabled || obj.IsLocalShip && !localPlaybackAllowed) continue;
            // Retail completes at t > 1 and starts the next item on the following update.
            if (state.Active is { } active && unchecked(now - state.Started) > Duration(active.Emote))
            { state.Active = null; continue; }
            if (state.Active is null)
            {
                if (!state.Pending.TryDequeue(out var next)) continue;
                state.Active = next; state.Started = now;
            }
            var current = state.Active;
            uint elapsed = unchecked(now - state.Started);
            obj.Pose=SamplePose(obj.Pose,current.Emote,
                elapsed/(float)Duration(current.Emote),EnbCurrentRetailRadarRenderRangeRuntime.ResolveWorldRadius(obj));
            obj.ShipEmote=new(current.Sequence,current.Emote,elapsed/1000.0);
            renderPoses.Write(obj);
        }
        foreach (var id in _states.Keys.Where(id => !present.Contains(id)).ToArray()) _states.Remove(id);
        return scene;
    }

    public static EnbRenderPose SamplePose(EnbRenderPose physical,uint emote,float t,float radius)
    {
        var sample=physical.Sample;
        Matrix4x4 provider=sample.ProviderTransform??EnbRetailCameraQuaternionBasis.Create(sample.Orientation);
        if(sample.ProviderTransform is null)provider.Translation=sample.Position;
        Matrix4x4 animated=EnbRetailShipEmoteTransform.Sample(provider,emote,t,radius);
        return physical with {Sample=sample with {Position=animated.Translation,
            Orientation=Quaternion.CreateFromRotationMatrix(animated),
            OrientationColumn0=new(animated.M11,animated.M12,animated.M13),ProviderTransform=animated}};
    }
}

/// <summary>007932A0: preserve the preceding camera position, move it out to
/// 4 * 75 units if necessary, and track the rendered ship with world Z up.</summary>
public sealed class EnbShipEmoteCamera
{
    private (ulong, long)? _active;
    private Vector3 _position;
    public bool Active => _active is not null;
    public void Reset() => _active = null;
    public EnbCameraPoseOverride? Update(IEnbRenderObject ship, Vector3 previousCamera,
        Vector3 renderedCenter, bool normalCamera)
    {
        if (!normalCamera || ship.ShipEmote is not { } emote || ship.Spatial is null)
        { Reset(); return null; }
        var key = (ship.RenderIdentity, emote.Sequence);
        if (_active != key)
        {
            _active = key;
            _position = EnbRetailPointCameraOrientation.DistancePosition(previousCamera,ship.Spatial.Transform.Position);
        }
        return EnbCameraPoseOverride.Point(_position,renderedCenter,false,true);
    }
}
