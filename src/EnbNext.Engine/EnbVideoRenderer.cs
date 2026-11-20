using EnbNext.Assets;
using GlInterface = EnbNext.Rendering.OpenGL.EnbGlApi;
using GlConsts = EnbNext.Rendering.OpenGL.EnbGlConstants;

namespace EnbNext.Engine;

internal interface IEnbVideoFont : IDisposable
{
    (EnbUiRaster Raster, int MeasuredWidth) Build(string? text, uint color);
}

internal sealed class EnbVideoRenderer(Func<IEnbVideoFont> createFont)
{
    internal event Action? FrameRequested;
    private sealed record State(EnbRetailVideoFrame? Frame, EnbRetailVideoSubtitle? Subtitle);
    private State _state = new(null, null);
    private readonly List<EnbOpenGlBackdropRenderer> _renderers = [];
    private readonly EnbOpenGlBackdropRenderer _textRenderer = new();
    private IEnbVideoFont? _font;
    private EnbRetailVideoSubtitle? _builtSubtitle;
    private EnbUiRaster? _text;
    private int _maximumTexture;
    private System.Numerics.Vector4 _textBounds;
    private byte[]? _source;
    private (EnbRetailVideoTile Tile, byte[] Pixels)[] _tiles = [];
    internal int FramesDrawn { get; private set; }
    internal int LastGlError { get; private set; }
    internal string? Failure { get; private set; }
    internal string? SubtitleFailure { get; private set; }
    internal int TextureCount => _renderers.Count;
    internal Action? Rendered { get; set; }

    internal void SetFrame(EnbRetailVideoFrame? frame, EnbRetailVideoSubtitle? subtitle)
    { Volatile.Write(ref _state, new(frame, subtitle)); FrameRequested?.Invoke(); }
    internal void InitializeGl(GlInterface gl)
    {
        _textRenderer.Initialize(gl);
        gl.GetIntegerv(0x0d33, out _maximumTexture);
    }
    private GlInterface? _frameQueueContext;
    internal void RenderGl(GlInterface gl, int fb, int width, int height)
    {
        _frameQueueContext = gl;
        try
        {
            using var frameQueue = EnbOpenGlFrameQueue.Enter(gl);
            gl.BindFramebuffer(GlConsts.GL_FRAMEBUFFER, fb); gl.Viewport(0, 0, width, height);
            gl.ClearColor(0, 0, 0, 1); gl.Clear(GlConsts.GL_COLOR_BUFFER_BIT);
            var state = Volatile.Read(ref _state);
            if (state.Frame is { Rgb565: { } pixels } frame)
            {
                if (!ReferenceEquals(_source, pixels))
                {
                    var tiles = EnbRetailVideoTiles.Create(frame.Width, frame.Height, _maximumTexture, _maximumTexture);
                    _tiles = tiles.Select(t => (t, EnbRetailVideoTiles.Rgb565(t, pixels, frame.Width))).ToArray();
                    _source = pixels;
                    while (_renderers.Count < _tiles.Length) { var renderer = new EnbOpenGlBackdropRenderer(); renderer.Initialize(gl); _renderers.Add(renderer); }
                    while (_renderers.Count > _tiles.Length) { _renderers[^1].Deinitialize(gl); _renderers.RemoveAt(_renderers.Count - 1); }
                }
                for (int i = 0; i < _tiles.Length; i++) _renderers[i].RenderVideoTile(gl, fb, width, height, _tiles[i].Pixels, _tiles[i].Tile);
                FramesDrawn++;
            }
            else
            {
                foreach (var renderer in _renderers) renderer.Deinitialize(gl);
                _renderers.Clear(); _tiles = []; _source = null;
            }
            if (!ReferenceEquals(_builtSubtitle, state.Subtitle))
            {
                _builtSubtitle = state.Subtitle; _text = null;
                if (state.Subtitle is { } subtitle)
                {
                    try
                    {
                        _font ??= createFont();
                        int measured;
                        (_text, measured) = _font.Build(subtitle.Text, subtitle.Color);
                        if (_text.Width > _maximumTexture || _text.Height > _maximumTexture)
                            throw new InvalidDataException("Subtitle exceeds the GPU texture limit.");
                        int x = subtitle.Alignment == 0 ? 0 : subtitle.Alignment == 1 ? width - measured : (width - measured) / 2;
                        int y = height / 16 * subtitle.Row;
                        // A17160 builds once; 970F70 stores normalized vertices.
                        // Retain this placement through resize and GPU recreation.
                        _textBounds = new(x / (float)width, y / (float)height,
                            (x + _text.Width) / (float)width, (y + _text.Height) / (float)height);
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or OverflowException)
                    { SubtitleFailure = ex.Message; _text = null; }
                }
            }
            if (_text is { } raster)
                _textRenderer.RenderVideoSubtitle(gl, fb, width, height, raster, _textBounds);
            LastGlError = gl.GetError();
            if (LastGlError != 0) Failure = "Video OpenGL error " + LastGlError;
            Rendered?.Invoke();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or InvalidOperationException or OverflowException)
        { Failure = ex.ToString(); }
    }
    internal void ContextLost() => ReleaseGpuContext(null);
    internal void DeinitializeGl(GlInterface gl) => ReleaseGpuContext(gl);
    private void ReleaseGpuContext(GlInterface? gl)
    {
        if (_frameQueueContext is { } queueContext) EnbOpenGlFrameQueue.Release(queueContext, gl is null);
        _frameQueueContext = null;
        foreach (var renderer in _renderers) renderer.Deinitialize(gl);
        _renderers.Clear(); _textRenderer.Deinitialize(gl); _tiles = []; _source = null;
        _font?.Dispose(); _font = null;
    }
}
