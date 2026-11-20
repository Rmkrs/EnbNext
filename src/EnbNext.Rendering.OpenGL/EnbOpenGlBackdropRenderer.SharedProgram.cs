namespace EnbNext.Rendering.OpenGL;

public sealed partial class EnbOpenGlBackdropRenderer
{
    /// <summary>One compiled program, shared by every overlay and map quad renderer of a context with the same shader source.</summary>
    private sealed class SharedProgram(GlInterface gl, string vertex, string fragment, int program)
    {
        public readonly GlInterface Gl = gl;
        public readonly string Vertex = vertex, Fragment = fragment;
        public readonly int Program = program;
        public int Users;
    }

    private static readonly List<SharedProgram> SharedPrograms = [];
    private SharedProgram? _sharedProgram;

    /// <summary>
    /// Overlay and map quad draws set every uniform their program reads on each draw (texture unit, opacity, brightness,
    /// the additive switch, the native-RGBA switch: RenderOverlay, RenderMapQuad and both video paths), so one program
    /// serves them all; the galaxy map alone draws hundreds. Dazzle and map texture modes keep their own program.
    /// </summary>
    private bool SharesProgram => !_dazzleMode && !_mapTextureMode;

    private static SharedProgram? FindSharedProgram(GlInterface gl, string vertex, string fragment)
    {
        lock (SharedPrograms)
            return SharedPrograms.Find(p => ReferenceEquals(p.Gl, gl) && p.Vertex == vertex && p.Fragment == fragment);
    }

    private static SharedProgram AddSharedProgram(GlInterface gl, string vertex, string fragment, int program)
    {
        var shared = new SharedProgram(gl, vertex, fragment, program);
        lock (SharedPrograms) SharedPrograms.Add(shared);
        return shared;
    }

    /// <summary>
    /// Lets go of this renderer's program; true when the caller deletes it (its own, or the last user of a shared one).
    /// A lost context (no GL) has already retired the program, so a shared one is forgotten for every user.
    /// </summary>
    private bool ReleaseProgram(GlInterface? gl)
    {
        if (_sharedProgram is not { } shared) return true;
        _sharedProgram = null;
        lock (SharedPrograms)
        {
            if (gl is null) { SharedPrograms.Remove(shared); return false; }
            if (--shared.Users > 0) return false;
            SharedPrograms.Remove(shared);
            return true;
        }
    }
}
