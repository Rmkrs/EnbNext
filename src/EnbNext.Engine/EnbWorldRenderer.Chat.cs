using System.Collections.Immutable;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private readonly List<EnbOpenGlBackdropRenderer> _chatRenderers = [];
    public void SetChatPresentation(ImmutableArray<EnbUiTextDraw> images)
    {
        Volatile.Write(ref _chatFrame, new(images));
        RequestFrame();
    }
    private sealed record ChatFrame(ImmutableArray<EnbUiTextDraw> Images);
    private ChatFrame _chatFrame = new([]);
    private void RenderChat(EnbGlApi gl, int fb, int width, int height, double scale)
    {
        using var chatScope=FullClientBenchmarkMeter.Enter("hud-chat-draw");
        if(UiLayout?.Visible(EnbUiGroup.Chat)==false)return;var images = Volatile.Read(ref _chatFrame).Images;
        while (_chatRenderers.Count < images.Length) _chatRenderers.Add(new());
        for (int i = 0; i < images.Length; i++)
        {
            var image = images[i]; var r = LayoutRect(EnbNext.Presentation.EnbUiGroup.Chat,image.Bounds);
            var renderer = _chatRenderers[i];
            if (!renderer.IsInitialized) renderer.Initialize(gl);
            if (LayoutRect(EnbNext.Presentation.EnbUiGroup.Chat,image.Clip) is { } clip)
            {
                gl.Enable(0x0C11);
                HudScissor(gl)(
                    (int)Math.Ceiling(clip.X * scale), height - (int)Math.Floor(clip.Bottom * scale),
                    Math.Max(0, (int)Math.Floor(clip.Right * scale) - (int)Math.Ceiling(clip.X * scale)),
                    Math.Max(0, (int)Math.Floor(clip.Bottom * scale) - (int)Math.Ceiling(clip.Y * scale)));
            }
            try { renderer.RenderOverlay(gl, fb, (int)Math.Round(r.X * scale), height - (int)Math.Round(r.Bottom * scale),
                Math.Max(1, (int)Math.Round(r.Width * scale)), Math.Max(1, (int)Math.Round(r.Height * scale)), image.Raster, opacity: image.Opacity, verticalScale: image.VerticalScale); }
            finally { if (image.Clip is not null) gl.Disable(0x0C11); }
        }
    }
    private void DestroyChat(EnbGlApi? gl)
    {
        foreach (var renderer in _chatRenderers) renderer.Deinitialize(gl);
        _chatRenderers.Clear();
    }
}
