
namespace EnbNext.Rendering.OpenGL;

public sealed partial class EnbOpenGlStage0TextureBackend
{
    // State ownership is limited to an explicitly delimited mesh pass.
    // No cross-frame bindings, draw reordering or deferred resource updates.
    // Call InterruptRetainedSubmission before external GL work.
    private readonly EnbW3dMaterialApplicationState _materialApplication = new();
    private bool _materialScopeActive;
    private GlInterface? _retainedSubmissionGl;
    private bool _retainedDrawPending;
    private bool _retainedLastVao;
    private (int Framebuffer, int Width, int Height, int Inset)? _retainedTarget;
    private bool _retainedProgram;
    private double? _retainedDepthRange;
    private int? _retainedActiveTexture, _retainedTexture0, _retainedTexture1, _retainedVao;
    private (int Depth, bool Write, int Cull)? _retainedRaster;
    private (bool Enabled, int Source, int Destination, bool Coverage)? _retainedBlend;
    private long _retainedRequests, _retainedSkips, _retainedBoundaries, _retainedDraws;

    public string RetainedSubmissionTiming =>
        $" retainedSubmit(draws={_retainedDraws},stateRequests={_retainedRequests},stateSkips={_retainedSkips},boundaries={_retainedBoundaries},materialApply={_materialApplication.ApplicationCount},materialReuse={_materialApplication.ReuseCount})";

    public IDisposable? BeginRetainedSubmission(GlInterface gl, bool enabled)
    {
        if (_materialScopeActive)
            throw new InvalidOperationException("Retained submission scopes cannot nest.");
        // Disabling physical GL reuse must not change native mapper apply order.
        _materialScopeActive = true;
        _retainedSubmissionGl = enabled ? gl : null;
        ForgetRetainedState();
        return new RetainedSubmissionScope(this);
    }

    private sealed class RetainedSubmissionScope(EnbOpenGlStage0TextureBackend backend) : IDisposable
    {
        private EnbOpenGlStage0TextureBackend? _backend = backend;
        public void Dispose()
        {
            if (_backend is not {} value) return;
            _backend = null;
            try { value.InterruptRetainedSubmission(); }
            finally { value._retainedSubmissionGl = null;value._materialScopeActive=false;value._materialApplication.Clear(); }
        }
    }

    public void InterruptRetainedSubmission(bool materialChanged = true)
    {
        if (materialChanged) _materialApplication.Clear();
        if (_retainedSubmissionGl is not {} gl) return;
        if (_retainedDrawPending)
        {
            CleanupSubmissionBindings(gl, _retainedLastVao);
            _retainedDrawPending = false;
            _retainedBoundaries++;
            int error = gl.GetError();
            if (error != GlConsts.GL_NO_ERROR)
                throw new InvalidOperationException($"Retained submission boundary GL error 0x{error:X4}.");
        }
        ForgetRetainedState(materialChanged);
    }

    private void ForgetRetainedState(bool materialChanged = true)
    {
        if (materialChanged) _materialApplication.Clear();
        _retainedTarget = null;
        _retainedDepthRange = null;
        _retainedProgram = false;
        _retainedActiveTexture = _retainedTexture0 = _retainedTexture1 = _retainedVao = null;
        _retainedRaster = null;
        _retainedBlend = null;
    }

    private bool ReuseRetainedState<T>(ref T? previous, T requested) where T : struct, IEquatable<T>
    {
        if (_retainedSubmissionGl is null) return false;
        _retainedRequests++;
        if (previous is {} value && value.Equals(requested))
        {
            _retainedSkips++;
            return true;
        }
        previous = requested;
        return false;
    }

    private void ActivateSubmissionTexture(int unit)
    {
        if (!ReuseRetainedState(ref _retainedActiveTexture, unit))
            RequireProc(_activeTexture, "glActiveTexture")(unit);
    }

    private void BindSubmissionTexture(int unit, int texture)
    {
        ref int? previous = ref (unit == GlTexture0 ? ref _retainedTexture0 : ref _retainedTexture1);
        if (!ReuseRetainedState(ref previous, texture))
        {
            ActivateSubmissionTexture(unit);
            RequireProc(_bindTexture, "glBindTexture")(GlTexture2D, texture);
        }
    }

    private void CleanupSubmissionBindings(GlInterface gl, bool usedVao)
    {
        RequireProc(_activeTexture, "glActiveTexture")(GlTexture1);
        RequireProc(_bindTexture, "glBindTexture")(GlTexture2D, 0);
        RequireProc(_activeTexture, "glActiveTexture")(GlTexture0);
        RequireProc(_bindTexture, "glBindTexture")(GlTexture2D, 0);
        if (usedVao) gl.BindVertexArray(0);
        else
        {
            gl.BindBuffer(GlConsts.GL_ELEMENT_ARRAY_BUFFER, 0);
            gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, 0);
        }
        gl.UseProgram(0);
    }
}
