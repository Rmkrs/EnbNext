using System.Numerics;
using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>00635ADE..00635D93: sequential room_Nmd models with room_Nan looping HAnim.</summary>
public sealed record EnbStationMachinery(string Name, EnbW3dRenderResource Resource,
    EnbW3dRenderHierarchyAnimation? Animation)
{
    public string? AnimationDiagnostic {get;init;}
    private readonly EnbCurrentRetailHierarchyAnimationState _playback=new()
        {Mode=2,Playback=1,HasAnimation=true,HasHierarchy=true};

    //635D64 binds once.6366A0/636D50 attach and detach the existing
    //object without resetting its HAnim clock, including while offscreen.
    public void StartPlayback(uint clock)
    {
        _playback.CurrentFrame=0;_playback.PreviousFrame=0;
        _playback.PreviousTime=clock;
    }

    /// <summary>Sample the shared scene clock, not time since room entry.</summary>
    public EnbW3dAnimationSample? Sample(long milliseconds)
    {
        if(Animation is null)return null;
        uint clock=unchecked((uint)milliseconds);
        _playback.RefreshSingle(()=>clock,()=>Animation.FrameRate,()=>checked((int)Animation.NumFrames),
            _=>{},()=>0,()=>0,(_,_,_)=>0);
        return new(Animation.Name,_playback.CurrentFrame);
    }

    public static IReadOnlyList<EnbStationMachinery> Load(EnbRetailAssetBundle assets, string room)
    {
        var resources = assets.RetailMixResources!;
        var result = new List<EnbStationMachinery>();
        for (int i = 1; ; i++)
        {
            string name = $"{room}_{i}md";
            if (!resources.TryReadResource(name + ".w3d", out var bytes, out var location)) break;
            if (!EnbW3dModel.TryDecode(bytes, out var model)) throw new InvalidDataException("Invalid machinery " + name);
            var textures = model.Meshes.SelectMany(m => m.TextureNames).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(n => new EnbResolvedTextureReference(n, resources.TryLocateResource(n, out var t) ? t : null)).ToArray();
            if (!EnbW3dRenderResourceCompiler.TryCompile(new(location.Name, name, location, model, textures), out var resource, out var error))
                throw new InvalidDataException(error);
            var machine=BindAnimation(assets,name,resource,$"{name}.{room}_{i}an");
            machine.StartPlayback(unchecked((uint)Environment.TickCount));
            result.Add(machine);
        }
        return result;
    }

    public static EnbStationMachinery BindAnimation(EnbRetailAssetBundle assets,string name,EnbW3dRenderResource resource,string animationName)
    {
        // 635D64 passes the acquired pointer even when null. 9C5980
        // selects the static hierarchy mode on null; the model stays attached.
        string? diagnostic;
        try
        {
            if(EnbCurrentRetailRslHAnimResolver.TryResolve(resource,new("",EnbRslFileDependencyStatus.NotAuthored),
                assets.Paths,assets.MixResources,assets.RetailMixResources!,animationName,out var resolved,out diagnostic))
                return new(name,resolved.DrawResource,resolved.Animation);
        }
        catch(Exception ex) when(ex is InvalidDataException or IOException or InvalidOperationException or ArgumentException)
        {diagnostic=ex.Message;}
        return new(name,resource,null){AnimationDiagnostic=diagnostic};
    }
}

/// <summary>A sound anchor evaluated from the same resource/sample as the GPU draw.</summary>
public sealed record EnbStationSoundObject(string Key, EnbW3dRenderResource Resource, EnbW3dAnimationSample? Sample)
{
    public string Name => Resource.HierarchicalLods.Single().Name;

    public bool TryPosition(string bone, bool onlyIfUsed, out Vector3 position)
    {
        position = default;
        var hierarchy = Resource.Hierarchies.Single();
        var pivot = hierarchy.Pivots.FirstOrDefault(p => p.Name.Equals(bone, StringComparison.OrdinalIgnoreCase));
        // Current retail 004348C0/00434FD0 ignores the failed/root-zero lookup.
        if (pivot is null || pivot.Ordinal == 0) return false;
        if (onlyIfUsed && !Resource.HierarchicalLods.Single().Levels.Single().Members.Any(m => m.ReferenceIndex == pivot.Ordinal)) return false;
        var animation = Sample is {} sample ? Resource.Animations.Single(a => a.Name.Equals(sample.Name, StringComparison.OrdinalIgnoreCase)) : null;
        if (!EnbW3dHierarchyPose.TryEvaluate(hierarchy, Matrix4x4.Identity, animation, Sample?.Frame ?? 0, out var pose, out var error))
            throw new InvalidDataException(error);
        position = pose.WorldTransforms[pivot.Ordinal].Translation;
        return true;
    }

    public static EnbStationAmbientRoom Bind(EnbStationAmbientRoom room, IReadOnlyList<EnbStationSoundObject> objects)
    {
        var entries = room.Entries.ToList();
        foreach (var obj in objects)
            foreach (var sound in room.Tracked)
                if (obj.Name == sound.Mesh && obj.TryPosition(sound.Bone, sound.OnlyIfBoneUsed, out _))
                    entries.Add(new(sound.Sound, sound.OnEntry, sound.MinDelayMilliseconds, sound.MaxDelayMilliseconds, .5f, .5f, true)
                    { Binding = new(obj.Key, sound.Bone, sound.OnEntry) });
        return room with { Entries = entries };
    }
}
