using EnbNext.Presentation;
using Rect = EnbNext.Presentation.EnbUiRect;

namespace EnbNext.Engine;

/// <summary>
/// Next Modern UI scroll bar for a list or text that is longer than its window: a slim rounded track at the right
/// edge with a thumb whose size and place show how much is visible and where. The thumb can be dragged, a click on
/// the track turns a page, and the wheel keeps scrolling as before.
/// </summary>
internal sealed partial class EnbInventoryUi
{
    private readonly Dictionary<string, (Rect Track, int Total, int Visible, int First, Action<int> Set)> _modernScrollbars = [];
    private double _modernScrollGrab;

    /// <summary>The room a scroll bar takes at the right edge of its area (keep the text clear of it).</summary>
    private double ModernScrollbarRoom => Math.Round(14 * ModernUnit);

    /// <summary>A scroll bar along the right edge of <paramref name="area"/>; nothing when everything is visible.</summary>
    private void ModernScrollbar(string id, Rect area, int total, int visible, int first, Action<int> set)
    {
        if (total <= visible || visible <= 0) { _modernScrollbars.Remove(id); return; }
        double u = ModernUnit, width = Math.Max(4, Math.Round(8 * u * _scale)) / _scale, inset = Math.Round(3 * u);
        var track = new Rect(Snap(area.Right - inset - width), Snap(area.Y + inset), width, Math.Round((area.Height - inset * 2) * _scale) / _scale);
        int max = total - visible;
        first = Math.Clamp(first, 0, max);
        double thumbHeight = Math.Round(Math.Max(20 * u, track.Height * visible / total) * _scale) / _scale;
        var thumb = new Rect(track.X, Snap(track.Y + (track.Height - thumbHeight) * first / max), width, thumbHeight);
        _modernScrollbars[id] = (track, total, visible, first, set);
        // The hit areas are a little wider than the bar, so it is easy to take.
        Hit(id + "/scrollTrack", FromPixels(new(track.X - inset, track.Y, width + inset * 2, track.Height)));
        Hit(id + "/scrollThumb", FromPixels(new(thumb.X - inset, thumb.Y, width + inset * 2, thumb.Height)));
        bool hot = _hover == id + "/scrollThumb" || _pressed == id + "/scrollThumb";
        int pixels = (int)Math.Round(width * _scale);
        _images.Add(new(EnbModernIcons.Pill(pixels, (int)Math.Round(track.Height * _scale), new(0, 0, 0), 120), track));
        _images.Add(new(EnbModernIcons.Pill(pixels, (int)Math.Round(thumbHeight * _scale), hot ? new(.72f, .84f, 1) : new(.48f, .6f, .9f), 235), thumb));
    }

    private static string? ModernScrollOf(string? id, string part)
        => id is not null && id.EndsWith(part, StringComparison.Ordinal) ? id[..^part.Length] : null;

    private double ModernScrollThumbHeight((Rect Track, int Total, int Visible, int First, Action<int> Set) bar)
        => Math.Round(Math.Max(20 * ModernUnit, bar.Track.Height * bar.Visible / bar.Total) * _scale) / _scale;
    private double ModernScrollThumbTop((Rect Track, int Total, int Visible, int First, Action<int> Set) bar)
        => bar.Track.Y + (bar.Track.Height - ModernScrollThumbHeight(bar)) * bar.First / Math.Max(1, bar.Total - bar.Visible);

    /// <summary>A press on a thumb: where on it the pointer took it.</summary>
    private void BeginModernScroll(string id, EnbUiPoint point)
    {
        if (ModernScrollOf(id, "/scrollThumb") is { } key && _modernScrollbars.TryGetValue(key, out var bar))
            _modernScrollGrab = point.Y - ModernScrollThumbTop(bar);
    }

    /// <summary>Dragging a thumb: the list follows it.</summary>
    private bool DragModernScroll(EnbUiPoint point)
    {
        if (ModernScrollOf(_pressed, "/scrollThumb") is not { } key || !_modernScrollbars.TryGetValue(key, out var bar)) return false;
        double room = bar.Track.Height - ModernScrollThumbHeight(bar);
        int max = bar.Total - bar.Visible;
        int first = room <= 0 ? 0 : (int)Math.Round(Math.Clamp((point.Y - _modernScrollGrab - bar.Track.Y) / room, 0, 1) * max);
        if (first != bar.First) { bar.Set(first); Publish(); }
        return true;
    }

    /// <summary>A click on the track turns a page towards it; the thumb itself does nothing on release.</summary>
    private bool ActivateModernScroll(string id)
    {
        if (ModernScrollOf(id, "/scrollThumb") is not null) return true;
        if (ModernScrollOf(id, "/scrollTrack") is not { } key) return false;
        if (_modernScrollbars.TryGetValue(key, out var bar))
        {
            double top = ModernScrollThumbTop(bar), page = Math.Max(1, bar.Visible - 1);
            int first = _point.Y < top ? bar.First - (int)page : _point.Y > top + ModernScrollThumbHeight(bar) ? bar.First + (int)page : bar.First;
            bar.Set(Math.Clamp(first, 0, bar.Total - bar.Visible));
            InteractionSoundRequested?.Invoke("slider_bar_1.wav");
        }
        return true;
    }
}
