using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Presentation;
using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>The same animated, influenced TSI26A geometry supplies GPU art and hit bounds.</summary>
public sealed class EnbAuthoredMapFrame
{
    public EnbAuthoredUiAsset Asset { get; }
    public EnbW3dRenderHierarchyAnimation Animation { get; }
    private float _frame = float.NaN;
    private bool _markerButton;
    /// <summary>Next option: a MARKER_BTTN copy of the authored LABEL_BTTN, one button pitch to its right.</summary>
    public const string MarkerButtonName = "MARKER_BTTN";
    public const float ButtonPitch = 15;
    public bool MarkerButton { get => _markerButton; set { if (_markerButton == value) return; _markerButton = value; _frame = float.NaN; } }
    public ImmutableArray<EnbW3dDrawTask> Packets { get; private set; } = [];
    public IReadOnlyDictionary<string, EnbUiRect> Bounds => _bounds;
    private readonly Dictionary<string, EnbUiRect> _bounds = new(StringComparer.OrdinalIgnoreCase);
    public EnbW3dHierarchyPoseOwner Pose { get; private set; } = null!;

    public EnbAuthoredMapFrame(EnbRetailAssetBundle assets)
    {
        Asset = EnbAuthoredUiAsset.Load(assets, "tsi26a.w3d") ?? throw new InvalidDataException("Missing TSI26A map frame.");
        if (!assets.RetailMixResources!.TryReadResource("tsi26a-1.w3d", out var bytes, out _) ||
            !EnbW3dModel.TryDecode(bytes, out var model) ||
            model.Animations.FirstOrDefault(a => a.HierarchyName.Equals(Asset.Hierarchy.Name, StringComparison.OrdinalIgnoreCase)) is not { } animation ||
            !EnbW3dRenderResourceCompiler.TryCompileAnimation(0, animation, out var compiled, out _))
            throw new InvalidDataException("Missing TSI26A resize animation.");
        Animation = compiled;
    }

    public void Evaluate(float frame)
    {
        if (_frame == frame) return;
        if (!EnbW3dHierarchyPose.TryEvaluate(Asset.Resource.Hierarchies.Single(), Matrix4x4.Identity,
            Animation, frame, out var pose, out var error)) throw new InvalidDataException(error);
        var packets = ImmutableArray.CreateBuilder<EnbW3dDrawTask>();
        _bounds.Clear();
        int Pivot(EnbW3dRenderMesh mesh) => mesh.BoneIndices.Count != 0 ? 0
            : Asset.Hierarchy.Pivots.First(p => p.Name == mesh.Name).Ordinal;
        foreach (var mesh in Asset.Resource.Meshes.OrderByDescending(m => m.StaticSort.RuntimeLevel)
            .ThenBy(Pivot))
        {
            int pivot = Pivot(mesh);
            var transform = pose.WorldTransforms[pivot];
            EnbW3dDeformedGeometry? geometry = null;
            if (mesh.BoneIndices.Count != 0)
            {
                if (!EnbW3dSkinDeformer.TryDeform(mesh, pose, out geometry, out error)) throw new InvalidDataException(error);
                transform = Matrix4x4.Identity;
            }
            var positions = geometry is null ? mesh.Positions.Select(p => Vector3.Transform(p, transform)).ToArray() : geometry.Positions.ToArray();
            var min = positions.Aggregate(Vector3.Min); var max = positions.Aggregate(Vector3.Max);
            _bounds[mesh.Name] = new(min.X + 320, 240 - max.Y, max.X - min.X, max.Y - min.Y);
            foreach (var pass in mesh.MaterialPasses)
                packets.Add(new(new(EnbRenderPassKind.ModernOverlayBoundary, EnbW3dDrawBlendClass.Blended,
                    mesh.StaticSort, Asset.Resource.Key, mesh.Ordinal, pass.Ordinal, 1, 0), 1, 1, 0,
                    transform, new(transform.Translation, mesh.BoundingRadius), mesh, geometry,
                    new(pass, pass.SingleBindingSemantics), EnbW3dLightEnvironment.BlackNoLights, 0,
                    Asset.Hierarchy.Name, pivot, mesh.Name));
            if (_markerButton && geometry is null && mesh.Name == "LABEL_BTTN")
            {
                var moved = transform * Matrix4x4.CreateTranslation(ButtonPitch, 0, 0);
                _bounds[MarkerButtonName] = new(min.X + ButtonPitch + 320, 240 - max.Y, max.X - min.X, max.Y - min.Y);
                foreach (var pass in mesh.MaterialPasses)
                    packets.Add(new(new(EnbRenderPassKind.ModernOverlayBoundary, EnbW3dDrawBlendClass.Blended,
                        mesh.StaticSort, Asset.Resource.Key, mesh.Ordinal, pass.Ordinal, 1, 0), 1, 1, 0,
                        moved, new(moved.Translation, mesh.BoundingRadius), mesh, null,
                        new(pass, pass.SingleBindingSemantics), EnbW3dLightEnvironment.BlackNoLights, 0,
                        Asset.Hierarchy.Name, pivot, MarkerButtonName));
            }
        }
        Pose = pose; Packets = packets.ToImmutable(); _frame = frame;
    }

    public EnbUiPoint Anchor(string name)
    {
        var v = Pose.WorldTransforms[Asset.Hierarchy.Pivots.First(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Ordinal].Translation;
        return new(v.X + 320, 240 - v.Y);
    }

    public EnbUiRect ContentBounds => new(Anchor("TL"), Anchor("BR"));
    public EnbUiRect BackgroundBounds => new(Anchor("BACK_TL"), Anchor("BACK_BR"));

    public bool Contains(string meshName, EnbUiPoint designPoint)
    {
        if (!_bounds.TryGetValue(meshName, out var bounds) || !bounds.Contains(designPoint)) return false;
        var packet = Packets.First(p => p.RenderObjectName.Equals(meshName, StringComparison.OrdinalIgnoreCase));
        var positions = packet.DeformedGeometry?.Positions ?? packet.Mesh.Positions;
        Vector2 At(uint i)
        {
            var v = Vector3.Transform(positions[(int)i],packet.WorldTransform);
            return new(v.X+320,240-v.Y);
        }
        static float Cross(Vector2 a, Vector2 b) => a.X*b.Y-a.Y*b.X;
        var point = new Vector2((float)designPoint.X,(float)designPoint.Y);
        for (int i=0;i+2<packet.Mesh.Indices.Count;i+=3)
        {
            var a=At(packet.Mesh.Indices[i]); var b=At(packet.Mesh.Indices[i+1]); var c=At(packet.Mesh.Indices[i+2]);
            float area=Cross(b-a,c-a);
            if (Math.Abs(area)<1e-7f) continue;
            float x=Cross(b-a,point-a), y=Cross(c-b,point-b), z=Cross(a-c,point-c);
            if (x>=0 && y>=0 && z>=0 || x<=0 && y<=0 && z<=0) return true;
        }
        return false;
    }
}
