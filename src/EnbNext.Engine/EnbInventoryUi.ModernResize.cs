using EnbNext.Presentation;
using Rect = EnbNext.Presentation.EnbUiRect;

namespace EnbNext.Engine;

/// <summary>
/// Next Modern UI windows the player sizes (the wiki window, the item catalogue): a grip at the bottom right corner
/// resizes the window while its top left corner stays; the size is kept, in Modern UI units.
/// </summary>
internal sealed partial class EnbInventoryUi
{
    private string? _modernResizeWindow;
    private EnbUiPoint _modernResizeStart, _modernResizeOrigin;

    private static bool IsModernResizeGrip(string? id) => id is "mwiki/resize" or "mitems/resize";

    /// <summary>A window's size in units (a default when none is kept), within its smallest size and the screen.</summary>
    private EnbUiPoint ModernSized(EnbUiPoint? kept, EnbUiPoint fallback, EnbUiPoint minimum)
    {
        double u = ModernUnit;
        double maxWidth = Math.Max(minimum.X, _size.Width / u - 8), maxHeight = Math.Max(minimum.Y, _size.Height / u - 8);
        var size = kept ?? fallback;
        return new(Math.Clamp(size.X, minimum.X, maxWidth), Math.Clamp(size.Y, minimum.Y, maxHeight));
    }

    /// <summary>The grip in the window's bottom right corner.</summary>
    private void ModernResizeGrip(string id, Rect window)
    {
        double u = ModernUnit;
        int pixels = (int)Math.Round(14 * u * _scale);
        var grip = new Rect(Snap(window.Right - pixels / _scale - 2 * u), Snap(window.Bottom - pixels / _scale - 2 * u), pixels / _scale, pixels / _scale);
        Hit(id, FromPixels(grip));
        _images.Add(new(EnbModernIcons.Grip(pixels, _hover == id || _pressed == id), grip));
        _tips[id] = "Drag to resize the window";
    }

    private void BeginModernResize(string id, EnbUiPoint point)
    {
        string window = id[..id.IndexOf('/')];
        var bounds = ModernWindowBounds(window);
        double u = ModernUnit;
        _modernResizeWindow = window; _modernResizeStart = point; _modernResizeOrigin = new(bounds.Width / u, bounds.Height / u);
        // The top left corner stays where it is while the grip moves.
        if (_size.Width <= 0 || _size.Height <= 0) return;
        var at = new EnbUiPoint(bounds.X / _size.Width, bounds.Y / _size.Height);
        if (window == "mwiki") ModernWikiPosition = at; else ModernItemsPosition = at;
    }

    private void ResizeModernWindow(EnbUiPoint point)
    {
        double u = ModernUnit;
        var size = new EnbUiPoint(_modernResizeOrigin.X + (point.X - _modernResizeStart.X) / u, _modernResizeOrigin.Y + (point.Y - _modernResizeStart.Y) / u);
        var bounds = ModernWindowBounds(_modernResizeWindow);
        // Never past the screen's edge from where the window stands.
        size = new(Math.Min(size.X, (_size.Width - bounds.X) / u), Math.Min(size.Y, (_size.Height - bounds.Y) / u));
        if (_modernResizeWindow == "mwiki") ModernWikiSize = ModernSized(size, size, WikiMinimumSize);
        else if (_modernResizeWindow == "mitems") ModernItemsSize = ModernSized(size, size, ItemsMinimumSize);
    }

    private void EndModernResize()
    {
        if (_modernResizeWindow == "mwiki")
        {
            if (ModernWikiSize is { } size) ModernWikiResized?.Invoke(size);
            if (ModernWikiPosition is { } at) ModernWikiMoved?.Invoke(at);
        }
        else if (_modernResizeWindow == "mitems")
        {
            if (ModernItemsSize is { } size) ModernItemsResized?.Invoke(size);
            if (ModernItemsPosition is { } at) ModernItemsMoved?.Invoke(at);
        }
        _modernResizeWindow = null;
    }
}
