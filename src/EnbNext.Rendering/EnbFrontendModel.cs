using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Rendering;

public sealed class EnbFrontendModel
{
    public EnbW3dRenderResource Resource { get; }
    public EnbW3dRenderHierarchy Hierarchy { get; }
    public IReadOnlyList<(string Name, EnbDazzleDefinition Definition)> Dazzles { get; }
    private readonly EnbRetailAssetBundle _assets;
    private readonly Dictionary<string, EnbW3dRenderHierarchyAnimation> _animations = new(StringComparer.OrdinalIgnoreCase);
    public EnbFrontendModel(EnbRetailAssetBundle assets, string name)
    {
        _assets = assets;
        Resource = Read(assets, name);
        string hierarchy = Resource.HierarchicalLods.FirstOrDefault()?.HierarchyName ?? Resource.Hierarchies.First().Name;
        Hierarchy = Resource.Hierarchies.FirstOrDefault(h => h.Name.Equals(hierarchy, StringComparison.OrdinalIgnoreCase))
            ?? Read(assets, hierarchy).Hierarchies.Single();
        if (name.Equals("fe_room", StringComparison.OrdinalIgnoreCase) &&
            assets.RetailMixResources!.TryReadResource("fe_room.w3d", out var bytes, out _))
        {
            var ini = EnbRetailIniDocument.Load(System.IO.Path.Combine(assets.Paths.ClientIniDirectory, "Dazzle.ini"));
            Dazzles = EnbW3dDazzle.Decode(bytes).Select(d => (d.Name, EnbDazzleDefinition.Resolve(ini, d.Type))).ToArray();
        }
        else Dazzles = [];
    }
    public static EnbW3dRenderResource Read(EnbRetailAssetBundle assets, string name)
    {
        string file = name.EndsWith(".w3d", StringComparison.OrdinalIgnoreCase) ? name : name + ".w3d";
        if (assets.RetailMixResources is not {} resources || !resources.TryReadResource(file, out var bytes, out var location) ||
            !EnbW3dModel.TryDecode(bytes, out var model) ||
            !EnbAuthoredUiAsset.TryCompile(resources, location, model, System.IO.Path.GetFileNameWithoutExtension(file), out var compiled))
            throw new InvalidDataException("Frontend resource unavailable: " + file);
        return compiled;
    }
    public EnbW3dRenderHierarchyAnimation Animation(string name)
    {
        if (!_animations.TryGetValue(name, out var clip))
        {
            var animations=Read(_assets,name).Animations;
            if(animations.Count!=1)
                throw new InvalidDataException($"Frontend animation {name} contains {animations.Count} HAnim records; expected one.");
            _animations[name]=clip=animations[0];
        }
        return clip;
    }
    public bool TryAnimation(string name,out EnbW3dRenderHierarchyAnimation? clip)
    {
        try { clip=Animation(name);return true; }
        catch(Exception error) when(error is InvalidDataException or IOException)
        { clip=null;return false; }
    }
    public EnbW3dHierarchyPoseOwner Pose(string? animation, double seconds)
    {
        var clip = animation is null ? null : Animation(animation);
        float frame = clip is null ? 0 : (float)Math.Clamp(seconds * clip.FrameRate, 0, clip.NumFrames - 1);
        return PoseFrame(clip,frame);
    }
    public EnbW3dHierarchyPoseOwner PoseFrame(EnbW3dRenderHierarchyAnimation? clip,float frame)
    {
        if (!EnbW3dHierarchyPose.TryEvaluate(Hierarchy, Matrix4x4.Identity, clip, frame, out var pose, out var error))
            throw new InvalidDataException(error);
        return pose;
    }
    public int Pivot(string name) => Hierarchy.Pivots.First(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Ordinal;
    public bool HasPivot(string name) => Hierarchy.Pivots.Any(p => p.Name.Equals(name,StringComparison.OrdinalIgnoreCase));
}
