using System.Numerics;
using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>The scene owner performs command mutations in the same order as the retail handler.</summary>
public interface IEnbRetailMovieScene : IDisposable
{
    bool Execute(string command, EnbRetailMovieArguments arguments, bool preload, uint milliseconds);
    void Update(uint deltaMilliseconds);
    void PlaySoundtrack(string name);
    void ExpireEffect(int id);
    void CycleCamera();
}

/// <summary>802B20 scheduling and 801E60 startup/update ownership. No GPU-context ownership.</summary>
public sealed class EnbRetailMovieRuntime : IDisposable
{
    // Ordered 803310 comparisons, including its attenuation alias.
    public static IReadOnlyList<string> CommandNames { get; } = Array.AsReadOnly(new[]
    {
        "CREATE_OBJECT", "CREATE_SHIP_OBJECT", "CREATE_SFX_ON_OBJECT", "CREATE_SFX_BETWEEN_OBJECTS",
        "CREATE_SFX_BETWEEN_BONES", "CREATE_SFX_BETWEEN_OBJECT_AND_BONE", "CREATE_SFX_ON_BONE",
        "CREATE_STARS", "CREATE_SKYBOX", "CREATE_ORBITAL", "DESTROY_OBJECT", "PLAY_ANIMATION", "LOAD_ANIMATION",
        "ATTACH_CAMERA", "ADD_CAMERA", "ATTACH_TO_BONE", "ATTACH_LIGHT_TO_BONE", "SET_AMBIENT_LIGHT",
        "CREATE_POINT_LIGHT", "CREATE_DIRECTIONAL_LIGHT_FROM_BONE", "CREATE_DIRECTIONAL_LIGHT",
        "SET_LIGHT_INTENSITY", "SET_LIGHT_FAR_ATTENUATION", "SET_LIGHT_ATTENUATION", "SET_LIGHT_AMBIENT_COLOR",
        "SET_LIGHT_SPECULAR_COLOR", "SET_LIGHT_DIFFUSE_COLOR", "SET_CAMERA_VIEW_PLANE", "SHAKE_CAMERA",
        "SET_SCREEN_FADE_COLOR", "SET_SCREEN_FADE_OPACITY", "PLAY_AUDIO", "TURN_ON_MAX_CAMERA", "TURN_OFF_MAX_CAMERA",
        "ENABLE_LETTERBOX", "DISABLE_LETTERBOX", "SET_CLIP_PLANE", "TURN_ON_FOG", "TURN_OFF_FOG", "STOP_AUDIO",
        "TURN_ON_MOUSE_MOVEMENT_OF_CAMERA", "TURN_OFF_MOUSE_MOVEMENT_OF_CAMERA", "TURN_ON_ENGINE_TRAILS",
        "TURN_OFF_ENGINE_TRAILS", "TURN_ON_DAMAGE_PARTICLES", "CHANGE_SHIP_COLOR", "MAKE_SHIP_BRONZE",
        "MAKE_SHIP_COPPER", "MAKE_SHIP_CHROME", "ADD_NAME_DECAL", "ADD_DECAL", "END_MOVIE", "SHOW_CURRENT_FRAME",
        "DONT_SHOW_CURRENT_FRAME", "SKIP_FRAMES", "HIDE_OBJECT", "UNHIDE_OBJECT", "DEBUG_MODE"
    });
    private readonly EnbRetailMovieScript _script;
    private readonly IEnbRetailMovieScene _scene;
    private readonly List<(int Id, uint Deadline)> _effects = [];
    private int _next;
    private bool _disposed, _fadeStarted;
    public uint Milliseconds { get; private set; }
    public uint LastDeltaMilliseconds { get; private set; }
    public uint DisplayFrame => unchecked((Milliseconds - _script.FadeAtMilliseconds) * 30u) / 1000u;
    public bool Started { get; private set; }
    public bool OverlayPublished { get; private set; }
    public bool Ended { get; private set; }
    public bool DebugMode { get; private set; }
    public bool ShowCurrentFrame { get; private set; }
    public bool Paused { get; private set; }
    public int RequestedSkipMilliseconds { get; private set; }
    public bool AllowsDebugInput { get; }
    public EnbRetailMovieCommand? FailedCommand { get; private set; }
    public int RemainingCommands => _script.Commands.Count - _next;
    public EnbRetailMovieOverlay Overlay { get; } = new();

    public EnbRetailMovieRuntime(EnbRetailMovieScript script, IEnbRetailMovieScene scene, bool allowsDebugInput)
    {
        _script = script; _scene = scene; AllowsDebugInput = allowsDebugInput;
        if (script.FadeAtMilliseconds != 0) Overlay.Opacity.Set(1, 0); // 800575..800589
        // 8099B0 preloads effects in source order, before the first frame.
        foreach (var command in script.Commands.OrderBy(c => c.LineNumber))
        {
            if (!command.Text.StartsWith("CREATE_SFX", StringComparison.Ordinal)) continue;
            var args = new EnbRetailMovieArguments(command.Text);
            var name = CommandNames.FirstOrDefault(args.Match);
            if (name is not null) _scene.Execute(name, args, true, 0);
        }
    }

    public void RetireEffectAt(int id, uint deadline) => _effects.Add((id, deadline));

    public void Update(uint deltaMilliseconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Ended) return;
        LastDeltaMilliseconds = deltaMilliseconds;
        if (_script.FadeAtMilliseconds != 0 && !_fadeStarted && Milliseconds >= _script.FadeAtMilliseconds)
        { Overlay.Opacity.Set(0, 1); _fadeStarted = true; }
        Milliseconds = unchecked(Milliseconds + deltaMilliseconds);
        for (int i = 0; i < _effects.Count;)
        {
            if (Milliseconds < _effects[i].Deadline) { i++; continue; }
            _scene.ExpireEffect(_effects[i].Id); _effects.RemoveAt(i);
        }
        // END_MOVIE does not break the native scheduler's current due-command loop.
        while (_next < _script.Commands.Count && _script.Commands[_next].Milliseconds <= Milliseconds)
        {
            var command = _script.Commands[_next];
            if (!Execute(command)) { FailedCommand = command; Ended = true; break; }
            _next++;
        }
        if (!Started)
        {
            if (_script.Soundtrack is { } name) _scene.PlaySoundtrack(name);
            Started = true;
            return;
        }
        _scene.Update(deltaMilliseconds);
        Overlay.Update(deltaMilliseconds);
        OverlayPublished = true;
    }

    private bool Execute(EnbRetailMovieCommand command)
    {
        var args = new EnbRetailMovieArguments(command.Text);
        string? name = CommandNames.FirstOrDefault(args.Match);
        if (name is null) return false;
        switch (name)
        {
            case "END_MOVIE": Ended = true; return true;
            case "SHOW_CURRENT_FRAME": ShowCurrentFrame = true; return true;
            case "DONT_SHOW_CURRENT_FRAME": ShowCurrentFrame = false; return true;
            case "DEBUG_MODE": DebugMode = true; return true;
            case "SKIP_FRAMES":
                if (!DebugMode || !AllowsDebugInput) return true;
                if (args.Read() is not { } frames) return false;
                RequestedSkipMilliseconds = unchecked(RequestedSkipMilliseconds + unchecked(EnbRetailMovieArguments.Integer(frames) * 1000) / 30);
                return true;
            case "SET_SCREEN_FADE_OPACITY":
                if (args.Read() is not { } opacity || args.Read() is not { } duration) return false;
                Overlay.Opacity.Set(EnbRetailMovieArguments.Number(opacity), EnbRetailMovieArguments.Number(duration)); return true;
            case "SET_SCREEN_FADE_COLOR":
                if (args.Read() is not { } r || args.Read() is not { } g || args.Read() is not { } b || args.Read() is not { } seconds) return false;
                Overlay.SetColor(new(EnbRetailMovieArguments.Number(r), EnbRetailMovieArguments.Number(g), EnbRetailMovieArguments.Number(b)), EnbRetailMovieArguments.Number(seconds)); return true;
            case "ENABLE_LETTERBOX": case "DISABLE_LETTERBOX":
                if (args.Read() is not { } time) return false;
                Overlay.Letterbox.Set(name == "ENABLE_LETTERBOX" ? 1 : 0, EnbRetailMovieArguments.Number(time)); return true;
            default: return _scene.Execute(name, args, !Started && name.StartsWith("CREATE_SFX", StringComparison.Ordinal), Milliseconds);
        }
    }

    public void Input(uint key)
    {
        if ((key & 0x10000000) != 0 || _disposed) return;
        switch (key & 0xffff)
        {
            case 0x1b: Ended = true; break;
            case 0x22: _scene.CycleCamera(); break;
            case 0x20 when DebugMode && AllowsDebugInput: Paused = !Paused; break;
            case 0x6b when DebugMode && AllowsDebugInput: RequestedSkipMilliseconds = unchecked(RequestedSkipMilliseconds + 5000); break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; Ended = true; _effects.Clear(); _scene.Dispose();
    }
}

/// <summary>7ECAA0: pause excludes wall time; the movie's skip field is cumulative.</summary>
public sealed class EnbRetailMovieClock
{
    private uint? _previous;
    private bool _wasPaused;
    private int _appliedSkip;
    private int _capturePhase;
    public void Advance(EnbRetailMovieRuntime movie, uint wallMilliseconds, bool capture = false)
    {
        if (!movie.Started) { movie.Update(0); return; }
        if (capture)
        {
            // 7ECB77..7ECBD3: pause holds time; resumed capture advances immediately.
            // The cumulative skip field belongs to the wall-clock branch only.
            uint step = movie.Paused ? 0u : ++_capturePhase == 3 ? 34u : 33u;
            if (_capturePhase == 3) _capturePhase = 0;
            movie.Update(step);
            return;
        }
        uint elapsed = _previous is { } previous ? unchecked(wallMilliseconds - previous) : 0;
        _previous = wallMilliseconds;
        if (movie.Paused || _wasPaused) elapsed = 0;
        _wasPaused = movie.Paused;
        if (!movie.Paused)
        {
            elapsed = unchecked(elapsed + (uint)(movie.RequestedSkipMilliseconds - _appliedSkip));
            _appliedSkip = movie.RequestedSkipMilliseconds;
        }
        // Paused frames still visit the movie, meshes, camera and overlay with zero delta.
        movie.Update(elapsed);
    }
}

public sealed class EnbRetailMovieTransition
{
    public float Current { get; private set; }
    public float Target { get; private set; }
    public float Rate { get; private set; }
    public void Set(float target, float seconds)
    {
        Target = target;
        if (seconds < BitConverter.UInt32BitsToSingle(0x38d1b717)) { Current = target; Rate = 0; }
        else Rate = (target - Current) / seconds;
    }
    public void Update(float seconds)
    {
        float step = Rate * seconds;
        Current = MathF.Abs(step) <= MathF.Abs(Target - Current) ? Current + step : Target;
    }
}

/// <summary>7FF3D0/7FF830/7FFA30/7FFBE0: global overlay values owned by the admitted movie session.</summary>
public sealed class EnbRetailMovieOverlay
{
    public EnbRetailMovieTransition Red { get; } = new();
    public EnbRetailMovieTransition Green { get; } = new();
    public EnbRetailMovieTransition Blue { get; } = new();
    public EnbRetailMovieTransition Opacity { get; } = new();
    public EnbRetailMovieTransition Letterbox { get; } = new();
    public Vector4 Color => new(Red.Current, Green.Current, Blue.Current, Opacity.Current);
    public float BarFraction => Letterbox.Current * .125f;
    public void SetColor(Vector3 color, float seconds)
    {
        Red.Set(Clamp(color.X), seconds); Green.Set(Clamp(color.Y), seconds); Blue.Set(Clamp(color.Z), seconds);
    }
    private static float Clamp(float value) => value >= 0 ? value > 1 ? 1 : value : 0;
    public void Update(uint deltaMilliseconds)
    {
        float seconds = deltaMilliseconds * .001f;
        Letterbox.Update(seconds); Opacity.Update(seconds); Red.Update(seconds); Green.Update(seconds); Blue.Update(seconds);
    }
}
