using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

public sealed record EnbRetailMovieVisual(EnbResolvedW3dResource Source, EnbW3dRenderResource Resource,
    EnbW3dRenderHierarchyAnimation? Animation, float Frame, bool Hidden, bool EngineTrails)
{
    public bool IsShip { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public IDisposable? RenderObjectOwner { get; init; }
    public bool EngineTrailsTouched { get; init; }
    public ImmutableArray<EnbRetailMovieDamage> Damage { get; init; } = [];
}

/// <summary>Immutable publication from the movie owner to the existing GPU scene consumer.</summary>
public sealed record EnbRetailMovieFrame(EnbSceneSnapshot Snapshot, EnbResolvedRenderCamera Camera,
    ImmutableDictionary<ulong, EnbRetailMovieVisual> Visuals, EnbW3dLightEnvironment Lighting, EnbW3dSceneFogState Fog,
    Vector4 Fade, float Letterbox, bool ShowFrame, EnbBaseAssetResolution? Skybox,
    EnbRetailStarLayerSelection? Stars, EnbRetailOrbitalSceneDefinition? Orbitals, float OrbitalHours)
{
    public ImmutableArray<EnbRetailMovieSoundFrame> Sounds { get; init; } = [];
    public Vector3 Ambient { get; init; }
    public IReadOnlyDictionary<EnbRetailOrbitalDefinition, string> OrbitalFactoryFailures { get; init; } = new Dictionary<EnbRetailOrbitalDefinition, string>();
    public IReadOnlyDictionary<EnbRetailOrbitalDefinition, EnbCurrentRetailRObjEffectAsset.FactoryPreparation> OrbitalFactories { get; init; } = new Dictionary<EnbRetailOrbitalDefinition, EnbCurrentRetailRObjEffectAsset.FactoryPreparation>();
    public static EnbRetailMovieFrame Capture(EnbRetailMovieScene scene, EnbRetailMovieAssets resources,
        EnbRetailMovieRuntime runtime, EnbRetailAssetBundle assets, int width, int height)
    {
        var world = scene.Camera;
        world.Translation += resources.CameraShake.Sample(resources.WallClock(), width, height);
        var aspect = Math.Tan(scene.FieldOfView.X * .5) / Math.Tan(scene.FieldOfView.Y * .5);
        if (!EnbResolvedRenderCamera.TryCreate(world.Translation, Quaternion.CreateFromRotationMatrix(world),
            new(width, height, 1), scene.FieldOfView.X, aspect, new(scene.Clip.X, scene.Clip.Y), out var camera))
            throw new InvalidDataException("Movie camera cannot be projected.");
        uint time = runtime.Milliseconds;
        var objects = scene.Models.Select(m => m.Snapshot(time)).ToList();
        var effects = new List<EnbSceneEffectSnapshot>();
        var helpers = new Dictionary<(ulong, int), EnbSceneObjectSnapshot>();
        uint Helper(EnbRetailMovieModel? model, string? bone)
        {
            if (model is null) return 0;
            var key = (model.Identity, model.BoneIndex(bone));
            if (helpers.TryGetValue(key, out var prior)) return prior.GameId;
            if (!model.TryBone(bone ?? "", out var transform)) return 0;
            uint id = resources.GetBoneIdentity(model, bone);
            var snapshot = model.Snapshot(time) with { RenderIdentity = id, GameId = id, BaseAsset = -1, ObjectType = 29,
                Spatial = new(EnbSceneSpatialProviderKind.Fixed, new(time, transform.Translation,
                    Quaternion.CreateFromRotationMatrix(transform), Vector3.Zero, 0, Vector3.Zero, Vector3.UnitX), null),
                WorldCreationOrdinal = id };
            helpers.Add(key, snapshot); objects.Add(snapshot); return id;
        }
        foreach (var effect in resources.Effects)
        {
            uint source = Helper(effect.Source, effect.SourceBone), target = Helper(effect.Target, effect.TargetBone);
            effects.Add(new(unchecked((uint)effect.Id), 1, source, target, effect.Definition, effect.Time,
                effect.Duration, "", 0, effect.Binary, effect.Scale, Vector3.Zero, 0, 0, true, Vector3.Zero, 1,
                effect.Binary ? EnbSceneEffectGeometryKind.ObjectToObject : EnbSceneEffectGeometryKind.Object,
                Vector3.Zero, -1, effect.Hsv, 0, 0,
                EnbPresentationSnapshotBuilder.BuildEffectReference(assets, effect.Definition))
                { EndpointReceipt = new(source == 0 ? null : source, target == 0 ? null : target) });
        }
        uint ambient = PackRgb(scene.Ambient.Current);
        bool Member(EnbRetailMovieModel model)
        {
            while (model.Parent is { } parent) model = parent;
            return model.IsSceneMember && scene.Models.Contains(model);
        }
        var lights = scene.Lights.Where(l => l is not null && (l.Parent is { } p ? Member(p.Model) : l.InScene)).OrderByDescending(l => l!.Sequence).Select(l =>
        {
            var light = l!;
            Vector3 position = default;
            if (light.Parent is { } parent && parent.Model.TryBone(parent.Bone, out var transform)) position = transform.Translation;
            float threshold = (float)BitConverter.Int64BitsToDouble(0x3ee4f8b588e368f1);
            float attenuation = MathF.Abs(light.FarAttenuation.X - light.FarAttenuation.Y) >= threshold ? 1 / light.FarAttenuation.X : 0;
            return new EnbW3dLightSlot(true, new(light.Directional ? EnbW3dLightType.Directional : EnbW3dLightType.Point,
                light.Diffuse * light.Intensity, light.Specular * light.Intensity, light.Ambient * light.Intensity,
                position, light.Direction, light.FarAttenuation.Y, 1, 1, attenuation, 0,
                BitConverter.UInt32BitsToSingle(0x3f490fdb), BitConverter.UInt32BitsToSingle(0x3f490fdb)));
        });
        var snapshot = new EnbSceneSnapshot(time, time, null, null, null, false, null,
            new(null, null, false, null, null, null, null, false, [], 1, new(scene.Clip.X, scene.Clip.Y), []),
            effects.ToImmutableArray(), new(null, null, false, false), new(ambient, []), objects.ToImmutableArray(), [], null, null, [], []);
        bool Hidden(EnbRetailMovieModel model)
        {
            for (var current = model; current is not null; current = current.Parent)
                if (current.Hidden || current.Parent is null && (!current.IsSceneMember || !scene.Models.Contains(current))) return true;
            return false;
        }
        var visuals = scene.Models.ToImmutableDictionary(m => m.Identity,
            m => new EnbRetailMovieVisual(m.Source, m.Resource, m.Animation, m.Frame, Hidden(m), m.EngineTrails)
            { IsShip = m.IsShip, EngineTrailsTouched = m.EngineTrailsTouched, Damage = m.Damage,RenderObjectOwner=m.RenderObjectOwner });
        return new(snapshot, camera, visuals, EnbW3dLightEnvironment.CreateCurrentRetail(ambient, lights),
            new(scene.FogEnabled, PackRgb(scene.Fog.Current), scene.FogRange.X, scene.FogRange.Y),
            runtime.OverlayPublished ? runtime.Overlay.Color : Vector4.Zero,
            runtime.OverlayPublished ? runtime.Overlay.BarFraction : 0, runtime.DebugMode && runtime.ShowCurrentFrame,
            resources.Skybox, resources.Stars, resources.Orbitals, resources.OrbitalHours)
        {
            Ambient = scene.Ambient.Current,
            OrbitalFactoryFailures = ImmutableDictionary.CreateRange<EnbRetailOrbitalDefinition, string>(ReferenceEqualityComparer.Instance, resources.OrbitalFactoryFailures),
            OrbitalFactories = ImmutableDictionary.CreateRange<EnbRetailOrbitalDefinition, EnbCurrentRetailRObjEffectAsset.FactoryPreparation>(ReferenceEqualityComparer.Instance, resources.OrbitalFactories),
            Sounds = resources.Sounds.Select(sound =>
            {
                Vector3? position = sound.Model is { } model && model.TryBone(sound.Bone ?? "", out var pose) ? pose.Translation : null;
                return new EnbRetailMovieSoundFrame(sound.Id, sound.Name, sound.Attached, position, sound.Loop, sound.Volume, sound.Music, sound.Stopped);
            }).ToImmutableArray()
        };
    }

    // 495DE0/495E70 copy floats unchanged; 917C80/965460 use x87 FISTP
    // with truncation. Invalid conversion produces integer-indefinite, not a
    // managed exception. Mask only at the backend RGB24 boundary.
    public static uint PackRgb(Vector3 color)
    {
        return unchecked((uint)(Channel(color.X) << 16 | Channel(color.Y) << 8 | Channel(color.Z))) & 0xffffff;
    }

    private static int Channel(float value)
    {
        double scaled = (double)value * 255;
        return !double.IsFinite(scaled) || scaled >= 2147483648d || scaled < -2147483648d ? int.MinValue : (int)scaled;
    }

    public static uint PackFade(Vector4 color) => color.W > 0
        ? unchecked((uint)(Channel(color.W) << 24 | Channel(color.X) << 16 | Channel(color.Y) << 8 | Channel(color.Z))) : 0;
}
public sealed record EnbRetailMovieSoundFrame(long Id, string Name, bool Attached, Vector3? Position, bool Loop, float Volume, bool Music, bool Stopped);
