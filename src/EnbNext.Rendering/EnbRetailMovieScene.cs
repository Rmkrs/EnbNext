using System.Collections.Immutable;
using System.Numerics;
using System.Text;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>Resource and device ownership used by the scripted scene, separate from its command cursor.</summary>
public interface IEnbRetailMovieResources : IDisposable
{
    EnbRetailMovieModel? CreateModel(int id, string name, bool ship);
    void DestroyModel(EnbRetailMovieModel model);
    void Animate(EnbRetailMovieModel model, string name, bool cycle, float start, float end, bool frozen);
    bool Environment(string command, EnbRetailMovieArguments arguments);
    bool Effect(string command, EnbRetailMovieArguments arguments, IReadOnlyList<EnbRetailMovieModel> models, bool preload, uint time);
    bool Audio(string command, EnbRetailMovieArguments arguments, IReadOnlyList<EnbRetailMovieModel> models);
    void Soundtrack(string name);
    void ExpireEffect(int id);
    void Damage(EnbRetailMovieModel model, int[] quadrants);
    void Shake(float first, float second);
    void Update(uint milliseconds, Matrix4x4 camera);
}

/// <summary>803310 and its mesh/light/appearance handlers. Lists preserve first-match identity semantics.</summary>
public sealed class EnbRetailMovieScene : IEnbRetailMovieScene
{
    private readonly IEnbRetailMovieResources _resources;
    private readonly List<EnbRetailMovieModel> _models = [];
    private readonly List<(EnbRetailMovieModel Model, string Bone, float H, float V)> _cameras = [];
    private int _cameraIndex;
    private long _lightSequence;
    private (EnbRetailMovieModel Model, string Bone)? _cameraParent;
    private Vector2 _mouseCache = new(-1);
    private bool _disposed;
    public IReadOnlyList<EnbRetailMovieModel> Models => _models;
    public EnbRetailMovieLight?[] Lights { get; } = new EnbRetailMovieLight?[4];
    public EnbRetailMovieColorTransition Ambient { get; } = new();
    public EnbRetailMovieColorTransition Fog { get; } = new();
    public bool FogEnabled { get; private set; }
    public Vector2 FogRange { get; private set; }
    public Vector2 Clip { get; private set; } = new(20, 100000);
    public Vector2 FieldOfView { get; private set; } = new(BitConverter.UInt32BitsToSingle(0x3f7a35dd), BitConverter.UInt32BitsToSingle(0x3f373094));
    public bool MaxCamera { get; private set; } = true;
    public bool MouseMovement { get; private set; }
    public float MouseAngle { get; private set; } = 1;
    public Vector2 Mouse { get; set; }
    public Matrix4x4 Camera { get; private set; } = Matrix4x4.Identity;
    private static float Degrees => BitConverter.UInt32BitsToSingle(0x3c8efa35);

    public EnbRetailMovieScene(IEnbRetailMovieResources resources) => _resources = resources;

    public bool Execute(string command, EnbRetailMovieArguments a, bool preload, uint milliseconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (command.StartsWith("CREATE_SFX", StringComparison.Ordinal))
            return _resources.Effect(command, a, _models, preload, milliseconds);
        if (command is "CREATE_SKYBOX" or "CREATE_STARS" or "CREATE_ORBITAL") return _resources.Environment(command, a);
        if (command is "PLAY_AUDIO" or "STOP_AUDIO") return _resources.Audio(command, a, _models);
        switch (command)
        {
            case "CREATE_OBJECT": case "CREATE_SHIP_OBJECT":
                if (!Int(a, out int id) || a.Read() is not { } name) return false;
                if (_resources.CreateModel(id, name, command == "CREATE_SHIP_OBJECT") is not { } created) return false;
                _models.Add(created); return true;
            case "DESTROY_OBJECT":
                if (!Model(a, out var destroyed)) return false;
                destroyed.Detach(); _resources.DestroyModel(destroyed); _models.Remove(destroyed); return true;
            case "PLAY_ANIMATION": case "LOAD_ANIMATION":
                if (!Model(a, out var animated) || a.Read() is not { } animation) return false;
                bool cycle = command == "PLAY_ANIMATION" && OptionalInt(a, 0) != 0;
                float start = command == "PLAY_ANIMATION" ? OptionalInt(a, 0) : 0;
                float end = command == "PLAY_ANIMATION" ? OptionalInt(a, -1) : -1;
                _resources.Animate(animated, animation, cycle, start, end, command == "LOAD_ANIMATION"); return true;
            case "ATTACH_TO_BONE":
                if (!Int(a, out int childId) || !Int(a, out int parentId)) return false;
                EnbRetailMovieModel? child = null, parent = null;
                foreach (var model in _models)
                    if (model.Id == parentId) parent ??= model; else if (model.Id == childId) child ??= model;
                if (child is null || parent is null) return false;
                child.Detach();
                if (a.Read() is not { } bone) return false;
                child.Attach(parent, bone); return true;
            case "ATTACH_CAMERA":
                if (!Model(a, out var cameraModel) || a.Read() is not { } cameraBone) return false;
                _cameraParent = (cameraModel, cameraBone); return true;
            case "ADD_CAMERA":
                if (!Model(a, out var cameraOwner) || a.Read() is not { } storedBone) return false;
                float h = 0, v = 0;
                if (a.Read() is { } horizontal)
                {
                    if (!Number(a, out v)) return false;
                    h = EnbRetailMovieArguments.Number(horizontal) * Degrees; v *= Degrees;
                }
                _cameras.Add((cameraOwner, storedBone, h, v)); return true;
            case "CREATE_POINT_LIGHT": case "CREATE_DIRECTIONAL_LIGHT": case "CREATE_DIRECTIONAL_LIGHT_FROM_BONE":
                return CreateLight(command, a);
            case "ATTACH_LIGHT_TO_BONE":
                if (!Light(a, out var attached) || !Model(a, out var lightModel)) return false;
                attached.Parent = null; attached.InScene = false;
                if (a.Read() is not { } lightBone) return false;
                attached.Parent = (lightModel, lightBone); attached.Sequence = ++_lightSequence; return true;
            case "SET_LIGHT_INTENSITY":
                if (!Light(a, out var intensity) || !Number(a, out float power)) return false;
                intensity.Intensity = power; return true;
            case "SET_LIGHT_FAR_ATTENUATION": case "SET_LIGHT_ATTENUATION":
                if (!Light(a, out var attenuation) || !Number(a, out float near)) return false;
                attenuation.FarAttenuation = new(near, OptionalNumber(a, near)); return true;
            case "SET_LIGHT_AMBIENT_COLOR": case "SET_LIGHT_DIFFUSE_COLOR": case "SET_LIGHT_SPECULAR_COLOR":
                if (!Light(a, out var light) || !Vector(a, out var color)) return false;
                if (command == "SET_LIGHT_AMBIENT_COLOR") light.Ambient = color;
                else if (command == "SET_LIGHT_DIFFUSE_COLOR") light.Diffuse = color;
                else light.Specular = color;
                return true;
            case "SET_AMBIENT_LIGHT":
                if (!Vector(a, out var ambient)) return false;
                Ambient.Set(ambient, OptionalNumber(a, 0), true); return true;
            case "TURN_ON_FOG":
                if (!Vector(a, out var fog) || !Number(a, out float fogNear) || !Number(a, out float fogFar)) return false;
                FogEnabled = true; FogRange = new(fogNear, fogFar);
                Fog.Set(fog, OptionalNumber(a, 0), false); return true;
            case "TURN_OFF_FOG": FogEnabled = false; return true;
            case "SET_CAMERA_VIEW_PLANE":
                if (!Number(a, out float hfov) || !Number(a, out float vfov)) return false;
                FieldOfView = new(hfov * Degrees, vfov * Degrees); return true;
            case "SET_CLIP_PLANE":
                if (!Number(a, out float clipNear) || !Number(a, out float clipFar)) return false;
                Clip = new(clipNear, clipFar); return true;
            case "SHAKE_CAMERA":
                if (!Number(a, out float shakeA) || !Number(a, out float shakeB)) return false;
                _resources.Shake(shakeA, shakeB); return true;
            case "TURN_ON_MAX_CAMERA": MaxCamera = true; return true;
            case "TURN_OFF_MAX_CAMERA": MaxCamera = false; return true;
            case "TURN_ON_MOUSE_MOVEMENT_OF_CAMERA":
                MouseMovement = true;
                if (a.Read() is { } angle) MouseAngle = EnbRetailMovieArguments.Number(angle) * Degrees;
                return true;
            case "TURN_OFF_MOUSE_MOVEMENT_OF_CAMERA": MouseMovement = false; return true;
            case "TURN_ON_ENGINE_TRAILS": case "TURN_OFF_ENGINE_TRAILS":
                if (!Model(a, out var trails)) return false;
                trails.SetEngineTrails(command == "TURN_ON_ENGINE_TRAILS"); return true;
            case "HIDE_OBJECT": case "UNHIDE_OBJECT":
                if (!Model(a, out var visibility)) return false;
                visibility.Hidden = command == "HIDE_OBJECT"; visibility.SetEngineTrails(!visibility.Hidden); return true;
            case "TURN_ON_DAMAGE_PARTICLES":
                if (!Model(a, out var damaged) || !Int(a, out int damage) || damage is < 0 or > 4) return false;
                int quadrant = OptionalInt(a, -1);
                _resources.Damage(damaged, Enumerable.Range(0, 4).Select(i => quadrant is < 0 or > 3 || i == quadrant ? damage : 0).ToArray()); return true;
            case "CHANGE_SHIP_COLOR":
                if (!Model(a, out var colored) || !Vector(a, out var first) || !Vector(a, out var second)) return false;
                SetColors(colored, -1, first, second); return true;
            case "MAKE_SHIP_BRONZE": case "MAKE_SHIP_COPPER": case "MAKE_SHIP_CHROME":
                if (!Model(a, out var metal)) return false;
                SetColors(metal, command == "MAKE_SHIP_BRONZE" ? 1 : command == "MAKE_SHIP_COPPER" ? 2 : 0, new(.9f), new(.9f)); return true;
            case "ADD_NAME_DECAL":
                if (!Model(a, out var named) || a.Read() is not { } label || !Vector(a, out var rgb)) return false;
                named.NameDecal = new(label, Encoding.Latin1.GetBytes(label).ToImmutableArray(), rgb);
                named.NameApplyCount++; named.ColorCountAtName = named.ColorApplyCount; return true;
            case "ADD_DECAL":
                if (!Model(a, out var decaled) || !Int(a, out int decal)) return false;
                var bytes = Floats(1, 1, 1, 1);
                decaled.Decals = [new(1, unchecked((uint)decal), bytes), new(2, unchecked((uint)decal), bytes)];
                decaled.DecalApplyCount++; return true;
            default: return false;
        }
    }

    private bool CreateLight(string command, EnbRetailMovieArguments a)
    {
        if (!Int(a, out int slot) || (uint)slot >= 4) return false;
        Lights[slot] = null;
        bool directional = command != "CREATE_POINT_LIGHT";
        var light = new EnbRetailMovieLight { Directional = directional, InScene = directional, Sequence = ++_lightSequence };
        Lights[slot] = light;
        // 805B60 reads the optional source before selecting the from-bone
        // branch. No source retains the constructor's +Z direction.
        if (a.Read() is not { } first) return true;
        if (command == "CREATE_DIRECTIONAL_LIGHT_FROM_BONE")
        {
            var model = _models.FirstOrDefault(m => m.Id == EnbRetailMovieArguments.Integer(first));
            if (model is null || a.Read() is not { } bone || !model.TryBone(bone, out var world)) return false;
            light.Direction = new(world.M31, world.M32, world.M33); return true;
        }
        if (directional)
        {
            if (!Number(a, out float y) || !Number(a, out float z)) return false;
            light.Direction = new(EnbRetailMovieArguments.Number(first), y, z); return true;
        }
        var parent = _models.FirstOrDefault(m => m.Id == EnbRetailMovieArguments.Integer(first));
        if (parent is null || a.Read() is not { } attachment) return false;
        light.Parent = (parent, attachment); return true;
    }

    public void CycleCamera()
    {
        if (_cameras.Count == 0) return;
        var entry = _cameras[_cameraIndex];
        if (entry.H != 0) FieldOfView = new(entry.H, entry.V);
        _cameraParent = (entry.Model, entry.Bone);
        _cameraIndex = (_cameraIndex + 1) % _cameras.Count;
    }

    public void Update(uint deltaMilliseconds)
    {
        foreach (var model in _models) model.Update(deltaMilliseconds);
        if (_cameraParent is { } parent && parent.Model.TryBone(parent.Bone, out var world)) Camera = world;
        Camera = EnbRetailMovieCamera.Transform(Camera, MaxCamera, MouseMovement, Mouse, ref _mouseCache, MouseAngle);
        _resources.Update(deltaMilliseconds, Camera);
        float seconds = deltaMilliseconds * .001f;
        Ambient.Update(seconds); Fog.Update(seconds);
    }
    public void PlaySoundtrack(string name) => _resources.Soundtrack(name);
    public void ExpireEffect(int id) => _resources.ExpireEffect(id);
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _resources.Dispose(); _models.Clear(); _cameras.Clear(); _cameraParent = null; Array.Clear(Lights);
    }
    private bool Model(EnbRetailMovieArguments a, out EnbRetailMovieModel model)
    { model = null!; if (!Int(a, out int id)) return false; model = _models.FirstOrDefault(m => m.Id == id)!; return model is not null; }
    private bool Light(EnbRetailMovieArguments a, out EnbRetailMovieLight light)
    { light = null!; if (!Int(a, out int id) || (uint)id >= 4) return false; light = Lights[id]!; return light is not null; }
    internal static bool Int(EnbRetailMovieArguments a, out int value)
    { string? text = a.Read(); value = text is null ? 0 : EnbRetailMovieArguments.Integer(text); return text is not null; }
    internal static bool Number(EnbRetailMovieArguments a, out float value)
    { string? text = a.Read(); value = text is null ? 0 : EnbRetailMovieArguments.Number(text); return text is not null; }
    internal static int OptionalInt(EnbRetailMovieArguments a, int value) => a.Read() is { } text ? EnbRetailMovieArguments.Integer(text) : value;
    internal static float OptionalNumber(EnbRetailMovieArguments a, float value) => a.Read() is { } text ? EnbRetailMovieArguments.Number(text) : value;
    internal static bool Vector(EnbRetailMovieArguments a, out Vector3 value)
    {
        value = default;
        if (!Number(a, out float x) || !Number(a, out float y) || !Number(a, out float z)) return false;
        value = new(x, y, z); return true;
    }
    private static ImmutableArray<byte> Floats(params float[] values) => values.SelectMany(BitConverter.GetBytes).ToImmutableArray();
    private static void SetColors(EnbRetailMovieModel model, int selector, Vector3 a, Vector3 b)
    {
        var first = new EnbSceneColorizationSnapshot(unchecked((uint)selector), Floats(a.X, a.Y, a.Z));
        var second = new EnbSceneColorizationSnapshot(unchecked((uint)selector), Floats(b.X, b.Y, b.Z));
        model.Colors = [first, second, first, second];
        model.ColorApplyCount++;
    }
}

public sealed class EnbRetailMovieLight
{
    public long Sequence;
    public bool Directional, InScene;
    public float Intensity = 1;
    public Vector3 Ambient = Vector3.One, Diffuse = Vector3.One, Specular = Vector3.One, Direction = Vector3.UnitZ;
    public Vector2 FarAttenuation = new(50, 100);
    public (EnbRetailMovieModel Model, string Bone)? Parent;
}

/// <summary>80A730/80A5A0, including TURN_ON_FOG's unconditional division.</summary>
public sealed class EnbRetailMovieColorTransition
{
    public Vector3 Current { get; private set; }
    public Vector3 Target { get; private set; }
    public Vector3 Rate { get; private set; }
    public void Set(Vector3 target, float seconds, bool immediateBelowThreshold)
    {
        Target = target;
        if (immediateBelowThreshold && seconds < BitConverter.UInt32BitsToSingle(0x38d1b717)) { Current = target; Rate = default; }
        else Rate = (target - Current) / seconds;
    }
    public void Update(float seconds)
    {
        Vector3 value = Current, rate = Rate;
        for (int i = 0; i < 3; i++)
        {
            float step = rate[i] * seconds;
            if (MathF.Abs(step) > MathF.Abs(Target[i] - value[i])) { value[i] = Target[i]; rate[i] = 0; }
            else value[i] += step;
        }
        Current = value; Rate = rate;
    }
}

public static class EnbRetailMovieCamera
{
    // System.Numerics stores the transpose of retail's 3x4 transform.
    public static Matrix4x4 Transform(Matrix4x4 matrix, bool max, bool mouseMovement, Vector2 mouse, ref Vector2 cache, float angle)
    {
        if (!max)
        {
            double radians = BitConverter.Int64BitsToDouble(unchecked((long)0xbff921fb60000000));
            Rotate(ref matrix, 0, 2, radians, false); Rotate(ref matrix, 0, 1, radians, true);
        }
        if (mouseMovement)
        {
            double threshold = BitConverter.Int64BitsToDouble(0x3f1a36e2e0000000);
            if (Math.Abs(mouse.X - cache.X) > threshold || Math.Abs(mouse.Y - cache.Y) > threshold) cache = mouse;
            double horizontal = -(cache.X - .5f), vertical = -(cache.Y - .5f);
            if (max) { horizontal *= angle; vertical *= angle; }
            Rotate(ref matrix, 0, 2, horizontal, false); Rotate(ref matrix, 1, 2, vertical, true);
        }
        return matrix;
    }
    private static void Rotate(ref Matrix4x4 matrix, int a, int b, double angle, bool positive)
    {
        double c = Math.Cos(angle), s = Math.Sin(angle) * (positive ? 1 : -1);
        for (int column = 0; column < 3; column++)
        {
            float x = matrix[a, column], y = matrix[b, column];
            matrix[a, column] = (float)(c * x + s * y); matrix[b, column] = (float)(c * y - s * x);
        }
    }
}
