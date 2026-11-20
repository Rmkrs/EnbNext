using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>Client 006FBEF0 / 006FC880 sound ownership, driven only by reported global warp transitions.</summary>
public sealed class EnbWarpSoundRuntime
{
    private readonly Dictionary<ulong, Owner> _owners = [];
    public void Clear() => _owners.Clear();

    public IReadOnlyList<EnbCurrentRetailEffectSoundNode> Update(IReadOnlyList<IEnbRenderObject> objects)
    {
        var retained = new HashSet<ulong>();
        var result = new List<EnbCurrentRetailEffectSoundNode>();
        foreach (var obj in objects)
        {
            if (obj.ObjectType != 1 || obj.ShipPresentation is not { } ship || !retained.Add(obj.RenderIdentity)) continue;
            if (!_owners.TryGetValue(obj.RenderIdentity, out var owner))
            {
                owner = new() { Sequence = ship.WarpChanges.Length > 1 ? ship.WarpChanges[^2].Sequence : 0 };
                _owners.Add(obj.RenderIdentity, owner);
            }
            foreach (var change in ship.WarpChanges)
            {
                if (change.Sequence <= owner.Sequence) continue;
                owner.Sequence = change.Sequence;
                string? cue = change.State switch { 1 => "start", 2 => "enter", 3 or 7 or 8 or 9 or 11 => "exit", _ => null };
                if (cue is not null)
                {
                    owner.Cues.Add(Node(obj, change.Sequence, 0, $"warp_{cue}_{Math.Clamp(change.PowerLevel, 1, 6)}", obj.IsLocalShip));
                    // Keep finite requests through asynchronous PCM loading and natural
                    // completion. The player tombstones completed voices; never replay.
                    if (owner.Cues.Count > 32) owner.Cues.RemoveAt(0);
                }
                if (change.State == 1)
                {
                    owner.Primary = Node(obj, change.Sequence, 1, $"engine_loop_{unchecked(change.EngineTrailType + 22)}", false)
                        with { Active = false, Doppler = true, RadiusMultiplier = obj.IsLocalShip ? 2.5f : 1f };
                    owner.Secondary = Node(obj, change.Sequence, 2, "Warp_Secondary", false) with { Active = false };
                }
                else if (change.State == 2)
                {
                    // Retail plays handles created by state 1. Seeing a ship already
                    // in state 2 does not manufacture a missed charge callback.
                    if (owner.Primary is not null) owner.Primary = owner.Primary with { Active = true };
                    if (owner.Secondary is not null) owner.Secondary = owner.Secondary with { Active = true };
                }
                else if (change.State is 3 or 7 or 8 or 9 or 11)
                    owner.Primary = owner.Secondary = null;
            }
            result.AddRange(owner.Cues);
            if (owner.Primary is not null) result.Add(owner.Primary);
            if (owner.Secondary is not null) result.Add(owner.Secondary);
        }
        foreach (var key in _owners.Keys.Where(k => !retained.Contains(k)).ToArray()) _owners.Remove(key);
        return result;
    }

    public static EnbCurrentRetailEffectSoundNode Node(IEnbRenderObject obj, ulong sequence, ulong slot, string name, bool flat)
    {
        // This adapter shares the proven sound player, but has a separate key
        // namespace. These are client callbacks, never network effect packets.
        var root = new EnbSceneEffectSnapshot(obj.GameId, obj.RenderIdentity, obj.GameId, 0, 0, 0, 0, "", 0,
            false, 1, Vector3.Zero, 0, 0, false, Vector3.Zero, 1, EnbSceneEffectGeometryKind.Object,
            Vector3.Zero, 0, Vector3.Zero, 0, 0, null);
        var definition = new EnbEffectDefinition(0, "SoundEffect", EnbEffectClassKind.SoundEffect,
            null, null, null, null, false, null, null, null, null, false, false, false, true,
            false, true, false, false, false, false, name,
            new Dictionary<string, string> { ["SFX2D"] = flat ? "TRUE" : "FALSE" });
        return new(sequence * 3 + slot, definition, root, obj.GameId, EnbCurrentRetailEffectLinkKind.Root, true, 0)
            { ClientWarp = true };
    }

    private sealed class Owner
    {
        public ulong Sequence;
        public readonly List<EnbCurrentRetailEffectSoundNode> Cues = [];
        public EnbCurrentRetailEffectSoundNode? Primary, Secondary;
    }
}
