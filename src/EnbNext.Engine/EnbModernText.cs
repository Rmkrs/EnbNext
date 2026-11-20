using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Engine;

/// <summary>Draws Modern UI text in a clear system font (opt-in UI; the retail screens keep the game's bitmap fonts).</summary>
internal interface IEnbModernTextRenderer
{
    /// <summary>Advance width in pixels of <paramref name="text"/> at <paramref name="pixelSize"/>.</summary>
    float Measure(string text, float pixelSize);
    /// <summary>The text with a dark outline, <see cref="Padding"/> pixels of margin on every side.</summary>
    EnbUiRaster? Render(string text, float pixelSize, EnbRgb color);
    float Padding(float pixelSize);
    /// <summary>Distance in pixels from the centre of the rendered image of <paramref name="text"/> down to the centre
    /// of its drawn glyphs (positive when the glyphs sit below the image centre), so bar text can be centred on its ink.</summary>
    float InkCenterOffset(string text, float pixelSize);
}

/// <summary>The host's Modern UI text renderer; without one, the Modern UI uses the game's fonts.</summary>
internal static class EnbModernText
{
    internal static IEnbModernTextRenderer? Renderer { get; set; }
}
