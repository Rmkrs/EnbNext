using System.Numerics;
using System.Collections.Immutable;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

public interface IEnbRetailMovieChildFactory
{
    void UpdateChildren(EnbRetailMovieModel model,bool engineTrailsChanged);
}

/// <summary>Movie mesh wrapper and its direct tag-zero animation request (804290/805000/8051B0).</summary>
public sealed class EnbRetailMovieModel
{
    public int Id { get; }
    public ulong Identity { get; }
    public EnbResolvedW3dResource Source { get; }
    public EnbW3dRenderResource Resource { get; private set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public IDisposable? RenderObjectOwner { get; internal set; }
    public EnbW3dRenderHierarchyAnimation? Animation { get; private set; }
    public float Frame { get; private set; }
    public bool Frozen { get; private set; } = true;
    public bool Hidden { get; internal set; }
    public bool EngineTrails { get; internal set; }
    public bool EngineTrailsTouched { get; internal set; }
    public bool IsShip { get; init; }
    public bool IsSceneMember { get; private set; } = true;
    public void SetEngineTrails(bool enabled)
    {
        EngineTrails=enabled;EngineTrailsTouched=true;
        (RenderObjectOwner as IEnbRetailMovieChildFactory)?.UpdateChildren(this,true);
    }
    public ImmutableArray<EnbRetailMovieDamage> Damage { get; private set; } = [default, default, default, default];
    public void SetDamage(int[] quadrants)
    {
        var damage = Damage.ToBuilder();
        for (int i = 0; i < 4; i++)
            if (quadrants[i] != 0) damage[i] = new(quadrants[i], damage[i].Generation + 1);
        Damage = damage.ToImmutable();
        (RenderObjectOwner as IEnbRetailMovieChildFactory)?.UpdateChildren(this,false);
    }
    public ImmutableArray<EnbSceneColorizationSnapshot> Colors { get; internal set; } = [];
    public ImmutableArray<EnbSceneDecalSnapshot> Decals { get; internal set; } = [];
    public EnbSceneNameDecalSnapshot? NameDecal { get; internal set; }
    public int ColorApplyCount { get; internal set; }
    public int NameApplyCount { get; internal set; }
    public int ColorCountAtName { get; internal set; }
    public int DecalApplyCount { get; internal set; }
    public EnbRetailMovieModel? Parent { get; private set; }
    public string? ParentBone { get; private set; }
    public Matrix4x4 LocalTransform { get; private set; } = Matrix4x4.Identity;
    private bool _cycle;
    private float _start, _end;

    public EnbRetailMovieModel(int id, ulong identity, EnbResolvedW3dResource source, EnbW3dRenderResource resource)
    { Id = id; Identity = identity; Source = source; Resource = resource; }

    public void BindAnimation(EnbW3dRenderHierarchyAnimation? animation, EnbW3dRenderResource resource,
        bool cycle, float start, float end, bool frozen)
    {
        Animation = animation; Resource = resource; Frozen = frozen; _cycle = cycle;
        if (animation is null) return;
        float last = animation.NumFrames - 1f;
        _start = start < 0 ? 0 : start > last ? last : start;
        _end = end == -1 ? last : end < 0 ? 0 : end > last ? last : end;
        if (_start > _end) (_start, _end) = (_end, _start);
        Frame = _start;
    }

    public void Update(uint deltaMilliseconds)
    {
        if (Animation is null || Frozen) return;
        float next = Frame + Animation.PlaybackFrameRate * (deltaMilliseconds * .001f);
        if (next > _end)
            next = _cycle ? (next - _start) % (_end - _start) + _start : _end;
        Frame = next;
    }

    public void Detach()
    {
        // 942160 removes membership; retain the last world transform.
        if (TryWorld(out var world)) LocalTransform = world;
        Parent = null; ParentBone = null;
        IsSceneMember = false;
    }

    public bool Attach(EnbRetailMovieModel parent, string bone)
    {
        // Contain graph cycles without inserting a fabricated replacement pose.
        for (var ancestor = parent; ancestor is not null; ancestor = ancestor.Parent)
            if (ReferenceEquals(ancestor, this)) return false;
        if (!parent.TryBone(bone, out _)) return false;
        Parent = parent; ParentBone = bone;
        return true;
    }

    public bool TryWorld(out Matrix4x4 world)
    {
        if (Parent is null) { world = LocalTransform; return true; }
        return Parent.TryBone(ParentBone!, out world);
    }

    public bool TryBone(string name, out Matrix4x4 world)
    {
        world = default;
        if (!TryWorld(out var root)) return false;
        string selected = Source.CurrentRetailAggregate?.BaseRequestedObjectName ?? Source.RequestedObjectName;
        string? hierarchyName = Resource.HierarchyModels.FirstOrDefault(h => h.Name.Equals(selected, StringComparison.OrdinalIgnoreCase))?.HierarchyName
            ?? Resource.HierarchicalLods.FirstOrDefault(h => h.Name.Equals(selected, StringComparison.OrdinalIgnoreCase))?.HierarchyName;
        if (hierarchyName is null) { world = root; return true; }
        var hierarchy = Resource.Hierarchies.FirstOrDefault(h => h.Name.Equals(hierarchyName, StringComparison.OrdinalIgnoreCase));
        if (hierarchy is null) return false;
        int index = EnbCurrentRetailAggregateBoneLookup.Resolve(hierarchy, name);
        if (!EnbW3dHierarchyPose.TryEvaluate(hierarchy, root, Animation, Frame, out var pose, out _)
            || index >= pose.WorldTransforms.Length) return false;
        world = pose.WorldTransforms[index]; return true;
    }

    public int BoneIndex(string? name)
    {
        string selected = Source.CurrentRetailAggregate?.BaseRequestedObjectName ?? Source.RequestedObjectName;
        string? hierarchyName = Resource.HierarchyModels.FirstOrDefault(h => h.Name.Equals(selected, StringComparison.OrdinalIgnoreCase))?.HierarchyName
            ?? Resource.HierarchicalLods.FirstOrDefault(h => h.Name.Equals(selected, StringComparison.OrdinalIgnoreCase))?.HierarchyName;
        var hierarchy = Resource.Hierarchies.FirstOrDefault(h => h.Name.Equals(hierarchyName, StringComparison.OrdinalIgnoreCase));
        return hierarchy is null || name is null ? 0 : EnbCurrentRetailAggregateBoneLookup.Resolve(hierarchy, name);
    }

    public EnbSceneObjectSnapshot Snapshot(uint time)
    {
        TryWorld(out var world);
        return new(Identity, 1, unchecked((uint)Identity), false, false, 0, unchecked(int.MinValue + (int)Identity), 1, 0, 0, 0, 0, false,
            Source.RequestedObjectName, new(false,0,null),
            new(EnbSceneSpatialProviderKind.Fixed,new(time,world.Translation,Quaternion.CreateFromRotationMatrix(world),Vector3.Zero,0,Vector3.Zero,Vector3.UnitX),null),
            null,null,Decals,Colors,[],[],[],[],NameDecal,[],null,0,0)
        {
            WorldCreationOrdinal = Identity,
            ColorizationAudit = new(ColorApplyCount, 0, 0, 0, [], []),
            NameDecalApplyCount = NameApplyCount, NameDecalColorizationApplyCount = ColorCountAtName,
            DecalApplyCount = DecalApplyCount
        };
    }
}
public readonly record struct EnbRetailMovieDamage(int Level, uint Generation);
