using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>Current client 007B7EF0/007B8210/007B8510/007B8880. Preview only.</summary>
public static class EnbNavigationRoutePlanner
{
    public readonly record struct Node(uint Id, Vector3 Position, float Range);

    public static ImmutableArray<uint> BuildPreview(IEnbSceneFrame scene,uint destinationId)
    {
        // 007BD6E0 dispatches planet selection to007B7C80, which stores
        // the origin AND destination. Ordinary-space solving omits the origin.
        // This is a map owner, not the vector emitted for a warp command.
        if(scene.Environment?.PresentationMode==1)
        {
            var from=scene.Objects.FirstOrDefault(o=>o.IsLocalShip);
            var destination=scene.Objects.FirstOrDefault(o=>o.GameId==destinationId);
            return from?.Spatial is not null && destination?.Spatial is not null
                ? [from.GameId,destinationId] : [];
        }
        return Build(scene,destinationId);
    }

    public static ImmutableArray<uint> Build(IEnbSceneFrame scene, uint destinationId)
    {
        var from = scene.Objects.FirstOrDefault(o => o.IsLocalShip);
        var dest = scene.Objects.FirstOrDefault(o => o.GameId == destinationId);
        if (from?.Spatial is null || dest?.Spatial is null || from.GameId == destinationId) return [];
        if (dest.Radar.Navigation is null) return [destinationId];
        float scan = scene.Hud.ScanRange.GetValueOrDefault(); // 007BCA70: missing ScanRange is zero.
        var nodes = new List<Node>
        {
            new(from.GameId, from.Spatial.Transform.Position, scan),
            new(dest.GameId, dest.Spatial.Transform.Position,
                dest.Radar.Navigation.Signature + EnbCurrentRetailRadarRenderRangeRuntime.ResolveWorldRadius(dest))
        };
        foreach (var obj in scene.Objects)
            if (obj.GameId != destinationId && obj.GameId != from.GameId && obj.Spatial is { } spatial &&
                obj.Radar.Navigation is { NavType: 1 } nav)
                nodes.Add(new(obj.GameId, spatial.Transform.Position, nav.Signature));
        return Build(nodes, EnbCurrentRetailRadarRenderRangeRuntime.ResolveWorldRadius(from), scan);
    }

    public static ImmutableArray<uint> Build(IReadOnlyList<Node> nodes, float shipRadius, float initialRange)
    {
        // Reject unusable presentation inputs; this does not validate network messages.
        if (nodes.Count < 2 || !float.IsFinite(shipRadius) || !float.IsFinite(initialRange) ||
            nodes.Any(n => !EnbNavigationMapProjection.Finite(n.Position) || !float.IsFinite(n.Range))) return [];
        float range = initialRange;
        while (float.IsFinite(range))
        {
            var parents = Enumerable.Repeat(-1, nodes.Count).ToArray();
            var costs = new float[nodes.Count];
            List<int> frontier = [0];
            while (frontier.Count != 0)
            {
                List<int> next = [];
                foreach (int from in frontier)
                {
                    if (from == 1) continue; // Destination has no outgoing links; origin has no incoming links.
                    for (int to = 1; to < nodes.Count; to++)
                    {
                        if (from == to) continue;
                        Vector3 delta = nodes[from].Position - nodes[to].Position;
                        float squared = delta.Y * delta.Y + delta.X * delta.X + delta.Z * delta.Z;
                        float reach = nodes[to].Range + shipRadius + range;
                        if (!(squared < reach * reach)) continue; // 00832010, strict and directed.
                        float cost = costs[from] + MathF.Sqrt(squared);
                        if (parents[to] == -1)
                        {
                            parents[to] = from; costs[to] = cost; next.Add(to);
                        }
                        else if (cost < costs[to]) { parents[to] = from; costs[to] = cost; }
                    }
                }
                // 007B8880 queues on first discovery, not on later cost improvements.
                frontier = next;
            }
            if (parents[1] != -1)
            {
                var path = new List<uint>();
                for (int node = 1; node != 0; node = parents[node]) path.Add(nodes[node].Id);
                path.Reverse(); return path.ToImmutableArray();
            }
            range = range <= 0 ? 1 : range + range;
        }
        return [];
    }
}
