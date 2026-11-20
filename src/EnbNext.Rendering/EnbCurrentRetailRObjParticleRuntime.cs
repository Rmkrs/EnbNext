using System.Numerics;
using EnbNext.Assets;

namespace EnbNext.Rendering;

public sealed record EnbCurrentRetailRObjParticleAsset(EnbW3dRenderResourceKey Key,
    EnbW3dRenderTextureLocation? Texture, EnbCurrentRetailParticleEmitterDefinition Definition, int PivotIndex = 0)
{
    public string? RuntimeTextureName { get; init; }
    public EnbResolvedTextureResource? RetainedTexture { get; init; }

    public EnbCurrentRetailRObjParticleAsset PrepareTexture(EnbRetailAssetBundle assets)
    {
        //0097D910 requests the common TextureClass for every nonempty name;
        //00949550/009493F0 retain009AF0F0's image on failed resource lookup.
        if (Definition.TextureFileName.Length == 0) return this with { Texture = null, RuntimeTextureName = null, RetainedTexture=null };
        var resolved = assets.Textures.ResolveCurrentRetailMissingTexture(Definition.TextureFileName, assets.Paths.ClientExecutablePath);
        if (resolved.Image is null) throw new InvalidDataException($"Particle texture unavailable: {Definition.TextureFileName}: {resolved.Diagnostic}");
        return resolved.ResourceLocation is { } location
            ? this with { Texture = EnbW3dRenderTextureLocation.From(location), RuntimeTextureName = null, RetainedTexture=resolved }
            : this with { Texture = null, RuntimeTextureName = resolved.LogicalName, RetainedTexture=resolved };
    }

    public EnbCurrentRetailRObjParticleAsset ApplyHsv(EnbRetailAssetBundle assets,Vector3 shift)
    {
        //0048D850 obtains the retained ParticleBuffer texture through00963600,
        //then looks up/constructs its variant without the named API's threshold.
        var texture=RetainedTexture is {} retained
            ? EnbCurrentRetailEffectMeshHsvRuntime.ResolveRetainedTexture(assets.Textures,retained,shift) : null;
        return this with {RetainedTexture=texture,RuntimeTextureName=texture?.LogicalName??RuntimeTextureName,
            Definition=Definition.Copy().SetTextureFileName(texture?.LogicalName??Definition.TextureFileName).SetColor(EnbCurrentRetailHsvRuntime.ShiftParticleColor(Definition.Color,shift))};
    }

    public uint BirthMilliseconds => EnbRetailParticleEmissionArithmetic.Interval(Definition.EmissionRatePerSecond);
    public long EndMilliseconds => Definition.MaximumEmissions == 0 ? long.MaxValue : Math.Max(0,
        BirthMilliseconds * (((long)Definition.MaximumEmissions + Definition.RuntimeBurstSize - 1) / Definition.RuntimeBurstSize) +
        EnbRetailParticleEmissionArithmetic.LifetimeMilliseconds(Definition.LifetimeSeconds) - EnbRetailParticleEmissionArithmetic.InitialMilliseconds(Definition.InitialEmissionTimeSeconds));

    public static void ValidateAnimated(EnbCurrentRetailParticleEmitterDefinition d)
        => EnbRetailParticleAdmission.Validate(d);

    public static void ValidateFinite(EnbCurrentRetailParticleEmitterDefinition d)
    {
        EnbRetailParticleAdmission.Validate(d);
        if (d.MaximumEmissions <= 0) throw new InvalidDataException("Finite particle owner requires an emission allowance");
    }
    public static void Validate(EnbCurrentRetailParticleEmitterDefinition d)
    {
        if (d.MaximumEmissions == 0)
        {
            EnbCurrentRetailShipParticleRuntime.Validate(d,allowInheritedMotion:true);
            return;
        }
        // The captured single-birth optimization remains narrow. CreateParticle
        // routes other finite definitions through ValidateFinite and replay ownership.
        if (!EnbCurrentRetailParticleEmitterRuntime.SupportsEmissionSpace(d) || d.LocalSpace || d.RenderMode != 1 || d.FrameMode != 0 || d.MaximumEmissions != 1 || d.RuntimeBurstSize != 1 ||
            d.CreationRandomizer.ClassId is not (0 or 1) || d.CreationRandomizer.Values != Vector3.Zero ||
            d.VelocityRandomizer.ClassId != 0 || d.VelocityRandomizer.Values != Vector3.Zero ||
            d.VelocityInheritanceFactor != 0 || d.AccelerationPerSecondSquared != Vector3.Zero ||
            d.Gravity != 0 || d.Elasticity != 0 || d.OutwardVelocityPerSecond != 0 ||
            d.Rotation.Random != 0 || d.Rotation.Keys.Any(k => k.Value != 0) || d.InitialOrientationRandom != 0 ||
            !float.IsFinite(d.EmissionRatePerSecond) || d.EmissionRatePerSecond is < 1 or > 1000 ||
            !float.IsFinite(d.LifetimeSeconds) || d.LifetimeSeconds is <= 0 or > 10)
            throw new InvalidDataException("RObjEffect particle definition is outside the single-emission quad family");
    }
}

/// <summary>Finite moving emitters retain sampled rigid poses and immutable birth
/// transforms. Emitter removal stops new births, not the independent buffer tail.</summary>
public sealed class EnbCurrentRetailParticlePoseHistory
{
    private readonly List<(uint Age, Vector3 Position, Quaternion Rotation)> _poses = [];
    private readonly Dictionary<long, Matrix4x4> _births = [];
    private readonly Dictionary<long, Vector3> _birthVelocities = [];
    public uint LastAge => _poses.Count == 0 ? 0 : _poses[^1].Age;
    public IReadOnlyList<(uint Age, Vector3 Position, Quaternion Rotation)> Samples => _poses;

    public Matrix4x4 LatestWorld(float scale)
    {
        if (_poses.Count == 0) throw new InvalidDataException("Particle buffer has no emitter pose");
        var p = _poses[^1];
        return Matrix4x4.CreateScale(scale)*Matrix4x4.CreateFromQuaternion(p.Rotation)*Matrix4x4.CreateTranslation(p.Position);
    }

    // Animated emitters consume each pose into independent particle births.
    // Keep the last sample for interpolation with the next source update.
    // Call only after every emitter sharing this history has captured the
    // current frame's births. Cached birth transforms/velocities remain valid.
    public void RetainLatestSample()
    {
        if (_poses.Count > 1) _poses.RemoveRange(0, _poses.Count - 1);
    }

    public void Record(uint age, Matrix4x4 world)
    {
        var pose = (age, world.Translation, Quaternion.CreateFromRotationMatrix(world));
        if (_poses.Count > 0 && age == LastAge) { _poses[^1] = pose; return; }
        if (_poses.Count >= 4096 || (_poses.Count > 0 && age < LastAge))
            throw new InvalidDataException("Finite emitter pose history exceeded its bound or moved backwards");
        _poses.Add(pose);
    }

    public Matrix4x4 Resolve(long birth, float scale)
    {
        if (_births.TryGetValue(birth, out var cached)) return cached;
        for (int i=1; i<_poses.Count; i++)
        {
            var a=_poses[i-1]; var b=_poses[i];
            if (birth<a.Age || birth>b.Age) continue;
            float t=(birth-a.Age)/(float)(b.Age-a.Age);
            var world=Matrix4x4.CreateScale(scale)*EnbCurrentRetailParticleBirthTransform.Create(a.Rotation,b.Rotation,t)*
                Matrix4x4.CreateTranslation(Vector3.Lerp(a.Position,b.Position,t));
            _births.Add(birth,world); return world;
        }
        throw new InvalidDataException("Particle birth has no bracketing emitter poses");
    }

    public Vector3 ResolveInheritedVelocity(long birth, float inheritance)
    {
        if (!float.IsFinite(inheritance)) throw new InvalidDataException("Particle inheritance must be finite");
        if (inheritance == 0) return Vector3.Zero;
        if (_birthVelocities.TryGetValue(birth, out var velocity)) return velocity * inheritance;
        for (int i = 1; i < _poses.Count; i++)
        {
            var a = _poses[i - 1]; var b = _poses[i];
            // 0097E530 emits only while interval < remainder. A birth exactly
            // at an update boundary belongs to the following emission frame.
            if (birth < a.Age || birth >= b.Age) continue;
            velocity = (b.Position - a.Position) / (b.Age - a.Age);
            _birthVelocities.Add(birth, velocity);
            return velocity * inheritance;
        }
        throw new InvalidDataException("Particle inherited velocity has no emitting-frame pose interval");
    }
}

/// <summary>0097E530 interpolates emitter position/orientation at birth. The
/// separate ParticleBuffer then owns that world-space particle. Keeping birth
/// pose avoids dragging a flash around with the source on subsequent frames.</summary>
public sealed class EnbCurrentRetailParticleBirthRuntime
{
    private readonly List<(uint Age, Vector3 Position, Quaternion Orientation)> _poses = [];
    public Matrix4x4? BirthWorld { get; private set; }

    public void Record(uint age, Matrix4x4 rigidWorld)
    {
        if (BirthWorld is not null) return;
        var pose = (age, rigidWorld.Translation, Quaternion.CreateFromRotationMatrix(rigidWorld));
        if (_poses.Count > 0 && age == _poses[^1].Age) { _poses[^1] = pose; return; }
        if (_poses.Count >= 1024 || (_poses.Count > 0 && age < _poses[^1].Age))
            throw new InvalidDataException("Particle birth pose history exceeded its bound or moved backwards");
        _poses.Add(pose);
    }

    public bool Capture(uint nowAge, uint birthAge, float scale)
    {
        if (BirthWorld is not null) return true;
        if (nowAge <= birthAge) return false; // Native interval < remainder.
        for (int i = 1; i < _poses.Count; i++)
        {
            var before = _poses[i - 1]; var after = _poses[i];
            if (before.Age > birthAge || after.Age < birthAge) continue;
            float fraction = (birthAge - before.Age) / (float)(after.Age - before.Age);
            BirthWorld = Matrix4x4.CreateScale(scale) * EnbCurrentRetailParticleBirthTransform.Create(before.Orientation, after.Orientation, fraction) *
                         Matrix4x4.CreateTranslation(Vector3.Lerp(before.Position, after.Position, fraction));
            _poses.Clear(); return true;
        }
        throw new InvalidDataException("Particle birth has no bracketing emitter poses; refusing to invent a birth transform");
    }
}
